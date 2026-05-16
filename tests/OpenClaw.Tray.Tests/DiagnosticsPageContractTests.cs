using System.Reflection;

namespace OpenClaw.Tray.Tests;

/// <summary>
/// Static-source contract tests for the Diagnostics page redesign. We assert
/// the structure rather than spin up WinUI so the tests stay in the pure
/// net10.0 Tray.Tests project and run on Linux build agents too. The intent
/// is to catch regressions that would silently undo the design:
///   - replacing Toolkit SettingsCard back with raw Expander
///   - dropping the bundle-preview dialog
///   - re-introducing duplicated "Open Log File" / "Open Config Folder"
///     surfaces that already live on AboutPage
///   - the AboutPage Copy-Support-Context handler diverging from the
///     unified CommandCenterTextHelper.BuildSupportContext path
///   - the diagnostics bundle text omitting the redaction exclusion line
/// </summary>
public sealed class DiagnosticsPageContractTests
{
    private static string RepoRoot()
    {
        var env = Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env;

        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d != null)
        {
            if (File.Exists(Path.Combine(d.FullName, "openclaw-windows-node.slnx")) &&
                Directory.Exists(Path.Combine(d.FullName, "src")))
                return d.FullName;
            d = d.Parent;
        }
        throw new InvalidOperationException(
            "Could not find repository root. Set OPENCLAW_REPO_ROOT to the repo path.");
    }

    private static string Read(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray()));

    [Fact]
    public void DebugPage_UsesToolkitSettingsCard_NotRawExpander()
    {
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml");
        // Toolkit namespace must be declared.
        Assert.Contains("xmlns:toolkit=\"using:CommunityToolkit.WinUI.Controls\"", xaml);
        // At least the primary card uses SettingsCard.
        Assert.Contains("toolkit:SettingsCard", xaml);
        // The page must not have the chaotic flat list of <Expander> cards
        // it had before the redesign. Stock <Expander> is still allowed
        // elsewhere if needed, but we assert the page now uses Toolkit
        // SettingsExpander as the grouping primitive for sub-items.
        Assert.Contains("toolkit:SettingsExpander", xaml);
    }

    [Fact]
    public void DebugPage_HasThreeTaskOrientedSections()
    {
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml");
        Assert.Contains("Share diagnostics with support", xaml);
        Assert.Contains("Inspect local diagnostics", xaml);
        Assert.Contains("Developer tools", xaml);
    }

    [Fact]
    public void DebugPage_SurfacesAllExistingDiagnosticCommands()
    {
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml");
        // The four diagnostic-text commands that already existed in
        // App.xaml.cs but were invisible on the page before the redesign
        // must now have UI entry points.
        Assert.Contains("OnCopySupportContext", xaml);
        Assert.Contains("OnCopyDebugBundle", xaml);
        Assert.Contains("OnCopyBrowserSetup", xaml);
        Assert.Contains("OnCopyPortDiagnostics", xaml);
        Assert.Contains("OnCopyCapabilityDiagnostics", xaml);
        // Primary bundle action opens the preview dialog instead of
        // copying silently.
        Assert.Contains("OnCreateDiagnosticsBundle", xaml);
    }

    [Fact]
    public void DebugPage_LeadsWithStatusInfoBar_NotIdentity()
    {
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml");
        var statusIndex = xaml.IndexOf("StatusInfoBar", StringComparison.Ordinal);
        var identityIndex = xaml.IndexOf("DeviceIdText", StringComparison.Ordinal);
        Assert.True(statusIndex > 0, "Status InfoBar must be present");
        Assert.True(identityIndex > 0, "Device identity must be present");
        // Rubber-duck fix v2 #4: identity is not the lead of the page.
        Assert.True(statusIndex < identityIndex,
            "Status InfoBar must appear before Device identity in the XAML.");
    }

    [Fact]
    public void DebugPage_HasInPageDetailView_NotSeparateWindow()
    {
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml");
        // We swap a MainView/DetailView pair via Visibility, mirroring
        // ConnectionPage.AddGatewayPanel — not a separate Window.
        Assert.Contains("x:Name=\"MainView\"", xaml);
        Assert.Contains("x:Name=\"DetailView\"", xaml);
        Assert.Contains("Visibility=\"Collapsed\"", xaml);
        // The detail surface uses a RichTextBlock so we can color
        // individual events / log lines.
        Assert.Contains("x:Name=\"DetailRichText\"", xaml);

        // The old "open separate ConnectionStatusWindow" entry must no
        // longer be on the page — the timeline is in-page now.
        Assert.DoesNotContain("OnOpenConnectionDiagnostics", xaml);
    }

    [Fact]
    public void DebugPage_UsesFluentIconCatalog_NotLiteralGlyphs()
    {
        // Per docs/design/iconography.md and AGENT_HANDOFF.md "drift
        // candidates", WinUI surfaces must route through
        // FluentIconCatalog. Page should declare the helpers xmlns
        // and bind glyphs from the catalog.
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml");
        Assert.Contains("xmlns:helpers=\"using:OpenClawTray.Helpers\"", xaml);
        Assert.Contains("x:Bind helpers:FluentIconCatalog.", xaml);

        // No literal PUA glyph hex entities in the body. We allow
        // catalog references; we forbid raw "Glyph=&#xE..."
        // declarations because that's exactly the drift the design
        // reference calls out.
        Assert.DoesNotContain("Glyph=\"&#x", xaml);
    }

    [Fact]
    public void DebugPage_UsesSystemFillBrushes_NotLiteralColors()
    {
        // Per docs/design/tokens.md status colors must use
        // SystemFillColor* tokens. Hard-coded ARGB / Color.FromArgb
        // is the drift the handoff calls out for status dots.
        var cs = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml.cs");
        Assert.Contains("SystemFillColorCriticalBrush", cs);
        Assert.Contains("SystemFillColorCautionBrush", cs);
        Assert.Contains("SystemFillColorSuccessBrush", cs);
        Assert.Contains("TextFillColorSecondaryBrush", cs);
        Assert.DoesNotContain("ColorHelper.FromArgb", cs);
    }

    [Fact]
    public void DebugPage_UsesCanonicalReconfigureLabel()
    {
        // Per docs/design/naming.md, "Reconfigure…" (with ellipsis) is
        // the canonical verb for "walk the user back through the
        // onboarding wizard". The old "Relaunch first-run setup"
        // label is prohibited.
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml");
        Assert.Contains("Reconfigure", xaml);
        Assert.Contains("\u2026", xaml); // U+2026 HORIZONTAL ELLIPSIS
        Assert.DoesNotContain("Relaunch first-run setup", xaml);
    }

    [Fact]
    public void DebugPage_PageDimensionsMatchPermissions()
    {
        // Page uses Width=900 + 24 px padding so the title block, MainView,
        // and DetailView all share a single centered 900-wide column.
        // Width (not MaxWidth) because we need the title TextBlocks to
        // align to the SettingsCard left edge even though TextBlocks
        // naturally measure to text width — see screenshot iteration
        // in #thread.
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml");
        Assert.Contains("Width=\"900\"", xaml);
        Assert.DoesNotContain("MaxWidth=\"1064\"", xaml);
    }

    [Fact]
    public void DebugPage_DetailView_HasTimelineAndLogModes()
    {
        var cs = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml.cs");
        Assert.Contains("enum DetailMode", cs);
        Assert.Contains("DetailMode.Timeline", cs);
        Assert.Contains("DetailMode.Log", cs);
        // Both entry points present.
        Assert.Contains("OnShowEventTimeline", cs);
        Assert.Contains("OnShowRecentLog", cs);
        // Back navigation present.
        Assert.Contains("OnBackToMain", cs);
    }

    [Fact]
    public void DebugPage_SharesColoringWithConnectionStatusWindow()
    {
        var cs = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml.cs");
        // The five severity brushes mirror ConnectionStatusWindow:33-40
        // so both surfaces speak the same visual language.
        Assert.Contains("ErrorTextBrush", cs);
        Assert.Contains("WarnTextBrush", cs);
        Assert.Contains("OkTextBrush", cs);
        Assert.Contains("DimTextBrush", cs);
        Assert.Contains("AuthTextBrush", cs);

        // Log lines must be parsed for severity so they get colored too.
        Assert.Contains("LogSeverityPattern", cs);
        Assert.Contains("CreateLogParagraph", cs);
    }

    [Fact]
    public void DebugPage_TimelineSubscribesToConnectionDiagnostics()
    {
        var cs = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml.cs");
        // Live timeline must subscribe to ConnectionDiagnostics events
        // and unsubscribe on leave to avoid leaks across navigations.
        Assert.Contains("EventRecorded += OnTimelineEventRecorded", cs);
        Assert.Contains("EventRecorded -= OnTimelineEventRecorded", cs);
        // After Ranjesh's single-app-model rebase, pages get
        // ConnectionManager from App directly rather than via HubWindow.
        Assert.Contains("CurrentApp.ConnectionManager?.Diagnostics", cs);
    }

    [Fact]
    public void DiagnosticsBundleDialog_Exists_And_ExposesCopyAndSaveAndCancel()
    {
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Windows", "DiagnosticsBundleDialog.xaml");
        Assert.Contains("ContentDialog", xaml);
        Assert.Contains("PrimaryButtonText=\"Copy to clipboard\"", xaml);
        Assert.Contains("SecondaryButtonText=\"Save to file\"", xaml);
        Assert.Contains("CloseButtonText=\"Close\"", xaml);

        var cs = Read("src", "OpenClaw.Tray.WinUI", "Windows", "DiagnosticsBundleDialog.xaml.cs");
        // The dialog must expose a Configure() that takes a HWND-provider
        // delegate (not a captured IntPtr) so we can resolve the host
        // window handle JUST-IN-TIME when Save is clicked, instead of
        // trusting a possibly-stale handle captured at dialog open
        // (Hanselman v2 review #4).
        Assert.Contains("public void Configure(", cs);
        Assert.Contains("Func<IntPtr>", cs);
        Assert.Contains("hwndProvider", cs);
        // It must NOT auto-close on Copy — the user may want to also save.
        Assert.Contains("args.Cancel = true", cs);
    }

    [Fact]
    public void AboutPage_CopySupportContext_UsesUnifiedHelper()
    {
        var cs = Read("src", "OpenClaw.Tray.WinUI", "Pages", "AboutPage.xaml.cs");
        // Plan §4 / rubber-duck v2 #7: AboutPage's Copy Support Context
        // must call the same CommandCenterTextHelper.BuildSupportContext
        // that Diagnostics uses, not its old hand-rolled local string.
        Assert.Contains("CommandCenterTextHelper.BuildSupportContext", cs);
        // And there must be a hyperlink that takes the user from About
        // to the richer Diagnostics surface.
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Pages", "AboutPage.xaml");
        Assert.Contains("OnMoreDiagnosticsClick", xaml);
    }

    [Fact]
    public void HubWindow_DebugNavItem_RoutesUnchanged_LabelRenamed()
    {
        // The Tag must still be "debug" so command-palette / deep-link
        // aliases keep working, even though the visible label is now
        // "Diagnostics".
        var xaml = Read("src", "OpenClaw.Tray.WinUI", "Windows", "HubWindow.xaml");
        Assert.Contains("Tag=\"debug\"", xaml);

        var resw = Read("src", "OpenClaw.Tray.WinUI", "Strings", "en-us", "Resources.resw");
        Assert.Contains("<data name=\"HubWindow_NavigationViewItem_145.Content\"", resw);
        // The resw entry must now say Diagnostics.
        var navEntryStart = resw.IndexOf("<data name=\"HubWindow_NavigationViewItem_145.Content\"", StringComparison.Ordinal);
        var navEntryEnd = resw.IndexOf("</data>", navEntryStart, StringComparison.Ordinal);
        var entry = resw.Substring(navEntryStart, navEntryEnd - navEntryStart);
        Assert.Contains("Diagnostics", entry);

        // Internal route mapping unchanged.
        var cs = Read("src", "OpenClaw.Tray.WinUI", "Windows", "HubWindow.xaml.cs");
        Assert.Contains("\"debug\" => typeof(DebugPage)", cs);
    }

    [Fact]
    public void DebugPage_ObservesAppState_NotHubWindow()
    {
        // After Ranjesh's single-app-model rebase, the page must
        // observe AppState directly per
        // docs/DATA_FLOW_ARCHITECTURE.md and not depend on HubWindow.
        var cs = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml.cs");
        Assert.Contains("private static App CurrentApp", cs);
        Assert.Contains("AppState? _appState", cs);
        Assert.Contains("_appState.PropertyChanged", cs);
        // App provides BuildCommandCenterState() so the bundle preview
        // dialog can render text without going through HubWindow.
        var app = Read("src", "OpenClaw.Tray.WinUI", "App.xaml.cs");
        Assert.Contains("internal GatewayCommandCenterState BuildCommandCenterState", app);
        // HubWindow no longer plumbs a state-action callback for pages.
        var hub = Read("src", "OpenClaw.Tray.WinUI", "Windows", "HubWindow.xaml.cs");
        Assert.DoesNotContain("GetCommandCenterStateAction", hub);
    }

    [Fact]
    public void DebugPage_RefreshesOnSettingsChanged()
    {
        // The Status InfoBar shows the effective Gateway URL from
        // SettingsManager. Settings-saved events must update the page
        // immediately rather than waiting for the next Status flip
        // (reactive-by-default ethos per docs/DATA_FLOW_ARCHITECTURE.md).
        var cs = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml.cs");
        Assert.Contains("CurrentApp.SettingsChanged += OnSettingsChanged", cs);
        Assert.Contains("CurrentApp.SettingsChanged -= OnSettingsChanged", cs);
        Assert.Contains("OnSettingsChanged", cs);
    }

    [Fact]
    public void CommandCenterTextHelper_SupportContext_AdvertisesRedaction()
    {
        // Rubber-duck v2 risk #3: the bundle output must continue to
        // explicitly advertise the redaction promise, since the new
        // preview dialog surfaces this text to users.
        var helper = Read("src", "OpenClaw.Tray.WinUI", "Helpers", "CommandCenterTextHelper.cs");
        Assert.Contains("Excluded:", helper);
        Assert.Contains("tokens", helper);
        Assert.Contains("bootstrap tokens", helper);
    }

    [Fact]
    public void DebugPage_DetailView_UsesGenerationCounterForRaceSafety()
    {
        // Hanselman v2 review #5/#6: long log reads and queued live
        // event handlers must check a generation counter after their
        // async/dispatched continuation so a mode switch or navigation
        // mid-flight can't clobber the active view.
        var cs = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml.cs");
        Assert.Contains("_detailGeneration", cs);
        // LoadLogFileAsync takes the generation as a parameter.
        Assert.Contains("LoadLogFileAsync(int generation)", cs);
        // Live timeline handler re-checks both mode AND generation
        // inside the DispatcherQueue lambda.
        Assert.Contains("_detailMode != DetailMode.Timeline || _detailGeneration != gen", cs);
    }

    [Fact]
    public void DebugPage_DropsDedupeSetAfterSnapshot()
    {
        // Hanselman v2 review #1 + #2: the _renderedEvents HashSet must
        // not be kept forever — it grows unbounded and can drop
        // legitimate events whose record value-equality collides
        // (DateTime.UtcNow resolution is ~15.6ms). The set is only
        // needed during the snapshot/subscribe overlap window.
        var cs = Read("src", "OpenClaw.Tray.WinUI", "Pages", "DebugPage.xaml.cs");
        Assert.Contains("_renderedEventsActive", cs);
        // The live handler only consults the set while it's active.
        Assert.Contains("_renderedEventsActive && !_renderedEvents.Add", cs);
        // The set is cleared after the snapshot foreach.
        Assert.Contains("_renderedEvents.Clear()", cs);
    }

    [Fact]
    public void App_GetHubWindowHandle_GuardsAgainstClosedWindow()
    {
        // Hanselman v2 review #4: every other _hubWindow call site
        // pairs the null check with !IsClosed; this one should too,
        // otherwise the file picker can land a stale HWND during a
        // shutdown race.
        var app = Read("src", "OpenClaw.Tray.WinUI", "App.xaml.cs");
        Assert.Contains("public IntPtr GetHubWindowHandle()", app);
        Assert.Contains("_hubWindow != null && !_hubWindow.IsClosed", app);
    }

    [Fact]
    public void App_SettingsChanged_DispatchesToUiThread()
    {
        // Hanselman v2 review #7: IAppCommands.NotifySettingsSaved is a
        // public entry point; future BG callers must not be able to
        // race the InfoBar refresh. OnSettingsSaved must marshal the
        // SettingsChanged?.Invoke onto the UI dispatcher when called
        // off-thread.
        var app = Read("src", "OpenClaw.Tray.WinUI", "App.xaml.cs");
        // Check the marshalling pattern explicitly.
        Assert.Contains("_dispatcherQueue.HasThreadAccess", app);
        Assert.Contains("_dispatcherQueue.TryEnqueue(() => SettingsChanged?.Invoke", app);
    }
}
