using OpenClaw.Shared;
using OpenClaw.Shared.Mxc;
using OpenClawTray.Presentation;
using OpenClawTray.Services;

namespace OpenClaw.Tray.Tests;

public sealed class SettingsSandboxPublicationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("OPENCLAW_REPO_ROOT") ?? Directory.GetCurrentDirectory(),
        ".mxc-test-runs", Guid.NewGuid().ToString("N"));
    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void TransferBudgetPersistsAndOnlyCommittedChangesAffectNewSnapshots()
    {
        var settings = new SettingsManager(_root);
        Assert.Equal(50L * 1024 * 1024, settings.SandboxMaxTransferBytes);
        settings.SaveOrThrow();
        var before = settings.SandboxPolicies.Capture();
        settings.SandboxMaxTransferBytes = 100L * 1024 * 1024;
        Assert.Same(before, settings.SandboxPolicies.Capture());
        settings.SaveOrThrow();
        var after = settings.SandboxPolicies.Capture();
        Assert.True(after.Revision > before.Revision);
        Assert.Equal(100L * 1024 * 1024, after.Settings.SandboxMaxTransferBytes);
        Assert.Equal(50L * 1024 * 1024, before.Settings.SandboxMaxTransferBytes);
        Assert.Equal(100L * 1024 * 1024, new SettingsManager(_root).SandboxMaxTransferBytes);
        Assert.Empty(settings.SandboxCustomFolders);
    }

    [Fact]
    public void UploadLocationPersistsInTheSamePolicyWithoutAddingFolderGrants()
    {
        var settings = new SettingsManager(_root);
        settings.SaveOrThrow();
        var before = settings.SandboxPolicies.Capture();
        var path = Path.Combine(_root, "uploads");
        settings.SandboxUploadStagingPath = path;
        Assert.Same(before, settings.SandboxPolicies.Capture());
        settings.SaveOrThrow();
        Assert.True(settings.SandboxPolicies.Capture().Revision > before.Revision);
        Assert.Equal(path, settings.SandboxPolicies.Capture().Settings.SandboxUploadStagingPath);
        Assert.Equal(path, new SettingsManager(_root).SandboxUploadStagingPath);
        Assert.Empty(settings.SandboxCustomFolders);
        Assert.False(Directory.Exists(path));
        Assert.Null(before.Settings.SandboxUploadStagingPath);
    }

    [Fact]
    public void UnpersistedEditsAndFailedAtomicReplaceNeverPublishRuntimeGrants()
    {
        var settings = new SettingsManager(_root);
        settings.SaveOrThrow();
        var original = settings.SandboxPolicies.Capture();
        settings.SystemRunAllowOutbound = true;
        Assert.Same(original, settings.SandboxPolicies.Capture());
        var path = Path.Combine(_root, "settings.json");
        var disk = File.ReadAllText(path);
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.True(Record.Exception(settings.SaveOrThrow) is IOException or UnauthorizedAccessException);
        Assert.Same(original, settings.SandboxPolicies.Capture());
        Assert.Equal(disk, File.ReadAllText(path));
        settings.SaveOrThrow();
        Assert.True(settings.SandboxPolicies.Capture().Revision > original.Revision);
        Assert.Equal(MxcRequestBuilder.Fingerprint(SettingsData.FromJson(File.ReadAllText(path))!),
            settings.SandboxPolicies.Capture().Fingerprint);
    }

    [Fact]
    public void FailedStoreEditRollsBackWithoutChangingCommittedPolicyOrUnknownFields()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "settings.json");
        File.WriteAllText(path, """{"UnknownFutureSetting":{"retain":true},"SystemRunAllowOutbound":false}""");
        var settings = new SettingsManager(_root);
        var original = settings.SandboxPolicies.Capture();
        using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.True(Record.Exception(() => settings.UpdateAndSave(() => settings.SystemRunAllowOutbound = true))
                is IOException or UnauthorizedAccessException);
        Assert.False(settings.SystemRunAllowOutbound);
        Assert.Same(original, settings.SandboxPolicies.Capture());
        settings.UpdateAndSave(() => settings.SystemRunAllowOutbound = true);
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(json.RootElement.GetProperty("UnknownFutureSetting").GetProperty("retain").GetBoolean());
        Assert.True(json.RootElement.GetProperty("SystemRunAllowOutbound").GetBoolean());
    }

    [Fact]
    public async Task PersistedRevisionControlsAdmissionBeforeSavedObserversFinish()
    {
        var settings = new SettingsManager(_root);
        settings.SaveOrThrow();
        var old = settings.SandboxPolicies.Capture();
        using var releaseNotification = new ManualResetEventSlim();
        var notified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        settings.Saved += (_, _) =>
        {
            notified.SetResult();
            Assert.True(releaseNotification.Wait(TimeSpan.FromSeconds(10)));
        };
        var saving = Task.Run(() => settings.UpdateAndSave(() => settings.SystemRunAllowOutbound = true));
        try
        {
            await notified.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var current = settings.SandboxPolicies.Capture();
            Assert.True(current.Revision > old.Revision);
            Assert.Equal(MxcRequestBuilder.Fingerprint(SettingsData.FromJson(
                File.ReadAllText(Path.Combine(_root, "settings.json")))!),
                current.Fingerprint);
            await Assert.ThrowsAsync<StaleNodeSandboxPolicyException>(() =>
                settings.SandboxPolicies.Submit(old, () => Task.FromResult(0), default));
            Assert.Equal(42, await settings.SandboxPolicies.Submit(current, () => Task.FromResult(42), default));
            settings.SystemRunAllowOutbound = false;
            Assert.Same(current, settings.SandboxPolicies.Capture());
        }
        finally { releaseNotification.Set(); await saving; }
    }

    [Fact]
    public async Task SavedCallbacksRunOutsideSettingsSerialization()
    {
        var settings = new SettingsManager(_root);
        var notification = 0;
        var firstNotified = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFirst = new ManualResetEventSlim();
        settings.Saved += (_, _) =>
        {
            if (Interlocked.Increment(ref notification) != 1) return;
            firstNotified.SetResult();
            Assert.True(releaseFirst.Wait(TimeSpan.FromSeconds(10)));
        };
        var first = Task.Run(settings.SaveOrThrow);
        try
        {
            await firstNotified.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = Task.Run(() => settings.UpdateAndSave(() => settings.SystemRunAllowOutbound = true));
            await second.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, notification);
            Assert.True(SettingsData.FromJson(File.ReadAllText(Path.Combine(_root, "settings.json")))!.SystemRunAllowOutbound);
        }
        finally { releaseFirst.Set(); await first; }
    }

    [Fact]
    public async Task AdmittedWorkRetainsItsRevisionWhileLaterSaveChangesNextAdmission()
    {
        var settings = new SettingsManager(_root);
        settings.SaveOrThrow();
        var admitted = settings.SandboxPolicies.Capture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = settings.SandboxPolicies.Submit(admitted, () =>
        {
            entered.SetResult();
            return completion.Task;
        }, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        settings.UpdateAndSave(() => settings.SandboxClipboard = SandboxClipboardMode.Both);
        Assert.False(run.IsCompleted);
        Assert.True(settings.SandboxPolicies.Capture().Revision > admitted.Revision);
        completion.SetResult(admitted.Revision);
        Assert.Equal(admitted.Revision, await run);
    }

    [Fact]
    public async Task StoreChangedCallbacksDoNotHoldWriterLockOrLoseConcurrentOrigins()
    {
        var settings = new SettingsManager(_root);
        using var store = new SettingsStore(settings, new ImmediateDispatcher());
        var firstOrigin = store.CreateOrigin();
        var secondOrigin = store.CreateOrigin();
        var events = new System.Collections.Concurrent.ConcurrentBag<SettingsChangedEventArgs>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        store.Changed += (_, args) =>
        {
            events.Add(args);
            if (!ReferenceEquals(args.Origin, firstOrigin)) return;
            entered.SetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        var first = Task.Run(() => store.Update(firstOrigin, editor => editor.NotificationSound = "first"));
        Task? second = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = Task.Run(() => store.Update(secondOrigin, editor => editor.NotifyHealth = false));
            await second.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(first.IsCompleted);
            Assert.Contains(events, e => ReferenceEquals(e.Origin, firstOrigin));
            Assert.Contains(events, e => ReferenceEquals(e.Origin, secondOrigin));
        }
        finally
        {
            release.Set();
            await Task.WhenAll(first, second ?? Task.CompletedTask);
        }
        Assert.Equal(2, events.Count);
        Assert.Equal(new long[] { 1, 2 }, events.Select(e => e.Version).Order());
        var disk = SettingsData.FromJson(File.ReadAllText(Path.Combine(_root, "settings.json")))!;
        Assert.Equal("first", disk.NotificationSound);
        Assert.False(disk.NotifyHealth);
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool HasThreadAccess => true;
        public bool TryEnqueue(Action action) { action(); return true; }
    }
}
