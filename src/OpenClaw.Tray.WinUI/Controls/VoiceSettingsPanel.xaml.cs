using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Services;
using OpenClawTray.Services.Voice;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace OpenClawTray.Controls;

public sealed partial class VoiceSettingsPanel : UserControl
{
    private SettingsManager? _settings;
    private VoiceService? _voiceService;
    private VoiceProviderCredentials _voiceProviderCredentialsDraft = new();
    private string _activeTtsProviderId = VoiceProviderIds.Windows;
    private bool _updatingVoiceProviderFields;
    private List<ProviderOption> _speechToTextOptions = new();
    private List<ProviderOption> _textToSpeechOptions = new();
    private List<DeviceOption> _inputOptions = new();
    private List<DeviceOption> _outputOptions = new();

    public VoiceSettingsPanel()
    {
        InitializeComponent();
    }

    public void Initialize(SettingsManager settings, VoiceService voiceService)
    {
        _settings = settings;
        _voiceService = voiceService;

        LoadVoiceSettings();
        _ = LoadVoiceDevicesAsync();
    }

    public void ApplyTo(SettingsManager settings)
    {
        CaptureSelectedVoiceProviderSettings();

        settings.Voice = new VoiceSettings
        {
            Mode = GetSelectedVoiceMode(),
            Enabled = GetSelectedVoiceMode() != VoiceActivationMode.Off,
            ShowConversationToasts = VoiceConversationToastsCheckBox.IsChecked ?? false,
            SpeechToTextProviderId = (VoiceSpeechToTextProviderComboBox.SelectedItem as ProviderOption)?.Id ?? VoiceProviderIds.Windows,
            TextToSpeechProviderId = (VoiceTextToSpeechProviderComboBox.SelectedItem as ProviderOption)?.Id ?? VoiceProviderIds.Windows,
            InputDeviceId = (VoiceInputDeviceComboBox.SelectedItem as DeviceOption)?.DeviceId,
            OutputDeviceId = (VoiceOutputDeviceComboBox.SelectedItem as DeviceOption)?.DeviceId,
            SampleRateHz = settings.Voice.SampleRateHz,
            CaptureChunkMs = settings.Voice.CaptureChunkMs,
            BargeInEnabled = settings.Voice.BargeInEnabled,
            WakeWord = new VoiceWakeWordSettings
            {
                Engine = settings.Voice.WakeWord.Engine,
                ModelId = settings.Voice.WakeWord.ModelId,
                TriggerThreshold = settings.Voice.WakeWord.TriggerThreshold,
                TriggerCooldownMs = settings.Voice.WakeWord.TriggerCooldownMs,
                PreRollMs = settings.Voice.WakeWord.PreRollMs,
                EndSilenceMs = settings.Voice.WakeWord.EndSilenceMs
            },
            AlwaysOn = new VoiceAlwaysOnSettings
            {
                MinSpeechMs = settings.Voice.AlwaysOn.MinSpeechMs,
                EndSilenceMs = settings.Voice.AlwaysOn.EndSilenceMs,
                MaxUtteranceMs = settings.Voice.AlwaysOn.MaxUtteranceMs,
                ChatWindowSubmitMode = GetSelectedChatWindowSubmitMode()
            }
        };
        settings.VoiceProviderCredentials = Clone(_voiceProviderCredentialsDraft);
    }

    private void LoadVoiceSettings()
    {
        if (_settings == null || _voiceService == null)
        {
            return;
        }

        _voiceProviderCredentialsDraft = Clone(_settings.VoiceProviderCredentials);
        LoadVoiceProviders();
        SelectVoiceMode(_settings.Voice.Mode);
        SelectChatWindowSubmitMode(_settings.Voice.AlwaysOn.ChatWindowSubmitMode);
        VoiceConversationToastsCheckBox.IsChecked = _settings.Voice.ShowConversationToasts;
        UpdateVoiceProviderSettingsEditor();
        UpdateVoiceSettingsInfo();
    }

