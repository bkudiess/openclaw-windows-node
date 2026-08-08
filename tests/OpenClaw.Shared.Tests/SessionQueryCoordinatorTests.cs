using OpenClaw.Shared;
using Xunit;

namespace OpenClaw.Shared.Tests;

public sealed class SessionQueryCoordinatorTests
{
    [Theory]
    [InlineData(1000, 10)]
    [InlineData(2000, 20)]
    public async Task RecentLoad_PagesInHundreds_ToBoundedMaximum(int total, int expectedPages)
    {
        var offsets = new List<int>();
        using var coordinator = new SessionQueryCoordinator((request, _) =>
        {
            var offset = request.Offset ?? 0;
            offsets.Add(offset);
            var count = Math.Min(SessionQueryCoordinator.PageSize, total - offset);
            return Task.FromResult(Page(
                Enumerable.Range(offset, count).Select(Session),
                offset,
                offset + count < total ? offset + count : null,
                offset + count < total));
        }, TimeSpan.Zero);

        var snapshot = await coordinator.LoadRecentAsync(new SessionQuery { IncludeBackground = true });

        Assert.Equal(total, snapshot.Sessions.Count);
        Assert.Equal(expectedPages, snapshot.PagesRead);
        Assert.Equal(Enumerable.Range(0, expectedPages).Select(i => i * 100), offsets);
    }

