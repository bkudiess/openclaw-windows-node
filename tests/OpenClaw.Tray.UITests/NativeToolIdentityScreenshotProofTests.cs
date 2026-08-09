using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using Xunit.Abstractions;

namespace OpenClaw.Tray.UITests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class NativeToolIdentityScreenshotCollection :
    ICollectionFixture<NativeToolIdentityScreenshotFixture>
{
    public const string Name = "Native tool identity screenshot";
}

public sealed class NativeToolIdentityScreenshotFixture : IDisposable
{
    private readonly AccessibilityAppFixture _app = new(initializeAxe: false);

    public IntPtr HubWindowHandle => _app.HubWindowHandle;

    public Task NavigateAsync(string pageTag, string pageMarkerAutomationId) =>
        _app.NavigateAsync(pageTag, pageMarkerAutomationId);

    public string? CaptureNativeChatVisualIfRequested() =>
        _app.CaptureNativeChatVisualIfRequested();

    public void Dispose() => _app.Dispose();
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HistoryCollisionScreenshotCollection :
    ICollectionFixture<HistoryCollisionScreenshotFixture>
{
    public const string Name = "History collision screenshot";
}

public sealed class HistoryCollisionScreenshotFixture : IDisposable
{
    private readonly AccessibilityAppFixture _app = new(
        initializeAxe: false,
        chatFixture: "history-collision",
        nativeChatProofSurface: "HistoryCollision",
        nativeChatProofElementPrefix: "ChatToolCall_",
        nativeChatProofText: "flattened output owned by history-tool-1",
        nativeChatProofCaptureCount: 3);

    public IntPtr HubWindowHandle => _app.HubWindowHandle;

    public string ProductionProductVersion => _app.ProductionProductVersion;

    public Task NavigateAsync(string pageTag, string pageMarkerAutomationId) =>
        _app.NavigateAsync(pageTag, pageMarkerAutomationId);

    public string? CaptureCollisionVisualIfRequested() =>
        _app.CaptureNativeChatVisualIfRequested();

    public void Dispose() => _app.Dispose();
}

[Collection(NativeToolIdentityScreenshotCollection.Name)]
public sealed class NativeToolIdentityScreenshotProofTests
{
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);

    private readonly NativeToolIdentityScreenshotFixture _app;
    private readonly ITestOutputHelper _output;

    public NativeToolIdentityScreenshotProofTests(
        NativeToolIdentityScreenshotFixture app,
        ITestOutputHelper output)
    {
        _app = app;
        _output = output;
    }

    [Fact]
    [Trait("Category", "Accessibility")]
    public async Task SyntheticNativeRows_RenderTrustedIdentitySafeInputAndTruthfulFallback()
    {
        await _app.NavigateAsync("chat", "ChatComposerInput");

        var proof = new List<string>
        {
            $"head={Environment.GetEnvironmentVariable("OPENCLAW_UI_PROOF_HEAD") ?? "local"}",
            $"dirty={Environment.GetEnvironmentVariable("OPENCLAW_UI_PROOF_DIRTY") ?? "unknown"}",
        };

        ExpandToolActivity(proof);
        ExpandTool("Tool call Bash. Done.", proof);
        ExpandTool("Tool call Apply Patch. Done.", proof);
        ExpandTool("Tool call Tool. Done.", proof);

        var names = WaitForExpectedText();
        Assert.Contains(
            "command: powershell -NoProfile -Command Get-ChildItem .\\src",
            names);
        Assert.Contains(
            "file_path: src\\OpenClaw.Chat\\ChatTimelineReducer.cs",
            names);
        Assert.Contains("command: [redacted]", names);
        Assert.Contains("Tool input", names);
        Assert.DoesNotContain(
            names,
            name => name.Contains("proof-run-", StringComparison.Ordinal));
        Assert.DoesNotContain(
            names,
            name => name.Contains("super-secret-value", StringComparison.Ordinal));

        proof.Add("UIA input=\"command: powershell -NoProfile -Command Get-ChildItem .\\src\"");
        proof.Add("UIA input=\"file_path: src\\OpenClaw.Chat\\ChatTimelineReducer.cs\"");
        proof.Add("UIA input=\"command: [redacted]\"");
        proof.Add("forbidden proof-run-=absent");
        proof.Add("forbidden super-secret-value=absent");

        if (_app.CaptureNativeChatVisualIfRequested() is { } screenshotPath)
        {
            proof.Add(
                $"screenshot={Path.GetFileName(screenshotPath)} " +
                $"bytes={new FileInfo(screenshotPath).Length}");
        }

        proof.Add("result=pass");
        foreach (var line in proof)
            _output.WriteLine(line);
        WriteProofArtifactIfRequested(proof);
    }