    private void LoadVoiceProviders()
    {
        var catalog = _voiceService!.GetProviderCatalog();

        _speechToTextOptions = catalog.SpeechToTextProviders
            .Select(p => new ProviderOption(p.Id, p.Name, p.Runtime, p.Description))
            .ToList();
        _textToSpeechOptions = catalog.TextToSpeechProviders
            .Select(p => new ProviderOption(p.Id, p.Name, p.Runtime, p.Description))
            .ToList();

        VoiceSpeechToTextProviderComboBox.ItemsSource = _speechToTextOptions;
        VoiceTextToSpeechProviderComboBox.ItemsSource = _textToSpeechOptions;

        VoiceSpeechToTextProviderComboBox.SelectedItem =
            _speechToTextOptions.FirstOrDefault(p => p.Id == _settings!.Voice.SpeechToTextProviderId)
            ?? _speechToTextOptions.FirstOrDefault();
        VoiceTextToSpeechProviderComboBox.SelectedItem =
            _textToSpeechOptions.FirstOrDefault(p => p.Id == _settings!.Voice.TextToSpeechProviderId)
            ?? _textToSpeechOptions.FirstOrDefault();
    }

    private async Task LoadVoiceDevicesAsync()
    {
        if (_settings == null || _voiceService == null)
        {
            return;
        }

        try
        {
            VoiceSettingsInfoTextBlock.Text = "Loading voice devices...";
            var devices = await _voiceService.ListDevicesAsync();

            _inputOptions =
            [
                new DeviceOption(null, "System default microphone")
            ];
            _inputOptions.AddRange(devices
                .Where(d => d.IsInput)
                .Select(d => new DeviceOption(d.DeviceId, d.Name)));

            _outputOptions =
            [
                new DeviceOption(null, "System default speaker")
            ];
            _outputOptions.AddRange(devices
                .Where(d => d.IsOutput)
                .Select(d => new DeviceOption(d.DeviceId, d.Name)));

            VoiceInputDeviceComboBox.ItemsSource = _inputOptions;
            VoiceOutputDeviceComboBox.ItemsSource = _outputOptions;

            VoiceInputDeviceComboBox.SelectedItem = _inputOptions.FirstOrDefault(o => o.DeviceId == _settings.Voice.InputDeviceId) ?? _inputOptions[0];
            VoiceOutputDeviceComboBox.SelectedItem = _outputOptions.FirstOrDefault(o => o.DeviceId == _settings.Voice.OutputDeviceId) ?? _outputOptions[0];

            UpdateVoiceSettingsInfo();
        }
        catch (Exception ex)
        {
            VoiceSettingsInfoTextBlock.Text = $"Failed to load voice devices: {ex.Message}";
        }
    }

