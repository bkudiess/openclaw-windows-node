using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Windows;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace OpenClawTray.Pages;

public sealed partial class SandboxPage : Page
{
    private HubWindow? _hub;
    private bool _suppress;

    public ObservableCollection<CustomFolderRow> CustomFolders { get; } = new();

    public SandboxPage()
    {
        InitializeComponent();
        CustomFoldersList.ItemsSource = CustomFolders;
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        LoadState();
        ProbeStatus();
    }

    // ── Load + probe ─────────────────────────────────────────────────

    private void LoadState()
    {
        if (_hub?.Settings is not { } settings) return;

        _suppress = true;
        try
        {
            SandboxEnabledToggle.IsOn = settings.SystemRunSandboxEnabled;
            SandboxOffWarning.Visibility = settings.SystemRunSandboxEnabled ? Visibility.Collapsed : Visibility.Visible;

            NetInternetToggle.IsOn = settings.SystemRunAllowOutbound;

            SelectAccessTag(DocsAccessCombo, settings.SandboxDocumentsAccess);
            SelectAccessTag(DownloadsAccessCombo, settings.SandboxDownloadsAccess);
            SelectAccessTag(DesktopAccessCombo, settings.SandboxDesktopAccess);

            CustomFolders.Clear();
            foreach (var f in settings.SandboxCustomFolders ?? new())
                CustomFolders.Add(new CustomFolderRow(f.Path, f.Access));
            RefreshCustomFoldersUi();

            (settings.SandboxClipboard switch
            {
                SandboxClipboardMode.Read => ClipboardReadRadio,
                SandboxClipboardMode.Write => ClipboardWriteRadio,
                SandboxClipboardMode.Both => ClipboardBothRadio,
                _ => ClipboardNoneRadio,
            }).IsChecked = true;

            var secs = Math.Clamp(settings.SandboxTimeoutMs / 1000, 5, 300);
            TimeoutSlider.Value = secs;
            TimeoutLabel.Text = $"Max time per command: {secs} sec";

            SelectMaxOutputTag(settings.SandboxMaxOutputBytes);
        }
        finally
        {
            _suppress = false;
        }
    }

    private void ProbeStatus()
    {
        var a = OpenClaw.Shared.Mxc.MxcAvailability.Probe();
        if (a.HasAnyBackend)
        {
            StatusBannerHeader.Text = "Sandbox available ✓";
            StatusBannerDetail.Text = $"appcontainer={a.IsAppContainerAvailable}, isolation_session={a.IsIsolationSessionAvailable}";
        }
        else
        {
            StatusBannerHeader.Text = "⚠ Sandbox UNAVAILABLE on this machine";
            StatusBannerDetail.Text = string.Join(" · ", a.UnsupportedReasons);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static void SelectAccessTag(ComboBox combo, SandboxFolderAccess? access)
    {
        var tag = access switch
        {
            SandboxFolderAccess.ReadOnly => "ReadOnly",
            SandboxFolderAccess.ReadWrite => "ReadWrite",
            _ => "None",
        };
        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => (string?)i.Tag == tag);
    }

    private void SelectMaxOutputTag(long bytes)
    {
        var match = MaxOutputCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => long.TryParse((string?)i.Tag, out var v) && v == bytes);
        MaxOutputCombo.SelectedItem = match ?? MaxOutputCombo.Items[1]; // default 4 MiB
    }

    private static SandboxFolderAccess? ReadAccessTag(ComboBox combo)
    {
        var tag = (string?)((ComboBoxItem?)combo.SelectedItem)?.Tag;
        return tag switch
        {
            "ReadOnly" => SandboxFolderAccess.ReadOnly,
            "ReadWrite" => SandboxFolderAccess.ReadWrite,
            _ => null,
        };
    }

    private void Save()
    {
        if (_suppress) return;
        _hub?.Settings?.Save();
    }