    private void ExpandToolActivity(ICollection<string> proof)
    {
        var activity = WaitForElement(
            element => element.Current.AutomationId.StartsWith(
                "ChatToolActivity_",
                StringComparison.Ordinal),
            "native tool activity to appear");
        Expand(activity, activity.Current.Name, proof);
    }

    private void ExpandTool(string automationName, ICollection<string> proof)
    {
        var element = WaitForElement(new PropertyCondition(
            AutomationElement.NameProperty,
            automationName));
        Expand(element, automationName, proof);
    }

    private static void Expand(
        AutomationElement element,
        string automationName,
        ICollection<string> proof)
    {
        Assert.True(
            element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var rawPattern),
            $"{automationName} did not expose ExpandCollapsePattern.");
        var pattern = Assert.IsType<ExpandCollapsePattern>(rawPattern);
        if (pattern.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
            pattern.Expand();
        proof.Add($"UIA expanded=\"{automationName}\"");
    }

    private HashSet<string> WaitForExpectedText()
    {
        HashSet<string>? names = null;
        WaitUntil(() =>
        {
            var hub = AutomationElement.FromHandle(_app.HubWindowHandle);
            names = hub.FindAll(TreeScope.Descendants, Condition.TrueCondition)
                .Cast<AutomationElement>()
                .SelectMany(ReadTextCandidates)
                .ToHashSet(StringComparer.Ordinal);
            return names.Contains("Tool input")
                && names.Contains("command: powershell -NoProfile -Command Get-ChildItem .\\src")
                && names.Contains("file_path: src\\OpenClaw.Chat\\ChatTimelineReducer.cs")
                && names.Contains("command: [redacted]");
        }, "expanded native tool inputs to appear");
        return names!;
    }

    private static IEnumerable<string> ReadTextCandidates(AutomationElement element)
    {
        var name = element.Current.Name;
        if (!string.IsNullOrWhiteSpace(name))
            yield return name;

        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var rawPattern)
            && rawPattern is TextPattern textPattern)
        {
            var text = textPattern.DocumentRange.GetText(-1).TrimEnd('\r', '\n');
            if (!string.IsNullOrWhiteSpace(text)
                && !string.Equals(text, name, StringComparison.Ordinal))
            {
                yield return text;
            }
        }

    }

    private AutomationElement WaitForElement(Condition condition)
    {
        AutomationElement? element = null;
        WaitUntil(() =>
        {
            var hub = AutomationElement.FromHandle(_app.HubWindowHandle);
            element = hub.FindFirst(TreeScope.Descendants, condition);
            return element is not null;
        }, "native tool row to appear");
        return element!;
    }

    private AutomationElement WaitForElement(
        Func<AutomationElement, bool> predicate,
        string description)
    {
        AutomationElement? element = null;
        WaitUntil(() =>
        {
            var hub = AutomationElement.FromHandle(_app.HubWindowHandle);
            element = hub.FindAll(TreeScope.Descendants, Condition.TrueCondition)
                .Cast<AutomationElement>()
                .FirstOrDefault(predicate);
            return element is not null;
        }, description);
        return element!;
    }

    private static void WaitUntil(Func<bool> predicate, string description)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < UiTimeout)
        {
            try
            {
                if (predicate())
                    return;
            }
            catch (ElementNotAvailableException)
            {
                // React navigation and flyouts replace their automation subtrees.
            }
            Thread.Sleep(100);
        }
        throw new TimeoutException($"Timed out waiting for {description}.");
    }

    private static void WriteProofArtifactIfRequested(IEnumerable<string> proof)
    {
        var path = Environment.GetEnvironmentVariable("OPENCLAW_UI_PROOF_ARTIFACT_PATH");
        if (string.IsNullOrWhiteSpace(path))
            return;

        path = Path.GetFullPath(path, Environment.CurrentDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, proof);
    }
}

