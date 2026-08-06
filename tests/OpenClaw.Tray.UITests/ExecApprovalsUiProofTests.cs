using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Automation;

namespace OpenClaw.Tray.UITests;

[Collection(AccessibilityCollection.Name)]
public sealed class ExecApprovalsUiProofTests
{
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(10);
    private readonly AccessibilityAppFixture _app;

    public ExecApprovalsUiProofTests(AccessibilityAppFixture app)
    {
        _app = app;
    }

    [Fact]
    [Trait("Category", "Accessibility")]
    public async Task Permissions_RejectsCommandTextAndPersistsV2PathAcrossReload()
    {
        try
        {
            await _app.NavigateAsync("permissions", "PermissionsPageMarker");
            var hub = AutomationElement.FromHandle(_app.HubWindowHandle);

            SelectComboItem(hub, "ExecPolicyScopeCombo", "All agents");

            var patternInput = WaitForElementById(hub, "NewExecAllowlistPattern");
            SetValue(patternInput, "hostname");
            Invoke(WaitForElementById(hub, "AddExecAllowlistButton"));

            var validation = WaitForElementById(hub, "ExecAllowlistValidationText");
            WaitUntil(
                () => IsVisibleWithName(validation, "executable path pattern"),
                "The invalid V1 command pattern did not show V2 path guidance.");
            patternInput = WaitForElementById(hub, "NewExecAllowlistPattern");
            Assert.Contains(
                "executable path pattern",
                patternInput.Current.HelpText,
                StringComparison.OrdinalIgnoreCase);

            patternInput = WaitForElementById(hub, "NewExecAllowlistPattern");
            SetValue(patternInput, "**/where.exe");
            Invoke(WaitForElementById(hub, "AddExecAllowlistButton"));

            var policyPath = Path.Combine(_app.DataDirectory, "exec-approvals.json");
            try
            {
                WaitUntil(
                    () => PolicyContainsPattern(
                        policyPath,
                        agentId: "*",
                        pattern: "**/where.exe"),
                    "The valid V2 executable path was not persisted.");
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException(
                    $"{ex.Message} {DescribeProofState(hub, policyPath)}",
                    ex);
            }

            UpgradeSavedRuleToGeneratedArgumentBinding(policyPath);
            await _app.NavigateAsync("settings", "SettingsPageMarker");
            await _app.NavigateAsync("permissions", "PermissionsPageMarker");
            hub = AutomationElement.FromHandle(_app.HubWindowHandle);
            SelectComboItem(hub, "ExecPolicyScopeCombo", "All agents");
            _ = WaitForElementByName(
                hub,
                "Arguments restricted to the approved command.");
            var removeButton = WaitForElementById(
                hub,
                "RemoveExecAllowlistEntry_0");
            ScrollIntoView(removeButton);
            WaitUntil(
                () => IsVisible(removeButton),
                "The persisted V2 allowlist entry could not be brought into view.");
            AxeHelper.AssertNoAccessibilityErrors(
                _app.HubWindowHandle,
                context: "PermissionsPage exec approvals V2");
        }
        finally
        {
            _ = _app.CaptureHubScreenshotIfRequested();
        }
    }

    private static bool IsVisible(AutomationElement element)
    {
        try
        {
            return !element.Current.IsOffscreen;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private AutomationElement WaitForElementById(
        AutomationElement root,
        string automationId)
    {
        AutomationElement? result = null;
        WaitUntil(
            () =>
            {
                result = root.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(
                        AutomationElement.AutomationIdProperty,
                        automationId));
                return result is not null;
            },
            $"UI Automation element '{automationId}' was not found.");
        return result!;
    }

    private AutomationElement WaitForElementByName(
        AutomationElement root,
        string name)
    {
        AutomationElement? result = null;
        WaitUntil(
            () =>
            {
                result = root.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(
                        AutomationElement.NameProperty,
                        name));
                return result is not null;
            },
            $"UI Automation element with name '{name}' was not found.");
        return result!;
    }

    private void SelectComboItem(
        AutomationElement root,
        string comboAutomationId,
        string itemName)
    {
        WaitUntil(
            () =>
            {
                try
                {
                    var combo = root.FindFirst(
                        TreeScope.Descendants,
                        new PropertyCondition(
                            AutomationElement.AutomationIdProperty,
                            comboAutomationId));
                    if (combo is null || !combo.Current.IsEnabled)
                        return false;
                    ((ExpandCollapsePattern)combo.GetCurrentPattern(
                        ExpandCollapsePattern.Pattern)).Expand();
                    return true;
                }
                catch (ElementNotAvailableException)
                {
                    return false;
                }
            },
            $"Combo box '{comboAutomationId}' was not ready.");

        AutomationElement? item = null;
        WaitUntil(
            () =>
            {
                item = AutomationElement.RootElement.FindFirst(
                    TreeScope.Descendants,
                    new AndCondition(
                        new PropertyCondition(
                            AutomationElement.ProcessIdProperty,
                            _app.ProcessId),
                        new PropertyCondition(
                            AutomationElement.ControlTypeProperty,
                            ControlType.ListItem),
                        new PropertyCondition(
                            AutomationElement.NameProperty,
                            itemName)));
                return item is not null;
            },
            $"Combo box item '{itemName}' was not found.");

        ((SelectionItemPattern)item!.GetCurrentPattern(
            SelectionItemPattern.Pattern)).Select();
        WaitUntil(
            () => ComboSelectionMatches(root, comboAutomationId, itemName),
            $"Combo box did not select '{itemName}'.");
    }

