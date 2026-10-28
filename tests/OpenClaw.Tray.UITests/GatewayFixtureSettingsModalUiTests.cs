using System.Windows.Automation;
using OpenClaw.TestSupport.Gateway;

namespace OpenClaw.Tray.UITests;

public sealed partial class GatewayFixtureUiTests
{
    [GatewayFixtureUiTheory]
    [InlineData(null, "ConnectionPageMarker")]
    [InlineData("connection", "ConnectionPageMarker")]
    [InlineData("permissions", "PermissionsPageMarker")]
    [Trait("Category", "GatewayFixture")]
    public async Task SettingsDisconnectPruning_WaitsForRealDialogBeforeRenderingFallbackOrLatestIntent(
        string? queuedRoute, string expectedMarker)
    {
        await WithAppAsync(async run =>
        {
            await run.InvokeAsync("app.navigate", new { page = "sessions" });
            await WaitUiAsync(run, () => FindText(run, GatewayScenario.MainTitle) is not null,
                "populated Sessions page");
            Assert.False(FindById(run, "NavSessions")!.Current.IsOffscreen);
            var window = Assert.Single(AppWindows(run), candidate =>
                candidate.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, "SessionsPageMarker")) is not null);
            var hwnd = window.Current.NativeWindowHandle;
            var row = FindText(run, GatewayScenario.MainTitle)!;
            for (var depth = 0; depth < 15 && row.Current.ControlType != ControlType.ListItem; depth++)
                row = TreeWalker.ControlViewWalker.GetParent(row)
                    ?? throw new InvalidOperationException("Session title has no list-item ancestor.");
            var more = row.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, "More actions")));
            Assert.NotNull(more);
            Invoke(more);
            await WaitUiAsync(run, () => FindInApp(run, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
                new PropertyCondition(AutomationElement.NameProperty, "Reset"))) is not null, "session action menu");
            Invoke(FindInApp(run, new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.MenuItem),
                new PropertyCondition(AutomationElement.NameProperty, "Reset")))!);
            await WaitUiAsync(run, () => FindText(run, "Reset session?") is { Current.IsOffscreen: false },
                "real Settings ContentDialog");
            var cancel = FindButton(run, "Cancel")!;
            var dialogIdentity = cancel.GetRuntimeId();
            OwnedWindowCapture.Save(run, window, "01-settings-dialog-before-disconnect.png",
                FindText(run, "Reset session?")!, cancel);

            // Stop only this run's synthetic server so reconnect cannot hide the prune transition.
            await run.Gateway.DisposeAsync();
            await run.WaitForAsync(async () =>
                (await run.InvokeAsync("app.status")).GetProperty("operatorState").GetString() != "Connected",
                "fixture operator disconnect");
            await WaitUiAsync(run, () => FindById(run, "NavSessions") is null or { Current.IsOffscreen: true },
                "Gateway navigation pruned after disconnect");
            if (queuedRoute is not null)
                await run.InvokeAsync("app.navigate", new { page = queuedRoute });

            Assert.NotNull(FindById(run, "SessionsPageMarker"));
            Assert.Null(FindById(run, "ConnectionPageMarker"));
            Assert.Null(FindById(run, "PermissionsPageMarker"));
            Assert.Equal(dialogIdentity, FindButton(run, "Cancel")!.GetRuntimeId());
            OwnedWindowCapture.Save(run, window, "02-settings-dialog-retains-pruned-page.png",
                FindText(run, "Reset session?")!, FindButton(run, "Cancel")!);

            Invoke(FindButton(run, "Cancel")!);
            await WaitUiAsync(run, () => FindById(run, expectedMarker) is { Current.IsOffscreen: false },
                "required fallback or latest intent after dialog closes");
            Assert.Null(FindText(run, "Reset session?"));
            Assert.Null(FindById(run, "SessionsPageMarker"));
            var destinationWindow = Assert.Single(AppWindows(run), candidate =>
                !candidate.Current.IsOffscreen && candidate.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, expectedMarker))
                    is { Current.IsOffscreen: false });
            Assert.Equal(hwnd, destinationWindow.Current.NativeWindowHandle);
            var mainWindow = Assert.Single(AppWindows(run), candidate =>
                !candidate.Current.IsOffscreen && candidate.FindFirst(TreeScope.Descendants,
                    new OrCondition(
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "WorkspaceTitleBar"),
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "SettingsNavigationToolbar"))) is not null);
            Assert.Equal(hwnd, mainWindow.Current.NativeWindowHandle);
            Assert.DoesNotContain(run.Gateway.Requests, request => request.Method == "sessions.reset");
            OwnedWindowCapture.Save(run, destinationWindow, "03-settings-dialog-closed-destination.png",
                destinationWindow.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.AutomationIdProperty, expectedMarker)));
        });
    }
}