[Collection(HistoryCollisionScreenshotCollection.Name)]
public sealed class HistoryCollisionScreenshotProofTests
{
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);

    private readonly HistoryCollisionScreenshotFixture _app;
    private readonly ITestOutputHelper _output;

    public HistoryCollisionScreenshotProofTests(
        HistoryCollisionScreenshotFixture app,
        ITestOutputHelper output)
    {
        _app = app;
        _output = output;
    }

    [Fact]
    [Trait("Category", "Accessibility")]
    public async Task StructuredAndFlattenedHistoryCollision_RendersTwoDistinctRows()
    {
        await _app.NavigateAsync("chat", "ChatComposerInput");

        var head = Environment.GetEnvironmentVariable("OPENCLAW_UI_PROOF_HEAD");
        var dirty = Environment.GetEnvironmentVariable("OPENCLAW_UI_PROOF_DIRTY");
        var proofArtifactPath = Environment.GetEnvironmentVariable(
            "OPENCLAW_UI_PROOF_ARTIFACT_PATH");
        var configuredScreenshotPath = Environment.GetEnvironmentVariable(
            "OPENCLAW_UI_SCREENSHOT_PATH");
        if (!string.IsNullOrWhiteSpace(proofArtifactPath)
            || !string.IsNullOrWhiteSpace(configuredScreenshotPath))
        {
            Assert.Matches("^[0-9a-f]{40}$", head ?? string.Empty);
            Assert.Matches(
                "^sha256:[0-9A-Fa-f]{64}; files=4; base=[0-9a-f]{40}$",
                dirty ?? string.Empty);
            Assert.Contains(
                $".Sha.{head}.{head}",
                _app.ProductionProductVersion,
                StringComparison.Ordinal);
        }

        var proof = new List<string>
        {
            $"head={head ?? "local"}",
            $"dirty={dirty ?? "unknown"}",
            $"product-version={_app.ProductionProductVersion}",
            "proof-scope=production reducer, activity projection, and Reactor renderer; allocator=focused provider regression",
            "visual=two production tool cards plus the synthetic output text, composed without coordinate cropping",
        };

        var activity = ExpandToolActivity(proof);
        ExpandTool("Tool call Exec. Interrupted.", proof);
        ExpandTool("Tool call Bash. Done.", proof);

        var structuredText = WaitForSubtreeText(
            "Tool call Exec. Interrupted.",
            text => text.Contains(
                "command: verified structured id: history-tool-0"),
            "structured history row details to render");
        var syntheticText = WaitForSubtreeText(
            "Tool call Bash. Done.",
            text => text.Contains(
                    "command: synthetic flattened id: history-tool-1")
                && text.Contains("flattened output owned by history-tool-1"),
            "synthetic history row details to render");
        Assert.Contains("command: verified structured id: history-tool-0", structuredText);
        Assert.DoesNotContain("Tool output", structuredText);
        Assert.DoesNotContain("Tool error", structuredText);
        Assert.DoesNotContain("flattened output owned by history-tool-1", structuredText);
        Assert.Contains("command: synthetic flattened id: history-tool-1", syntheticText);
        Assert.Contains("Tool output", syntheticText);
        Assert.DoesNotContain("Tool error", syntheticText);
        Assert.Contains("flattened output owned by history-tool-1", syntheticText);

        var hub = AutomationElement.FromHandle(_app.HubWindowHandle);
        var toolRows = hub.FindAll(TreeScope.Descendants, Condition.TrueCondition)
            .Cast<AutomationElement>()
            .Where(element => element.Current.AutomationId.StartsWith(
                "ChatToolCall_",
                StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(2, toolRows.Length);
        Assert.Equal(
            2,
            toolRows.Select(row => row.Current.AutomationId)
                .Distinct(StringComparer.Ordinal)
                .Count());
        var hubBounds = hub.Current.BoundingRectangle;
        var activityBounds = activity.Current.BoundingRectangle;
        Assert.True(activityBounds.Width > 0 && activityBounds.Height > 0);
        Assert.False(activity.Current.IsOffscreen);
        Assert.True(
            activityBounds.Left >= hubBounds.Left
                && activityBounds.Right <= hubBounds.Right
                && activityBounds.Top >= hubBounds.Top
                && activityBounds.Bottom <= hubBounds.Bottom,
            "The tool activity was not fully contained by the visible Hub window.");
        Assert.All(toolRows, row =>
        {
            var rowBounds = row.Current.BoundingRectangle;
            Assert.True(rowBounds.Width > 0 && rowBounds.Height > 0);
            Assert.False(row.Current.IsOffscreen);
            Assert.True(
                rowBounds.Left >= activityBounds.Left
                    && rowBounds.Right <= activityBounds.Right
                    && rowBounds.Top >= activityBounds.Top
                    && rowBounds.Bottom <= activityBounds.Bottom,
                $"{row.Current.AutomationId} was not fully contained by the visible activity bounds.");
        });

        var structuredAutomationId = toolRows.Single(
            row => row.Current.Name == "Tool call Exec. Interrupted.")
            .Current.AutomationId;
        var syntheticAutomationId = toolRows.Single(
            row => row.Current.Name == "Tool call Bash. Done.")
            .Current.AutomationId;
        proof.Add("UIA tool-row-count=2");
        proof.Add(
            $"UIA structured=\"automationId={structuredAutomationId}; state=Interrupted; output=absent\"");
        proof.Add(
            $"UIA synthetic=\"automationId={syntheticAutomationId}; state=Done; output=flattened output owned by history-tool-1\"");

        if (_app.CaptureCollisionVisualIfRequested() is { } screenshotPath)
        {
            proof.Add(
                $"screenshot={Path.GetFileName(screenshotPath)} " +
                $"bytes={new FileInfo(screenshotPath).Length}");
        }

        proof.Add("result=pass");
        foreach (var line in proof)
            _output.WriteLine(line);
        WriteProofArtifactIfRequested(proof);
    }

    private AutomationElement ExpandToolActivity(ICollection<string> proof)
    {
        var activity = WaitForElement(
            element => element.Current.AutomationId.StartsWith(
                "ChatToolActivity_",
                StringComparison.Ordinal),
            "history collision activity to appear");
        Expand(activity, activity.Current.Name, proof);
        return activity;
    }

    private void ExpandTool(
        string automationName,
        ICollection<string> proof)
    {
        var element = WaitForElement(new PropertyCondition(
            AutomationElement.NameProperty,
            automationName));
        Expand(element, automationName, proof);
    }

    private static void Expand(
        AutomationElement element,
        string automationName,
        ICollection<string> proof)
    {
        Assert.True(
            element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var rawPattern),
            $"{automationName} did not expose ExpandCollapsePattern.");
        var pattern = Assert.IsType<ExpandCollapsePattern>(rawPattern);
        if (pattern.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
            pattern.Expand();
        proof.Add($"UIA expanded=\"{automationName}\"");
    }

    private static HashSet<string> ReadSubtreeText(AutomationElement root) =>
        root.FindAll(TreeScope.Subtree, Condition.TrueCondition)
            .Cast<AutomationElement>()
            .SelectMany(ReadTextCandidates)
            .ToHashSet(StringComparer.Ordinal);

    private HashSet<string> WaitForSubtreeText(
        string automationName,
        Func<HashSet<string>, bool> predicate,
        string description)
    {
        HashSet<string>? text = null;
        WaitUntil(() =>
        {
            var hub = AutomationElement.FromHandle(_app.HubWindowHandle);
            var element = hub.FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(
                    AutomationElement.NameProperty,
                    automationName));
            if (element is null)
                return false;

            text = ReadSubtreeText(element);
            return predicate(text);
        }, description);
        return text!;
    }

    private static IEnumerable<string> ReadTextCandidates(AutomationElement element)
    {
        var name = element.Current.Name;
        if (!string.IsNullOrWhiteSpace(name))
            yield return name;

        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var rawPattern)
            && rawPattern is TextPattern textPattern)
        {
            var text = textPattern.DocumentRange.GetText(-1).TrimEnd('\r', '\n');
            if (!string.IsNullOrWhiteSpace(text)
                && !string.Equals(text, name, StringComparison.Ordinal))
            {
                yield return text;
            }
        }
    }

    private AutomationElement WaitForElement(Condition condition)
    {
        AutomationElement? element = null;
        WaitUntil(() =>
        {
            var hub = AutomationElement.FromHandle(_app.HubWindowHandle);
            element = hub.FindFirst(TreeScope.Descendants, condition);
            return element is not null;
        }, "history collision tool row to appear");
        return element!;
    }

    private AutomationElement WaitForElement(
        Func<AutomationElement, bool> predicate,
        string description)
    {
        AutomationElement? element = null;
        WaitUntil(() =>
        {
            var hub = AutomationElement.FromHandle(_app.HubWindowHandle);
            element = hub.FindAll(TreeScope.Descendants, Condition.TrueCondition)
                .Cast<AutomationElement>()
                .FirstOrDefault(predicate);
            return element is not null;
        }, description);
        return element!;
    }

    private static void WaitUntil(Func<bool> predicate, string description)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < UiTimeout)
        {
            try
            {
                if (predicate())
                    return;
            }
            catch (ElementNotAvailableException)
            {
                // React navigation and flyouts replace their automation subtrees.
            }
            Thread.Sleep(100);
        }
        throw new TimeoutException($"Timed out waiting for {description}.");
    }

    private static void WriteProofArtifactIfRequested(IEnumerable<string> proof)
    {
        var path = Environment.GetEnvironmentVariable("OPENCLAW_UI_PROOF_ARTIFACT_PATH");
        if (string.IsNullOrWhiteSpace(path))
            return;

        path = Path.GetFullPath(path, Environment.CurrentDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, proof);
    }
}