    private static bool IsVisibleWithName(
        AutomationElement element,
        string expectedText)
    {
        try
        {
            return !element.Current.IsOffscreen
                && element.Current.Name.Contains(
                    expectedText,
                    StringComparison.OrdinalIgnoreCase);
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static void SetValue(AutomationElement element, string value) =>
        ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);

    private static void Invoke(AutomationElement element) =>
        ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    private static void ScrollIntoView(AutomationElement element)
    {
        if (element.TryGetCurrentPattern(
                ScrollItemPattern.Pattern,
                out var pattern))
        {
            ((ScrollItemPattern)pattern).ScrollIntoView();
            return;
        }

        element.SetFocus();
    }

    private static bool ComboSelectionMatches(
        AutomationElement root,
        string automationId,
        string itemName)
    {
        try
        {
            var combo = root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(
                    AutomationElement.AutomationIdProperty,
                    automationId));
            if (combo is null)
                return false;
            var selected = ((SelectionPattern)combo.GetCurrentPattern(
                SelectionPattern.Pattern)).Current.GetSelection();
            return selected.Any(item =>
                string.Equals(item.Current.Name, itemName, StringComparison.Ordinal));
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }

    private static bool PolicyContainsPattern(
        string policyPath,
        string agentId,
        string pattern)
    {
        try
        {
            if (!File.Exists(policyPath))
                return false;
            using var document = JsonDocument.Parse(File.ReadAllText(policyPath));
            if (!document.RootElement.TryGetProperty("agents", out var agents))
            {
                return false;
            }

            // Try the specified agentId first, then fall back to "main" or "*"
            var agentIds = new[] { agentId, "main", "*" }.Distinct().ToList();
             
            foreach (var id in agentIds)
            {
                if (!agents.TryGetProperty(id, out var agent)
                    || !agent.TryGetProperty("allowlist", out var allowlist))
                {
                    continue;
                }

                if (allowlist.EnumerateArray().Any(entry =>
                    entry.TryGetProperty("pattern", out var value)
                    && string.Equals(
                        value.GetString(),
                        pattern,
                        StringComparison.Ordinal)))
                {
                    return true;
                }
            }
             
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void UpgradeSavedRuleToGeneratedArgumentBinding(string policyPath)
    {
        var root = JsonNode.Parse(File.ReadAllText(policyPath))
            ?? throw new InvalidOperationException("Exec approvals JSON was empty.");
        var entry = root["agents"]?["*"]?["allowlist"]?[0]?.AsObject()
            ?? throw new InvalidOperationException(
                "Saved wildcard allowlist entry was missing.");
        entry["source"] = "allow-always";
        entry["argPattern"] =
            "sha256:argv:e4f60d0aa6d7f3d3b6a6494b1c861b99f649c6f9ec51abaf201b20f297327c95";
        File.WriteAllText(
            policyPath,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WaitUntil(Func<bool> condition, string failureMessage)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < UiTimeout)
        {
            if (condition())
                return;
            Thread.Sleep(100);
        }

        throw new TimeoutException(failureMessage);
    }

    private string DescribeProofState(
        AutomationElement root,
        string policyPath)
    {
        var scope = TryReadElementName(root, "ExecPolicyScopeCombo");
        var input = TryReadElementValue(root, "NewExecAllowlistPattern");
        var status = TryReadElementName(root, "ExecPolicyStatusInfoBar");
        var files = string.Join(
            ", ",
            Directory.EnumerateFiles(_app.DataDirectory, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(_app.DataDirectory, path)));
        var policy = File.Exists(policyPath)
            ? File.ReadAllText(policyPath)
            : "<missing>";
        return $"Scope='{scope}', input='{input}', status='{status}', files=[{files}], policy={policy}";
    }

    private static string TryReadElementName(
        AutomationElement root,
        string automationId)
    {
        try
        {
            return root.FindFirst(
                    TreeScope.Descendants,
                    new PropertyCondition(
                        AutomationElement.AutomationIdProperty,
                        automationId))
                ?.Current.Name ?? "<missing>";
        }
        catch (ElementNotAvailableException)
        {
            return "<stale>";
        }
    }

    private static string TryReadElementValue(
        AutomationElement root,
        string automationId)
    {
        try
        {
            var element = root.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(
                    AutomationElement.AutomationIdProperty,
                    automationId));
            return element is null
                ? "<missing>"
                : ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern))
                    .Current.Value;
        }
        catch (ElementNotAvailableException)
        {
            return "<stale>";
        }
    }
}
