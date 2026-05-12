using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OpenClaw.Shared;
using OpenClawTray.Helpers;
using OpenClawTray.Services;
using OpenClawTray.Windows;
using System;

namespace OpenClawTray.Pages;

public sealed partial class SettingsPage : Page
{
    private HubWindow? _hub;
    private bool _initialized;
    private bool _saving;
    private bool _isDirty;

    public SettingsPage()
    {
        InitializeComponent();
    }

    public void Initialize(HubWindow hub)
    {
        _hub = hub;
        if (!_initialized && hub.Settings != null)
        {
            LoadSettings(hub.Settings);
            hub.Settings.Saved += OnExternalSettingsChanged;
            RegisterDirtyHandlers();
            _initialized = true;
        }
        // No external-state re-bind needed: pre-approve toggles moved to Capabilities → Camera/Screen rows.
    }

    private void RegisterDirtyHandlers()
    {
        void MarkDirty(object s, RoutedEventArgs e) { if (_initialized) _isDirty = true; }

        AutoStartToggle.Toggled += MarkDirty;
        GlobalHotkeyToggle.Toggled += MarkDirty;
        NotificationsToggle.Toggled += MarkDirty;
        NotificationSoundComboBox.SelectionChanged += (s, e) => { if (_initialized) _isDirty = true; };
        NotifyHealthCb.Checked += MarkDirty; NotifyHealthCb.Unchecked += MarkDirty;
        NotifyUrgentCb.Checked += MarkDirty; NotifyUrgentCb.Unchecked += MarkDirty;
        NotifyReminderCb.Checked += MarkDirty; NotifyReminderCb.Unchecked += MarkDirty;
        NotifyEmailCb.Checked += MarkDirty; NotifyEmailCb.Unchecked += MarkDirty;
        NotifyCalendarCb.Checked += MarkDirty; NotifyCalendarCb.Unchecked += MarkDirty;
        NotifyBuildCb.Checked += MarkDirty; NotifyBuildCb.Unchecked += MarkDirty;
        NotifyStockCb.Checked += MarkDirty; NotifyStockCb.Unchecked += MarkDirty;
        NotifyInfoCb.Checked += MarkDirty; NotifyInfoCb.Unchecked += MarkDirty;
    }

    private void OnExternalSettingsChanged(object? sender, EventArgs e)
    {
        if (_hub?.Settings == null || _saving || _isDirty) return;
        // Pre-approve toggles moved to Capabilities — nothing to re-bind here.
    }

    private void LoadSettings(SettingsManager settings)
    {
        AutoStartToggle.IsOn = settings.AutoStart;
        GlobalHotkeyToggle.IsOn = settings.GlobalHotkeyEnabled;
        NotificationsToggle.IsOn = settings.ShowNotifications;

        for (int i = 0; i < NotificationSoundComboBox.Items.Count; i++)
        {
            if (NotificationSoundComboBox.Items[i] is ComboBoxItem item &&
                item.Tag?.ToString() == settings.NotificationSound)
            {
                NotificationSoundComboBox.SelectedIndex = i;
                break;
            }
        }
        if (NotificationSoundComboBox.SelectedIndex < 0)
            NotificationSoundComboBox.SelectedIndex = 0;

        NotifyHealthCb.IsChecked = settings.NotifyHealth;
        NotifyUrgentCb.IsChecked = settings.NotifyUrgent;
        NotifyReminderCb.IsChecked = settings.NotifyReminder;
        NotifyEmailCb.IsChecked = settings.NotifyEmail;
        NotifyCalendarCb.IsChecked = settings.NotifyCalendar;
        NotifyBuildCb.IsChecked = settings.NotifyBuild;
        NotifyStockCb.IsChecked = settings.NotifyStock;
        NotifyInfoCb.IsChecked = settings.NotifyInfo;

        // Pre-approve camera/screen toggles moved to Capabilities → row "Always allow".
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_hub?.Settings == null) return;

        var s = _hub.Settings;
        s.AutoStart = AutoStartToggle.IsOn;
        s.GlobalHotkeyEnabled = GlobalHotkeyToggle.IsOn;
        s.ShowNotifications = NotificationsToggle.IsOn;

        if (NotificationSoundComboBox.SelectedItem is ComboBoxItem item)
            s.NotificationSound = item.Tag?.ToString() ?? "Default";

        s.NotifyHealth = NotifyHealthCb.IsChecked ?? true;
        s.NotifyUrgent = NotifyUrgentCb.IsChecked ?? true;
        s.NotifyReminder = NotifyReminderCb.IsChecked ?? true;
        s.NotifyEmail = NotifyEmailCb.IsChecked ?? true;
        s.NotifyCalendar = NotifyCalendarCb.IsChecked ?? true;
        s.NotifyBuild = NotifyBuildCb.IsChecked ?? true;
        s.NotifyStock = NotifyStockCb.IsChecked ?? true;
        s.NotifyInfo = NotifyInfoCb.IsChecked ?? true;

        // Pre-approve camera/screen toggles moved to Capabilities — saved from there.

        _saving = true;
        s.Save();
        _saving = false;
        _isDirty = false;
        AutoStartManager.SetAutoStart(s.AutoStart);
        _hub.RaiseSettingsSaved();

        SaveButton.Content = "✓ Saved";
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.Tick += (t, a) => { SaveButton.Content = "Save"; timer.Stop(); };
        timer.Start();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (_hub?.Settings != null)
        {
            _initialized = false;
            LoadSettings(_hub.Settings);
            _initialized = true;
            _isDirty = false;
        }
    }

    private void OnTestNotification(object sender, RoutedEventArgs e)
    {
        try
        {
            new ToastContentBuilder()
                .AddText("Test Notification")
                .AddText("This is a test notification from OpenClaw settings.")
                .Show();
        }
        catch { }
    }

    // OnRemoveGateway / ShowUninstallError / ApplyUninstallUiState / UninstallUiState
    // moved to ConnectionPage along with the Local Gateway lifecycle UI.
}