    [Fact]
    public async Task Paging_DeduplicatesKeys_AndKeepsLaterMutation()
    {
        using var coordinator = new SessionQueryCoordinator((request, _) =>
        {
            if (request.Offset == 0)
            {
                return Task.FromResult(Page(
                    Enumerable.Range(0, 100).Select(Session),
                    0, 100, true));
            }
            var rows = Enumerable.Range(100, 99).Select(Session).Prepend(
                new SessionInfo { Key = "agent:main:0", Label = "mutated" });
            return Task.FromResult(Page(rows, 100, null, false));
        }, TimeSpan.Zero);

        var snapshot = await coordinator.LoadRecentAsync(new SessionQuery { IncludeBackground = true });

        Assert.Equal(199, snapshot.Sessions.Count);
        Assert.Equal("mutated", Assert.Single(snapshot.Sessions, s => s.Key == "agent:main:0").Label);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Paging_StopsOnMalformedOrStalledNextOffset(int nextOffset)
    {
        var calls = 0;
        using var coordinator = new SessionQueryCoordinator((request, _) =>
        {
            calls++;
            return Task.FromResult(Page(
                Enumerable.Range(0, 100).Select(Session),
                request.Offset ?? 0,
                nextOffset,
                true));
        }, TimeSpan.Zero);

        var snapshot = await coordinator.LoadRecentAsync();

        Assert.Equal(1, calls);
        Assert.Equal(1, snapshot.PagesRead);
    }

    [Fact]
    public async Task Paging_StopsWhenNextOffsetRepeatsSeenCursor()
    {
        var calls = 0;
        using var coordinator = new SessionQueryCoordinator((request, _) =>
        {
            calls++;
            var offset = request.Offset ?? 0;
            return Task.FromResult(Page(
                Enumerable.Range(offset, 100).Select(Session),
                offset,
                offset == 0 ? 100 : 0,
                true));
        }, TimeSpan.Zero);

        var snapshot = await coordinator.LoadRecentAsync();

        Assert.Equal(2, calls);
        Assert.Equal(2, snapshot.PagesRead);
    }

    [Fact]
    public async Task HiddenOnlyPage_DoesNotStopRawPaging()
    {
        using var coordinator = new SessionQueryCoordinator((request, _) =>
        {
            if (request.Offset == 0)
            {
                var hidden = Enumerable.Range(0, 100).Select(i => new SessionInfo
                {
                    Key = $"agent:main:subagent:{i}",
                    Label = "Background",
                    Classification = "subagent",
                    IsBackground = true,
                });
                return Task.FromResult(Page(hidden, 0, 100, true));
            }
            return Task.FromResult(Page([new SessionInfo { Key = "agent:main:visible" }], 100, null, false));
        }, TimeSpan.Zero);

        var snapshot = await coordinator.LoadRecentAsync();

        Assert.Equal(2, snapshot.PagesRead);
        Assert.Equal("agent:main:visible", Assert.Single(snapshot.Sessions).Key);
    }

    [Fact]
    public async Task AdvanceConnectionGeneration_CancelsInFlightAndRejectsLateResponse()
    {
        var response = new TaskCompletionSource<SessionListResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var coordinator = new SessionQueryCoordinator((_, _) => response.Task, TimeSpan.Zero);
        var query = coordinator.LoadRecentAsync();

        coordinator.AdvanceConnectionGeneration();
        response.TrySetResult(Page([Session(1)], 0, null, false));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
        Assert.Empty(coordinator.ClearSearch().Sessions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleExpectedGeneration_IsRejectedBeforeQueryEnrollment(bool search)
    {
        var calls = 0;
        using var coordinator = new SessionQueryCoordinator(
            (_, _) =>
            {
                Interlocked.Increment(ref calls);
                return Task.FromResult(Page([], 0, null, false));
            },
            TimeSpan.Zero);
        coordinator.AdvanceConnectionGeneration();

        var query = new SessionQuery { Search = search ? "needle" : null };
        var operation = search
            ? coordinator.SearchAsync(
                query,
                expectedConnectionGeneration: 0)
            : coordinator.LoadRecentAsync(
                query,
                expectedConnectionGeneration: 0);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ConcurrentRecentLoads_LatestIdentityWins()
    {
        var firstResponse = new TaskCompletionSource<SessionListResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var call = 0;
        using var coordinator = new SessionQueryCoordinator((_, _) =>
        {
            call++;
            return call == 1
                ? firstResponse.Task
                : Task.FromResult(Page([new SessionInfo { Key = "agent:main:latest" }], 0, null, false));
        }, TimeSpan.Zero);
        var first = coordinator.LoadRecentAsync();
        var latest = await coordinator.LoadRecentAsync();
        firstResponse.TrySetResult(Page([new SessionInfo { Key = "agent:main:stale" }], 0, null, false));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal("agent:main:latest", Assert.Single(latest.Sessions).Key);
        Assert.Equal(
            latest.Sessions.Select(session => session.Key),
            coordinator.ClearSearch().Sessions.Select(session => session.Key));
    }

    [Fact]
    public async Task ConcurrentRecentCompletionAndSupersession_DoesNotRaceCtsDisposal()
    {
        for (var iteration = 0; iteration < 250; iteration++)
        {
            var firstResponse = new TaskCompletionSource<SessionListResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var call = 0;
            using var coordinator = new SessionQueryCoordinator((_, _) =>
            {
                return Interlocked.Increment(ref call) == 1
                    ? firstResponse.Task
                    : Task.FromResult(Page([Session(2)], 0, null, false));
            }, TimeSpan.Zero);
            var first = coordinator.LoadRecentAsync();

            var complete = Task.Run(async () =>
            {
                await start.Task;
                firstResponse.TrySetResult(Page([Session(1)], 0, null, false));
            });
            var supersede = Task.Run(async () =>
            {
                await start.Task;
                return await coordinator.LoadRecentAsync();
            });

            start.TrySetResult();
            await complete;
            _ = await supersede;
            var firstException = await Record.ExceptionAsync(() => first);
            Assert.True(
                firstException is null or OperationCanceledException,
                $"Unexpected supersession exception: {firstException}");
        }
    }

    [Fact]
    public async Task Dispose_CancelsClientOwnedQuery()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new SessionQueryCoordinator(async (_, cancellationToken) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Page([], 0, null, false);
        }, TimeSpan.Zero);
        var query = coordinator.LoadRecentAsync();
        await started.Task;

        coordinator.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query);
    }

    [Fact]
    public async Task Search_FindsOnlyRowReturnedOnNinthPage()
    {
        using var coordinator = new SessionQueryCoordinator((request, _) =>
        {
            var offset = request.Offset ?? 0;
            var rows = offset == 800
                ? new[] { new SessionInfo { Key = "agent:main:900", Label = "needle" } }
                : Array.Empty<SessionInfo>();
            return Task.FromResult(Page(rows, offset, offset < 800 ? offset + 100 : null, offset < 800));
        }, TimeSpan.Zero);

        var snapshot = await coordinator.SearchAsync(new SessionQuery { Search = "needle" });

        Assert.Equal(9, snapshot.PagesRead);
        Assert.Equal("agent:main:900", Assert.Single(snapshot.Sessions).Key);
    }

    [Fact]
    public async Task Search_WhenServerRejectsSearch_PagesAndFindsRowOutsideNewestHundred()
    {
        using var coordinator = new SessionQueryCoordinator((request, _) =>
        {
            var offset = request.Offset ?? 0;
            var rows = Enumerable.Range(offset, 100)
                .Select(index => new SessionInfo
                {
                    Key = $"agent:main:{index}",
                    Label = index == 900 ? "Needle outside first page" : $"Session {index}",
                });
            return Task.FromResult(Page(
                rows,
                offset,
                offset < 900 ? offset + 100 : null,
                offset < 900,
                SessionListRequestFields.Search));
        }, TimeSpan.Zero);

        var snapshot = await coordinator.SearchAsync(new SessionQuery
        {
            Search = "needle",
            IncludeBackground = true,
        });

        Assert.Equal(10, snapshot.PagesRead);
        Assert.Equal(SessionSearchExecutionMode.LegacyLocal, snapshot.SearchExecutionMode);
        Assert.Equal(SessionListRequestFields.Search, snapshot.UnsupportedRequestFields);
        Assert.Equal("agent:main:900", Assert.Single(snapshot.Sessions).Key);
    }

    [Fact]
    public async Task Search_DebounceCancelsOldQuery_AndLatestWins()
    {
        var requests = new List<string?>();
        using var coordinator = new SessionQueryCoordinator((request, _) =>
        {
            lock (requests) requests.Add(request.Search);
            return Task.FromResult(Page(
                [new SessionInfo { Key = $"agent:main:{request.Search}" }],
                0, null, false));
        }, TimeSpan.FromMilliseconds(50));

        var old = coordinator.SearchAsync(new SessionQuery { Search = "old" });
        var latest = coordinator.SearchAsync(new SessionQuery { Search = "latest" });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        var snapshot = await latest;
        Assert.Equal("agent:main:latest", Assert.Single(snapshot.Sessions).Key);
        Assert.Equal(["latest"], requests);
    }

    [Fact]
    public async Task Search_PinsSelectedLocalSession_WhenServerOmitsIt()
    {
        using var coordinator = new SessionQueryCoordinator(
            (_, _) => Task.FromResult(Page([Session(1)], 0, null, false)),
            TimeSpan.Zero);

        var snapshot = await coordinator.SearchAsync(new SessionQuery
        {
            Search = "one",
            IncludeBackground = true,
            PinnedSessions = [new SessionInfo { Key = "agent:main:selected", Label = "Selected" }],
        });

        Assert.Contains(snapshot.Sessions, s => s.Key == "agent:main:selected");
    }

    [Fact]
    public async Task ClearSearch_RestoresCoherentRecentSnapshotMetadata()
    {
        using var coordinator = new SessionQueryCoordinator(
            (_, _) => Task.FromResult(Page([Session(1)], 0, null, false)),
            TimeSpan.Zero);
        var recent = await coordinator.LoadRecentAsync();
        _ = await coordinator.SearchAsync(new SessionQuery { Search = "other" });

        var restored = coordinator.ClearSearch();

        Assert.NotSame(recent, restored);
        Assert.Equal(recent.Sessions.Select(session => session.Key), restored.Sessions.Select(session => session.Key));
        Assert.Equal(recent.PagesRead, restored.PagesRead);
        Assert.Equal(recent.UnsupportedRequestFields, restored.UnsupportedRequestFields);
    }

    [Fact]
    public async Task ClearSearch_UsesServerIdentityAndReprojectsClientOnlyState()
    {
        using var coordinator = new SessionQueryCoordinator((request, _) =>
            Task.FromResult(Page(
                [
                    new SessionInfo
                    {
                        Key = $"agent:{request.AgentId}:recent",
                        Label = request.ConfiguredAgentsOnly == true ? "Configured" : "All",
                    },
                    new SessionInfo
                    {
                        Key = $"agent:{request.AgentId}:background",
                        Classification = "subagent",
                        IsBackground = true,
                    },
                ],
                0, null, false)),
            TimeSpan.Zero);
        var pinned = new SessionInfo { Key = "agent:a:pinned", Label = "Pinned" };
        var recentQuery = new SessionQuery
        {
            AgentId = "a",
            ConfiguredAgentsOnly = true,
            Archived = false,
            IncludeBackground = true,
            PinnedSessions = [pinned],
        };
        var recent = await coordinator.LoadRecentAsync(recentQuery);
        _ = await coordinator.SearchAsync(new SessionQuery
        {
            AgentId = "a",
            Search = "other",
            ConfiguredAgentsOnly = true,
            Archived = false,
            IncludeBackground = true,
            PinnedSessions = [pinned],
        });

        Assert.Equal(
            recent.Sessions.Select(session => session.Key),
            coordinator.ClearSearch(recentQuery).Sessions.Select(session => session.Key));
        Assert.Empty(coordinator.ClearSearch(new SessionQuery
        {
            AgentId = "b",
            ConfiguredAgentsOnly = true,
            Archived = false,
            IncludeBackground = true,
            PinnedSessions = [],
        }).Sessions);
        Assert.Empty(coordinator.ClearSearch(new SessionQuery
        {
            AgentId = "a",
            ConfiguredAgentsOnly = false,
            Archived = false,
            IncludeBackground = true,
            PinnedSessions = [],
        }).Sessions);
        Assert.Empty(coordinator.ClearSearch(new SessionQuery
        {
            AgentId = "a",
            ConfiguredAgentsOnly = true,
            Archived = true,
            IncludeBackground = true,
            PinnedSessions = [],
        }).Sessions);
        var foregroundOnly = coordinator.ClearSearch(new SessionQuery
        {
            AgentId = "a",
            ConfiguredAgentsOnly = true,
            Archived = false,
            IncludeBackground = false,
            PinnedSessions = [],
        });
        Assert.Equal("agent:a:recent", Assert.Single(foregroundOnly.Sessions).Key);
        var withoutPin = coordinator.ClearSearch(new SessionQuery
        {
            AgentId = "a",
            ConfiguredAgentsOnly = true,
            Archived = false,
            IncludeBackground = true,
            PinnedSessions = [],
        });
        Assert.Equal(
            ["agent:a:recent", "agent:a:background"],
            withoutPin.Sessions.Select(session => session.Key));
        var replacementPin = new SessionInfo { Key = "agent:a:replacement", Label = "Replacement" };
        var withReplacementPin = coordinator.ClearSearch(new SessionQuery
        {
            AgentId = "a",
            ConfiguredAgentsOnly = true,
            Archived = false,
            IncludeBackground = true,
            PinnedSessions = [replacementPin],
        });
        Assert.Equal(
            ["agent:a:recent", "agent:a:background", replacementPin.Key],
            withReplacementPin.Sessions.Select(session => session.Key));
    }

    [Fact]
    public async Task ClearSearch_ReprojectsChangedSameKeyCurrentPin()
    {
        var selectedKey = "agent:main:selected";
        using var coordinator = new SessionQueryCoordinator(
            (_, _) => Task.FromResult(Page(
                [
                    new SessionInfo { Key = "agent:main:before", Label = "Before" },
                    new SessionInfo
                    {
                        Key = selectedKey,
                        Label = "Stale server label",
                        CurrentActivity = "Stale server activity",
                        DerivedTitle = "Stale server title",
                    },
                    new SessionInfo { Key = "agent:main:after", Label = "After" },
                ],
                0,
                null,
                false)),
            TimeSpan.Zero);
        var oldPin = new SessionInfo
        {
            Key = selectedKey,
            Label = "Old label",
            CurrentActivity = "Old activity",
            DerivedTitle = "Old title",
        };
        _ = await coordinator.LoadRecentAsync(new SessionQuery
        {
            IncludeBackground = true,
            PinnedSessions = [oldPin],
        });
        var currentPin = new SessionInfo
        {
            Key = oldPin.Key,
            Label = "Current label",
            CurrentActivity = "Current activity",
            DerivedTitle = "Current title",
        };

        var restored = coordinator.ClearSearch(new SessionQuery
        {
            IncludeBackground = true,
            PinnedSessions = [currentPin],
        });

        var selected = Assert.Single(restored.Sessions, session => session.Key == currentPin.Key);
        Assert.Equal(
            ["agent:main:before", selectedKey, "agent:main:after"],
            restored.Sessions.Select(session => session.Key));
        Assert.Equal("Current label", selected.Label);
        Assert.Equal("Current activity", selected.CurrentActivity);
        Assert.Equal("Current title", selected.DerivedTitle);
    }

    [Fact]
    public async Task LegacySearch_FiltersSafeDisplayFieldsWithoutMatchingRawKeys()
    {
        using var coordinator = new SessionQueryCoordinator(
            (_, _) => Task.FromResult(new SessionListResult
            {
                Sessions =
                [
                    new SessionInfo
                    {
                        Key = "agent:main:telegram:main:direct:needle",
                        Label = "Unrelated",
                    },
                    new SessionInfo { Key = "agent:main:label", Label = "Project Needle" },
                    new SessionInfo
                    {
                        Key = "agent:main:presentation",
                        DerivedTitle = "Needle discussion",
                    },
                    new SessionInfo { Key = "agent:main:miss", DisplayName = "Different" },
                ],
                UnsupportedRequestFields = SessionListRequestFields.Search |
                                           SessionListRequestFields.Offset,
            }),
            TimeSpan.Zero);

        var snapshot = await coordinator.SearchAsync(new SessionQuery
        {
            Search = "  needle ",
            IncludeBackground = true,
        });

        Assert.Equal(SessionSearchExecutionMode.LegacyLocal, snapshot.SearchExecutionMode);
        Assert.Equal(
            ["agent:main:label", "agent:main:presentation"],
            snapshot.Sessions.Select(session => session.Key));
        Assert.DoesNotContain(
            snapshot.Sessions,
            session => session.Key == "agent:main:telegram:main:direct:needle");
    }

    [Fact]
    public async Task CompatibilitySearch_UsesCurrentSessionDisplayResolverWithoutUnsafeFields()
    {
        const string opaqueId = "01234567-89ab-cdef-0123-456789abcdef";
        using var coordinator = new SessionQueryCoordinator(
            (_, _) => Task.FromResult(new SessionListResult
            {
                Sessions =
                [
                    new SessionInfo
                    {
                        Key = $"agent:main:tui-{opaqueId}",
                        DisplayName = $"Terminal:{opaqueId}",
                    },
                    new SessionInfo { Key = "global" },
                    new SessionInfo { Key = "agent:ops:tui-safe", ExecNode = "buildbox" },
                    new SessionInfo
                    {
                        Key = "agent:main:explicit:safe",
                        SessionId = "secretneedle",
                        ParentSessionKey = "secretneedle",
                    },
                    new SessionInfo
                    {
                        Key = "agent:main:tui-duplicate",
                        Label = "Terminal session",
                    },
                    new SessionInfo
                    {
                        Key = "agent:main:explicit:visible",
                        Label = "Visible title",
                        Subject = "hiddenneedle",
                        Room = "hiddenneedle",
                        Space = "hiddenneedle",
                        OriginLabel = "hiddenneedle",
                    },
                ],
                UnsupportedRequestFields = SessionListRequestFields.Search |
                                           SessionListRequestFields.Offset,
            }),
            TimeSpan.Zero);

        var terminal = await coordinator.SearchAsync(new SessionQuery
        {
            Search = "terminal session",
            IncludeBackground = true,
        });
        var globalTitle = SessionDisplayResolver.Resolve(new SessionInfo { Key = "global" }).Title;
        var global = await coordinator.SearchAsync(new SessionQuery
        {
            Search = globalTitle,
            IncludeBackground = true,
        });
        var subtitle = await coordinator.SearchAsync(new SessionQuery
        {
            Search = "node buildbox",
            IncludeBackground = true,
        });
        var opaque = await coordinator.SearchAsync(new SessionQuery
        {
            Search = opaqueId,
            IncludeBackground = true,
        });
        var unsafeFields = await coordinator.SearchAsync(new SessionQuery
        {
            Search = "secretneedle",
            IncludeBackground = true,
        });
        var hiddenRawFields = await coordinator.SearchAsync(new SessionQuery
        {
            Search = "hiddenneedle",
            IncludeBackground = true,
        });

        Assert.Equal(3, terminal.Sessions.Count);
        Assert.Equal(
            terminal.Sessions.Count,
            terminal.Sessions.Select(session => session.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("global", Assert.Single(global.Sessions).Key);
        Assert.Equal("agent:ops:tui-safe", Assert.Single(subtitle.Sessions).Key);
        Assert.Empty(opaque.Sessions);
        Assert.Empty(unsafeFields.Sessions);
        Assert.Empty(hiddenRawFields.Sessions);
    }

    [Fact]
    public async Task LegacySearch_IsCappedPinsSelectionAndClearRestoresRecent()
    {
        var rows = Enumerable.Range(0, 2500)
            .Select(index => new SessionInfo
            {
                Key = $"agent:main:{index}",
                DisplayName = index % 2 == 0 ? "Needle" : "Other",
            })
            .ToArray();
        using var coordinator = new SessionQueryCoordinator(
            (_, _) => Task.FromResult(new SessionListResult
            {
                Sessions = rows,
                UnsupportedRequestFields = SessionListRequestFields.Search |
                                           SessionListRequestFields.Offset,
            }),
            TimeSpan.Zero);
        var recentQuery = new SessionQuery { IncludeBackground = true };
        var recent = await coordinator.LoadRecentAsync(recentQuery);
        var pinned = new SessionInfo { Key = "agent:main:selected", Label = "Selected" };

        var search = await coordinator.SearchAsync(new SessionQuery
        {
            Search = "needle",
            IncludeBackground = true,
            PinnedSessions = [pinned],
        });
        var restored = coordinator.ClearSearch(recentQuery);

        Assert.Equal(SessionQueryCoordinator.MaximumMaterializedSessions, recent.Sessions.Count);
        Assert.Equal(1001, search.Sessions.Count);
        Assert.Contains(search.Sessions, session => session.Key == pinned.Key);
        Assert.Equal(
            recent.Sessions.Select(session => session.Key),
            restored.Sessions.Select(session => session.Key));
        Assert.Equal(SessionSearchExecutionMode.None, restored.SearchExecutionMode);
    }

    [Fact]
    public async Task ServerSearch_ReportsServerExecutionMode()
    {
        using var coordinator = new SessionQueryCoordinator(
            (_, _) => Task.FromResult(Page([Session(1)], 0, null, false)),
            TimeSpan.Zero);

        var snapshot = await coordinator.SearchAsync(new SessionQuery { Search = "one" });

        Assert.Equal(SessionSearchExecutionMode.Server, snapshot.SearchExecutionMode);
    }

    [Fact]
    public async Task LegacyUnboundedResponse_IsCappedDefensively()
    {
        using var coordinator = new SessionQueryCoordinator(
            (_, _) => Task.FromResult(new SessionListResult
            {
                Sessions = Enumerable.Range(0, 2500).Select(Session).ToArray(),
                UnsupportedRequestFields = SessionListRequestFields.Offset,
            }),
            TimeSpan.Zero);

        var snapshot = await coordinator.LoadRecentAsync(new SessionQuery { IncludeBackground = true });

        Assert.Equal(SessionQueryCoordinator.MaximumMaterializedSessions, snapshot.Sessions.Count);
        Assert.Equal(1, snapshot.PagesRead);
        Assert.True(snapshot.UsedCompatibilityFallback);
    }

    private static SessionInfo Session(int index) => new() { Key = $"agent:main:{index}" };

    private static SessionListResult Page(
        IEnumerable<SessionInfo> sessions,
        int offset,
        int? nextOffset,
        bool hasMore,
        SessionListRequestFields unsupportedRequestFields = SessionListRequestFields.None)
    {
        var rows = sessions.ToArray();
        return new SessionListResult
        {
            Sessions = rows,
            Count = rows.Length,
            TotalCount = 2000,
            LimitApplied = 100,
            Offset = offset,
            NextOffset = nextOffset,
            HasMore = hasMore,
            UnsupportedRequestFields = unsupportedRequestFields,
        };
    }
}