    private void SelectVoiceMode(VoiceActivationMode mode)
    {
        var target = mode switch
        {
            VoiceActivationMode.WakeWord => "WakeWord",
            VoiceActivationMode.AlwaysOn => "AlwaysOn",
            _ => "Off"
        };

        foreach (var item in VoiceModeComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), target, StringComparison.Ordinal))
            {
                VoiceModeComboBox.SelectedItem = item;
                return;
            }
        }

        VoiceModeComboBox.SelectedIndex = 0;
    }

    private VoiceActivationMode GetSelectedVoiceMode()
    {
        var tag = (VoiceModeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return tag switch
        {
            "WakeWord" => VoiceActivationMode.WakeWord,
            "AlwaysOn" => VoiceActivationMode.AlwaysOn,
            _ => VoiceActivationMode.Off
        };
    }

    private void SelectChatWindowSubmitMode(VoiceChatWindowSubmitMode mode)
    {
        var target = mode == VoiceChatWindowSubmitMode.WaitForUser ? "WaitForUser" : "AutoSend";

        foreach (var item in VoiceChatWindowSubmitModeComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), target, StringComparison.Ordinal))
            {
                VoiceChatWindowSubmitModeComboBox.SelectedItem = item;
                return;
            }
        }

        VoiceChatWindowSubmitModeComboBox.SelectedIndex = 0;
    }

    private VoiceChatWindowSubmitMode GetSelectedChatWindowSubmitMode()
    {
        var tag = (VoiceChatWindowSubmitModeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return tag == "WaitForUser"
            ? VoiceChatWindowSubmitMode.WaitForUser
            : VoiceChatWindowSubmitMode.AutoSend;
    }

    private void UpdateVoiceSettingsInfo()
    {
        var stt = (VoiceSpeechToTextProviderComboBox.SelectedItem as ProviderOption)?.Name ?? "Windows Speech Recognition";
        var tts = (VoiceTextToSpeechProviderComboBox.SelectedItem as ProviderOption)?.Name ?? "Windows Speech Synthesis";
        var input = (VoiceInputDeviceComboBox.SelectedItem as DeviceOption)?.Name ?? "System default microphone";
        var output = (VoiceOutputDeviceComboBox.SelectedItem as DeviceOption)?.Name ?? "System default speaker";
        var fallbackNotice = string.Empty;

        if (VoiceTextToSpeechProviderComboBox.SelectedItem is ProviderOption ttsOption &&
            !VoiceProviderCatalogService.SupportsTextToSpeechRuntime(ttsOption.Id))
        {
            fallbackNotice = " Unsupported TTS providers will fall back to Windows until their runtime adapters are added.";
        }

        VoiceSettingsInfoTextBlock.Text =
            $"Mode: {VoiceDisplayHelper.GetModeLabel(GetSelectedVoiceMode())}. STT: {stt}. TTS: {tts}. Listen: {input}. Talk: {output}.{fallbackNotice}";
    }

    private void UpdateVoiceProviderSettingsEditor()
    {
        var providerId = GetSelectedTextToSpeechProviderId();
        var showProviderSettings = !string.Equals(providerId, VoiceProviderIds.Windows, StringComparison.OrdinalIgnoreCase);

        VoiceTtsProviderSettingsPanel.Visibility = showProviderSettings ? Visibility.Visible : Visibility.Collapsed;
        if (!showProviderSettings)
        {
            _activeTtsProviderId = VoiceProviderIds.Windows;
            return;
        }

        _updatingVoiceProviderFields = true;
        try
        {
            VoiceTtsProviderSettingsTitleTextBlock.Text = $"{GetSelectedTextToSpeechProviderName().ToUpperInvariant()} SETTINGS";
            VoiceTtsApiKeyPasswordBox.Password = GetProviderApiKey(providerId) ?? string.Empty;
            VoiceTtsModelTextBox.Text = GetProviderModel(providerId);
            VoiceTtsVoiceIdTextBox.Text = GetProviderVoiceId(providerId);
            _activeTtsProviderId = providerId;
        }
        finally
        {
            _updatingVoiceProviderFields = false;
        }
    }

    private string GetSelectedTextToSpeechProviderId()
    {
        return (VoiceTextToSpeechProviderComboBox.SelectedItem as ProviderOption)?.Id ?? VoiceProviderIds.Windows;
    }

    private string GetSelectedTextToSpeechProviderName()
    {
        return (VoiceTextToSpeechProviderComboBox.SelectedItem as ProviderOption)?.Name ?? "Provider";
    }

    private void CaptureSelectedVoiceProviderSettings()
    {
        if (_updatingVoiceProviderFields)
        {
            return;
        }

        var providerId = _activeTtsProviderId;
        if (string.Equals(providerId, VoiceProviderIds.Windows, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        SetProviderApiKey(providerId, VoiceTtsApiKeyPasswordBox.Password);
        SetProviderModel(providerId, VoiceTtsModelTextBox.Text);
        SetProviderVoiceId(providerId, VoiceTtsVoiceIdTextBox.Text);
    }

    private string? GetProviderApiKey(string providerId)
    {
        return providerId switch
        {
            VoiceProviderIds.MiniMax => _voiceProviderCredentialsDraft.MiniMaxApiKey,
            VoiceProviderIds.ElevenLabs => _voiceProviderCredentialsDraft.ElevenLabsApiKey,
            _ => null
        };
    }

    private string GetProviderModel(string providerId)
    {
        return providerId switch
        {
            VoiceProviderIds.MiniMax => _voiceProviderCredentialsDraft.MiniMaxModel,
            VoiceProviderIds.ElevenLabs => _voiceProviderCredentialsDraft.ElevenLabsModel ?? string.Empty,
            _ => string.Empty
        };
    }

    private string GetProviderVoiceId(string providerId)
    {
        return providerId switch
        {
            VoiceProviderIds.MiniMax => _voiceProviderCredentialsDraft.MiniMaxVoiceId,
            VoiceProviderIds.ElevenLabs => _voiceProviderCredentialsDraft.ElevenLabsVoiceId ?? string.Empty,
            _ => string.Empty
        };
    }

    private void SetProviderApiKey(string providerId, string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        switch (providerId)
        {
            case VoiceProviderIds.MiniMax:
                _voiceProviderCredentialsDraft.MiniMaxApiKey = normalized;
                break;
            case VoiceProviderIds.ElevenLabs:
                _voiceProviderCredentialsDraft.ElevenLabsApiKey = normalized;
                break;
        }
    }

    private void SetProviderModel(string providerId, string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? GetDefaultModel(providerId) : value.Trim();

        switch (providerId)
        {
            case VoiceProviderIds.MiniMax:
                _voiceProviderCredentialsDraft.MiniMaxModel = normalized;
                break;
            case VoiceProviderIds.ElevenLabs:
                _voiceProviderCredentialsDraft.ElevenLabsModel = normalized;
                break;
        }
    }

    private void SetProviderVoiceId(string providerId, string? value)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? GetDefaultVoiceId(providerId) : value.Trim();

        switch (providerId)
        {
            case VoiceProviderIds.MiniMax:
                _voiceProviderCredentialsDraft.MiniMaxVoiceId = normalized;
                break;
            case VoiceProviderIds.ElevenLabs:
                _voiceProviderCredentialsDraft.ElevenLabsVoiceId = normalized;
                break;
        }
    }

    private static string GetDefaultModel(string providerId)
    {
        return providerId switch
        {
            VoiceProviderIds.MiniMax => "speech-2.8-turbo",
            _ => string.Empty
        };
    }

    private static string GetDefaultVoiceId(string providerId)
    {
        return providerId switch
        {
            VoiceProviderIds.MiniMax => "English_MatureBoss",
            _ => string.Empty
        };
    }

    private async void OnRefreshVoiceDevices(object sender, RoutedEventArgs e)
    {
        await LoadVoiceDevicesAsync();
    }

    private void OnVoiceModeChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateVoiceSettingsInfo();
    }

    private void OnVoiceProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        CaptureSelectedVoiceProviderSettings();
        UpdateVoiceProviderSettingsEditor();
        UpdateVoiceSettingsInfo();
    }

    private void OnVoiceProviderSettingsChanged(object sender, RoutedEventArgs e)
    {
        CaptureSelectedVoiceProviderSettings();
    }

    private static VoiceProviderCredentials Clone(VoiceProviderCredentials source)
    {
        return new VoiceProviderCredentials
        {
            MiniMaxApiKey = source.MiniMaxApiKey,
            MiniMaxModel = source.MiniMaxModel,
            MiniMaxVoiceId = source.MiniMaxVoiceId,
            ElevenLabsApiKey = source.ElevenLabsApiKey,
            ElevenLabsModel = source.ElevenLabsModel,
            ElevenLabsVoiceId = source.ElevenLabsVoiceId
        };
    }

    private sealed record DeviceOption(string? DeviceId, string Name);
    private sealed record ProviderOption(string Id, string Name, string Runtime, string? Description);
}
