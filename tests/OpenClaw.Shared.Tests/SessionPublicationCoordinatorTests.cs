using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class SessionPublicationCoordinatorTests
{
    [Fact]
    public async Task RefreshBurst_DoesNotCancelFirstPublication_AndCoalescesOneTrailingRefresh()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var publications = new List<IReadOnlyList<SessionInfo>>();
        using var coordinator = new SessionPublicationCoordinator(
            async (_, _) =>
            {
                var call = Interlocked.Increment(ref calls);
                if (call == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                }
                return Result(new SessionInfo
                {
                    Key = "agent:main:current",
                    Label = call == 1 ? "First publication" : "Trailing publication",
                });
            },
            (_, sessions) => sessions.ToArray(),
            sessions => publications.Add(sessions));

        var first = coordinator.RequestRefreshAsync();
        await firstStarted.Task;
        var burst = Enumerable.Range(0, 100)
            .Select(_ => coordinator.RequestRefreshAsync())
            .ToArray();

        releaseFirst.TrySetResult();
        await first;
        await Task.WhenAll(burst);

        Assert.Equal(2, calls);
        Assert.Equal(2, publications.Count);
        Assert.Equal("First publication", Assert.Single(publications[0]).Label);
        Assert.Equal("Trailing publication", Assert.Single(publications[1]).Label);
    }

    [Fact]
    public async Task Publication_IsBounded_Deduplicated_AndKeepsLaterMutation()
    {
        var rows = Enumerable.Range(0, 150)
            .Select(index => new SessionInfo { Key = $"agent:main:{index}", Label = $"Session {index}" })
            .Append(new SessionInfo { Key = "agent:main:0", Label = "Mutated" })
            .ToArray();
        IReadOnlyList<SessionInfo>? publication = null;
        using var coordinator = new SessionPublicationCoordinator(
            (_, _) => Task.FromResult(new SessionListResult { Sessions = rows }),
            (_, sessions) => sessions.ToArray(),
            sessions => publication = sessions);

        await coordinator.RequestRefreshAsync();

        Assert.NotNull(publication);
        Assert.Equal(SessionPublicationCoordinator.MaximumPublishedSessions, publication.Count);
        Assert.Equal(
            publication.Count,
            publication.Select(session => session.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("Mutated", Assert.Single(publication, session => session.Key == "agent:main:0").Label);
        Assert.DoesNotContain(publication, session => session.Key == "agent:main:149");
    }

    [Fact]
    public async Task AdvanceConnectionGeneration_CancelsLatePublication_AndAllowsCurrentRefresh()
    {
        var staleResponse = new TaskCompletionSource<SessionListResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var publications = new List<IReadOnlyList<SessionInfo>>();
        using var coordinator = new SessionPublicationCoordinator(
            (_, _) => Interlocked.Increment(ref calls) == 1
                ? staleResponse.Task
                : Task.FromResult(Result(new SessionInfo
                {
                    Key = "agent:main:current",
                    Label = "Current",
                })),
            (_, sessions) => sessions.ToArray(),
            sessions => publications.Add(sessions));
        var stale = coordinator.RequestRefreshAsync();

        coordinator.AdvanceConnectionGeneration();
        staleResponse.TrySetResult(Result(new SessionInfo
        {
            Key = "agent:main:stale",
            Label = "Stale",
        }));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stale);
        await coordinator.RequestRefreshAsync();

        Assert.Equal(2, calls);
        Assert.Equal("agent:main:current", Assert.Single(Assert.Single(publications)).Key);
    }

    [Fact]
    public async Task StaleExpectedGeneration_IsRejectedBeforeFetchEnrollment()
    {
        var calls = 0;
        using var coordinator = new SessionPublicationCoordinator(
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(Result());
            },
            (_, sessions) => sessions.ToArray(),
            _ => { });
        coordinator.AdvanceConnectionGeneration();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.RequestRefreshAsync(expectedConnectionGeneration: 0));

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task PublicationCallback_DoesNotHoldCoordinatorLock_AndGenerationAdvanceCancelsCompletion()
    {
        SessionPublicationCoordinator coordinator = null!;
        var publicationCount = 0;
        coordinator = new SessionPublicationCoordinator(
            (_, _) => Task.FromResult(Result(new SessionInfo { Key = "agent:main:first" })),
            (_, sessions) => sessions.ToArray(),
            _ =>
            {
                if (Interlocked.Increment(ref publicationCount) != 1)
                    return;
                var advance = Task.Run(coordinator.AdvanceConnectionGeneration);
                if (!advance.Wait(TimeSpan.FromSeconds(2)))
                    throw new TimeoutException("Generation advance blocked behind the publication callback.");
            });
        using (coordinator)
        {
            var stale = coordinator.RequestRefreshAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stale);
            await coordinator.RequestRefreshAsync();
        }

        Assert.Equal(2, publicationCount);
    }

    [Fact]
    public async Task AlternatingScopeBurst_CoalescesToOneTrailingRefreshUsingLatestScope()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requestedAgents = new List<string?>();
        using var coordinator = new SessionPublicationCoordinator(
            async (request, _) =>
            {
                requestedAgents.Add(request.AgentId);
                if (requestedAgents.Count == 1)
                {
                    firstStarted.TrySetResult();
                    await releaseFirst.Task;
                }
                return Result(new SessionInfo { Key = $"agent:{request.AgentId}:session" });
            },
            (_, sessions) => sessions.ToArray(),
            _ => { });

        var first = coordinator.RequestRefreshAsync("first");
        await firstStarted.Task;
        var burst = Enumerable.Range(0, 100)
            .Select(index => coordinator.RequestRefreshAsync(index % 2 == 0 ? "even" : "odd"))
            .ToArray();

        releaseFirst.TrySetResult();
        await first;
        await Task.WhenAll(burst);

        Assert.Equal(["first", "odd"], requestedAgents);
    }

    private static SessionListResult Result(params SessionInfo[] sessions) =>
        new() { Sessions = sessions };
}