    private void RefreshCustomFoldersUi()
    {
        CustomFoldersList.Visibility = CustomFolders.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        CustomFoldersEmpty.Visibility = CustomFolders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Event handlers ───────────────────────────────────────────────

    private void OnSandboxEnabledToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        if (_hub?.Settings is not { } s) return;
        s.SystemRunSandboxEnabled = SandboxEnabledToggle.IsOn;
        SandboxOffWarning.Visibility = SandboxEnabledToggle.IsOn ? Visibility.Collapsed : Visibility.Visible;
        Save();
    }

    private void OnNetInternetToggled(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        if (_hub?.Settings is not { } s) return;
        s.SystemRunAllowOutbound = NetInternetToggle.IsOn;
        Save();
    }

    private void OnDocsAccessChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (_hub?.Settings is not { } s) return;
        s.SandboxDocumentsAccess = ReadAccessTag(DocsAccessCombo);
        Save();
    }

    private void OnDownloadsAccessChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (_hub?.Settings is not { } s) return;
        s.SandboxDownloadsAccess = ReadAccessTag(DownloadsAccessCombo);
        Save();
    }

    private void OnDesktopAccessChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (_hub?.Settings is not { } s) return;
        s.SandboxDesktopAccess = ReadAccessTag(DesktopAccessCombo);
        Save();
    }

    private async void OnAddCustomFolderReadOnly(object sender, RoutedEventArgs e)
        => await PickCustomFolderAsync(SandboxFolderAccess.ReadOnly);

    private async void OnAddCustomFolderReadWrite(object sender, RoutedEventArgs e)
        => await PickCustomFolderAsync(SandboxFolderAccess.ReadWrite);

    private async System.Threading.Tasks.Task PickCustomFolderAsync(SandboxFolderAccess access)
    {
        if (_hub is null) return;

        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.Desktop,
        };
        picker.FileTypeFilter.Add("*");

        var hwnd = WindowNative.GetWindowHandle(_hub);
        InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null || string.IsNullOrWhiteSpace(folder.Path)) return;
        if (CustomFolders.Any(f => string.Equals(f.Path, folder.Path, StringComparison.OrdinalIgnoreCase))) return;

        CustomFolders.Add(new CustomFolderRow(folder.Path, access));
        RefreshCustomFoldersUi();

        if (_hub.Settings is { } s)
        {
            s.SandboxCustomFolders.Add(new SandboxCustomFolder { Path = folder.Path, Access = access });
            Save();
        }
    }

    private void OnRemoveCustomFolder(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string path) return;
        var row = CustomFolders.FirstOrDefault(f => f.Path == path);
        if (row != null) CustomFolders.Remove(row);
        RefreshCustomFoldersUi();

        if (_hub?.Settings is { } s)
        {
            s.SandboxCustomFolders.RemoveAll(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
            Save();
        }
    }

    private void OnClipboardChanged(object sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        if (sender is not RadioButton rb || rb.Tag is not string tag) return;
        if (_hub?.Settings is not { } s) return;
        s.SandboxClipboard = tag switch
        {
            "Read" => SandboxClipboardMode.Read,
            "Write" => SandboxClipboardMode.Write,
            "Both" => SandboxClipboardMode.Both,
            _ => SandboxClipboardMode.None,
        };
        Save();
    }

    private void OnTimeoutChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_suppress) return;
        var secs = (int)Math.Round(e.NewValue);
        TimeoutLabel.Text = $"Max time per command: {secs} sec";
        if (_hub?.Settings is not { } s) return;
        s.SandboxTimeoutMs = secs * 1000;
        Save();
    }

    private void OnMaxOutputChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppress) return;
        if (_hub?.Settings is not { } s) return;
        var tag = (string?)((ComboBoxItem?)MaxOutputCombo.SelectedItem)?.Tag;
        if (tag != null && long.TryParse(tag, out var bytes))
        {
            s.SandboxMaxOutputBytes = bytes;
            Save();
        }
    }

    public sealed class CustomFolderRow
    {
        public string Path { get; }
        public string AccessLabel { get; }

        public CustomFolderRow(string path, SandboxFolderAccess access)
        {
            Path = path;
            AccessLabel = access == SandboxFolderAccess.ReadWrite ? "✏️ Read+Write" : "🔒 Read only";
        }
    }
}
