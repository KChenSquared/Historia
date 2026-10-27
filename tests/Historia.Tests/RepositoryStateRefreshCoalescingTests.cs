namespace Historia.Tests;

/// <summary>
/// Single-flight coalescing and incremental ref publishing.
/// Ensures callers arriving while a refresh executes share one trailing run, changes made while a read
/// runs are not lost, the refresh after an operation joins only a run that started after it, faults are not cached, joiner cancellation does not cancel shared runs, and branches
/// publish before tags finish.
/// </summary>
[Trait("tier", "git")]
// Serialized: one test watches the process-wide UnobservedTaskException, which any concurrently
// running test can raise.
[Collection("serial")]
public sealed class RepositoryStateRefreshCoalescingTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();
    private readonly TestRepositoryBuilder _repo = TestRepositoryBuilder.FromTemplate("empty-repo", _ => { });

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _repo.Dispose();
    }

    private static RepositoryStateService NewState(RepositoryContext ctx, FakeGitGateway gateway)
        => new(ctx, gateway: gateway);

    private static BranchInfo MakeBranch(string name, bool isCurrent = true) => new()
    {
        Name = name,
        CanonicalName = $"refs/heads/{name}",
        IsRemote = false,
        IsCurrent = isCurrent,
        TipCommitId = null
    };

    private static TagInfo MakeTag(string name, GitObjectId? id = null) => new()
    {
        Name = name,
        TargetCommitId = id ?? new GitObjectId("0123456789012345678901234567890123456789"),
        IsAnnotated = false
    };

    [Fact(Timeout = 10000)]
    public async Task Status_refresh_requested_after_the_read_began_sees_the_new_answer()
    {
        var gateway = new FakeGitGateway();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = Historia.Models.RepositoryStatus.Empty;

        gateway.OnGetStatus = async ct =>
        {
            var seen = answer;
            if (gateway.StatusCalls == 1)
            {
                entered.TrySetResult();
                await proceed.Task.WaitAsync(ct);
            }
            return seen;
        };

        var ctx = _host.Open(_repo.Path);
        var state = NewState(ctx, gateway);

        var taskA = state.RefreshStatusAsync();
        await entered.Task;

        answer = Historia.Models.RepositoryStatus.Empty with
        {
            Unstaged = [new FileStatusEntry { Path = "b.txt", Kind = FileChangeKind.Modified, IsBinary = false, IsSubmodule = false }],
            IsDirty = true,
        };
        var taskB = state.RefreshStatusAsync();

        proceed.TrySetResult();
        await Task.WhenAll(taskA, taskB);

        Assert.Equal(2, gateway.StatusCalls);
        Assert.Equal("b.txt", Assert.Single(state.Status.Unstaged).Path);
    }

    [Fact(Timeout = 10000)]
    public async Task Status_refresh_requested_during_the_info_read_sees_the_new_head()
    {
        var gateway = new FakeGitGateway();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentBranch = "main";

        gateway.OnGetRepositoryInfo = async ct =>
        {
            var branch = currentBranch;
            if (gateway.StatusCalls == 1 && !entered.Task.IsCompleted)
            {
                entered.TrySetResult();
                await proceed.Task.WaitAsync(ct);
            }
            return new RepositoryInfo
            {
                WorkingDirectory = gateway.RepositoryPath,
                GitDirectory = gateway.RepositoryPath + "\\.git",
                DisplayName = "fake",
                IsBare = false,
                IsHeadDetached = false,
                IsHeadUnborn = false,
                CurrentBranchName = branch,
            };
        };

        var ctx = _host.Open(_repo.Path);
        var state = NewState(ctx, gateway);

        var taskA = state.RefreshStatusAsync();
        await entered.Task;

        currentBranch = "feature";
        var taskB = state.RefreshStatusAsync();

        proceed.TrySetResult();
        await Task.WhenAll(taskA, taskB);

        Assert.Equal("feature", state.Repository.CurrentBranchName);
    }

    [Fact(Timeout = 10000)]
    public async Task Refs_refresh_requested_after_the_read_began_sees_the_new_branches()
    {
        var gateway = new FakeGitGateway();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var branches = (IReadOnlyList<BranchInfo>)[MakeBranch("main")];

        gateway.OnGetBranches = async ct =>
        {
            var seen = branches;
            if (gateway.BranchesCalls == 1)
            {
                entered.TrySetResult();
                await proceed.Task.WaitAsync(ct);
            }
            return seen;
        };

        var ctx = _host.Open(_repo.Path);
        var state = NewState(ctx, gateway);

        var taskA = state.RefreshRefsAsync();
        await entered.Task;

        branches = [MakeBranch("main"), MakeBranch("feature", isCurrent: false)];
        var taskB = state.RefreshRefsAsync();

        proceed.TrySetResult();
        await Task.WhenAll(taskA, taskB);

        Assert.Equal(2, gateway.BranchesCalls);
        Assert.Equal(2, state.Branches.Count);
        Assert.Contains(state.Branches, b => b.Name == "main");
        Assert.Contains(state.Branches, b => b.Name == "feature");
    }

    [Fact(Timeout = 10000)]
    public async Task A_burst_during_a_refs_refresh_queues_exactly_one_trailing_run()
    {
        var gateway = new FakeGitGateway();
        var branchesEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var branchesProceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        gateway.OnGetBranches = async ct =>
        {
            if (gateway.BranchesCalls == 1)
            {
                branchesEntered.TrySetResult();
                await branchesProceed.Task.WaitAsync(ct);
            }
            return [MakeBranch("main")];
        };

        var ctx = _host.Open(_repo.Path);
        var state = NewState(ctx, gateway);

        var task1 = state.RefreshRefsAsync();
        await branchesEntered.Task;

        // Five callers arrive while task1 is executing:
        var burst = Enumerable.Range(0, 5).Select(_ => state.RefreshRefsAsync()).ToArray();

        branchesProceed.TrySetResult();
        await Task.WhenAll([task1, .. burst]);

        Assert.Equal(2, gateway.BranchesCalls);
        Assert.Equal(2, gateway.TagsCalls);
    }

    [Fact(Timeout = 10000)]
    public async Task A_burst_during_a_status_refresh_queues_exactly_one_trailing_run()
    {
        var gateway = new FakeGitGateway();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        gateway.OnGetStatus = async ct =>
        {
            if (gateway.StatusCalls == 1)
            {
                entered.TrySetResult();
                await proceed.Task.WaitAsync(ct);
            }
            return Historia.Models.RepositoryStatus.Empty;
        };

        var ctx = _host.Open(_repo.Path);
        var state = NewState(ctx, gateway);

        var first = state.RefreshStatusAsync();
        await entered.Task;

        // Five callers arrive while the first run is executing:
        var burst = Enumerable.Range(0, 5).Select(_ => state.RefreshStatusAsync()).ToArray();

        proceed.TrySetResult();
        await Task.WhenAll([first, .. burst]);

        Assert.Equal(2, gateway.StatusCalls);
    }

    [Fact(Timeout = 10000)]
    public async Task Caller_arriving_mid_flight_gets_a_trailing_refresh()
    {
        var gateway = new FakeGitGateway();
        var tagsEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tagsProceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        gateway.OnGetTags = async ct =>
        {
            tagsEntered.TrySetResult();
            await tagsProceed.Task.WaitAsync(ct);
            return [];
        };

        var ctx = _host.Open(_repo.Path);
        var state = NewState(ctx, gateway);

        var task1 = state.RefreshRefsAsync();
        await tagsEntered.Task;

        // Call 2 arrives mid-flight:
        var task2 = state.RefreshRefsAsync();

        tagsProceed.TrySetResult();
        await task1;
        await task2;

        Assert.Equal(2, gateway.BranchesCalls);
        Assert.Equal(2, gateway.TagsCalls);
    }

    [Fact(Timeout = 10000)]
    public async Task Status_refresh_from_a_context_bound_thread_reads_git_on_another_thread()
    {
        var gateway = new FakeGitGateway();
        var entered = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var gatewayThread = -1;

        gateway.OnGetStatus = ct =>
        {
            gatewayThread = Environment.CurrentManagedThreadId;
            entered.Set();
            // Blocks synchronously, like a git start that runs its prefix inline would.
            release.Wait(TimeSpan.FromSeconds(5));
            return Task.FromResult(Historia.Models.RepositoryStatus.Empty);
        };

        var ctx = _host.Open(_repo.Path);
        var state = NewState(ctx, gateway);

        var callerThread = -1;
        Task? refresh = null;
        var caller = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            callerThread = Environment.CurrentManagedThreadId;
            refresh = state.RefreshStatusAsync();
        });
        caller.Start();

        // The caller returns while the gateway is still blocked, so the call did not run inline.
        Assert.True(caller.Join(TimeSpan.FromSeconds(5)), "RefreshStatusAsync blocked its caller");
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        Assert.False(refresh!.IsCompleted);
        Assert.NotEqual(callerThread, gatewayThread);

        release.Set();
        await refresh;
        Assert.Equal(1, gateway.StatusCalls);
    }

    // Ends one of Historia's own operations on the tab and returns the count it ended at.
    private static long EndOperation(RepositoryContext ctx)
    {
        ctx.BeginSelfOperation().Dispose();
        return ctx.CompletedOperations;
    }

    [Fact(Timeout = 10000)]
    public async Task The_refresh_after_an_operation_joins_a_run_started_after_the_operation_ended()
    {
        var gateway = new FakeGitGateway();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.OnGetBranches = async ct =>
        {
            entered.TrySetResult();
            await proceed.Task.WaitAsync(ct);
            return [MakeBranch("main")];
        };

        var ctx = _host.Open(_repo.Path);
        var state = NewState(ctx, gateway);

        // The operation's caller refreshes as soon as the operation returns.
        var operationEnd = EndOperation(ctx);
        var callerRefresh = state.RefreshRefsAsync();
        await entered.Task;

        // The operation's own refresh arrives while the caller's run executes.
        var operationRefresh = state.RefreshAfterOperationAsync(operationEnd, CancellationToken.None);

        proceed.TrySetResult();
        await Task.WhenAll(callerRefresh, operationRefresh);

        Assert.Equal(1, gateway.BranchesCalls);
        Assert.Equal(1, gateway.TagsCalls);
    }

    [Fact(Timeout = 10000)]
    public async Task The_refresh_after_an_operation_does_not_join_a_run_started_before_the_operation_ended()
    {
        var gateway = new FakeGitGateway();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.OnGetBranches = async ct =>
        {
            if (gateway.BranchesCalls == 1)
            {
                entered.TrySetResult();
                await proceed.Task.WaitAsync(ct);
            }
            return [MakeBranch("main")];
        };

        var ctx = _host.Open(_repo.Path);
        var state = NewState(ctx, gateway);

        var running = state.RefreshRefsAsync();
        await entered.Task;

        // The operation ends after the running read began, so that read may predate its result.
        var operationEnd = EndOperation(ctx);
        var operationRefresh = state.RefreshAfterOperationAsync(operationEnd, CancellationToken.None);

        proceed.TrySetResult();
        await Task.WhenAll(running, operationRefresh);

        Assert.Equal(2, gateway.BranchesCalls);
        Assert.Equal(2, gateway.TagsCalls);
    }

    [Fact(Timeout = 10000)]
    public async Task Faulted_refresh_is_not_cached_for_later_callers()
    {
        var gateway = new FakeGitGateway();
        var shouldThrow = true;
        gateway.OnGetBranches = ct =>
        {
            if (shouldThrow)
                throw new InvalidOperationException("simulated git fault");
            return Task.FromResult<IReadOnlyList<BranchInfo>>([MakeBranch("main")]);
        };

        var ctx = _host.Open(_repo.Path);
        var state = NewState(ctx, gateway);

        // First call fails:
        await Assert.ThrowsAsync<InvalidOperationException>(() => state.RefreshRefsAsync());

        // Later call after first call settled:
        shouldThrow = false;
        await state.RefreshRefsAsync();

        Assert.Equal(2, gateway.BranchesCalls);
        Assert.Single(state.Branches);
    }

    [Fact(Timeout = 10000)]
    public async Task A_queued_callers_cancellation_does_not_cancel_the_running_refresh()
    {
        var gateway = new FakeGitGateway();
        var branchesEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var branchesProceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        gateway.OnGetBranches = async ct =>
        {
            branchesEntered.TrySetResult();
            await branchesProceed.Task;
            return [MakeBranch("main")];
        };

        var ctx = _host.Open(_repo.Path);
        var state = NewState(ctx, gateway);

        var task1 = state.RefreshRefsAsync(CancellationToken.None);
        await branchesEntered.Task;

        using var joinerCts = new CancellationTokenSource();
        var task2 = state.RefreshRefsAsync(joinerCts.Token);

        // Cancel joiner's token:
        joinerCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task2);

        // Running run completes successfully:
        branchesProceed.TrySetResult();
        await task1;

        Assert.Equal(1, gateway.BranchesCalls);
        Assert.Single(state.Branches);
    }

    [Fact(Timeout = 10000)]
    public async Task When_every_waiter_gives_up_the_refs_refresh_is_cancelled()
    {
        var gateway = new FakeGitGateway();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.OnGetBranches = async ct =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return Array.Empty<BranchInfo>();
        };

        var ctx = _host.Open(_repo.Path);
        var state = NewState(ctx, gateway);

        using var cts = new CancellationTokenSource();
        var only = state.RefreshRefsAsync(cts.Token);
        await entered.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => only);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // The next refresh runs afresh rather than joining the cancelled one.
        gateway.OnGetBranches = _ => Task.FromResult<IReadOnlyList<BranchInfo>>([MakeBranch("main")]);
        await state.RefreshRefsAsync();
        Assert.Single(state.Branches);
    }

    [Fact(Timeout = 10000)]
    public async Task When_every_waiter_gives_up_the_status_refresh_is_cancelled()
    {
        var gateway = new FakeGitGateway();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway.OnGetStatus = async ct =>
        {
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return Historia.Models.RepositoryStatus.Empty;
        };

        var ctx = _host.Open(_repo.Path);
        var state = NewState(ctx, gateway);

        using var cts = new CancellationTokenSource();
        var only = state.RefreshStatusAsync(cts.Token);
        await entered.Task;
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => only);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact(Timeout = 10000)]
    public async Task A_waiter_without_a_token_keeps_the_refresh_running_when_the_others_leave()
    {
        var gateway = new FakeGitGateway();
        var blockA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceedA = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceedB = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledB = false;

        gateway.OnGetBranches = async ct =>
        {
            if (gateway.BranchesCalls == 1)
            {
                blockA.TrySetResult();
                await proceedA.Task.WaitAsync(ct);
            }
            else
            {
                blockB.TrySetResult();
                using (ct.Register(() => cancelledB = true))
                    await proceedB.Task.WaitAsync(ct);
            }
            return [MakeBranch("main")];
        };

        var ctx = _host.Open(_repo.Path);
        var state = NewState(ctx, gateway);

        var blockerA = state.RefreshRefsAsync();
        await blockA.Task;

        using var cts = new CancellationTokenSource();
        var leaver = state.RefreshRefsAsync(cts.Token);
        var stayer = state.RefreshRefsAsync(CancellationToken.None);

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaver);

        proceedA.TrySetResult();
        await blockerA;

        await blockB.Task;
        proceedB.TrySetResult();
        await stayer;

        Assert.False(cancelledB);
        Assert.Equal(2, gateway.BranchesCalls);
        Assert.Single(state.Branches);
    }

    [Fact(Timeout = 10000)]
    public async Task A_caller_that_already_gave_up_starts_no_refresh()
    {
        var gateway = new FakeGitGateway();
        var ctx = _host.Open(_repo.Path);
        var state = NewState(ctx, gateway);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => state.RefreshRefsAsync(new CancellationToken(canceled: true)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => state.RefreshStatusAsync(new CancellationToken(canceled: true)));

        Assert.Equal(0, gateway.BranchesCalls);
        Assert.Equal(0, gateway.StatusCalls);
    }

    [Fact(Timeout = 10000)]
    public async Task Branches_are_published_before_tags_are_read()
    {
        var gateway = new FakeGitGateway();
        var tagsEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tagsProceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        gateway.OnGetBranches = ct => Task.FromResult<IReadOnlyList<BranchInfo>>([
            MakeBranch("feature")
        ]);

        gateway.OnGetTags = async ct =>
        {
            tagsEntered.TrySetResult();
            await tagsProceed.Task.WaitAsync(ct);
            return [MakeTag("v1.0")];
        };

        var ctx = _host.Open(_repo.Path);
        var state = NewState(ctx, gateway);

        RefsChangedEventArgs? eventBeforeTagsRead = null;
        state.RefsChanged += (_, e) =>
        {
            eventBeforeTagsRead ??= e;
        };

        var refreshTask = state.RefreshRefsAsync();

        await tagsEntered.Task;

        // Branches published before tags are read:
        Assert.NotNull(eventBeforeTagsRead);
        Assert.Single(eventBeforeTagsRead!.Branches);
        Assert.Equal("feature", eventBeforeTagsRead.Branches[0].Name);
        Assert.Empty(eventBeforeTagsRead.Tags);

        Assert.Single(state.Branches);
        Assert.Equal("feature", state.Branches[0].Name);

        // Release tags:
        tagsProceed.TrySetResult();
        await refreshTask;

        Assert.Single(state.Tags);
        Assert.Equal("v1.0", state.Tags[0].Name);
    }

    [Fact(Timeout = 10000)]
    public async Task Joiner_cancellation_with_subsequent_fault_does_not_leak_unobserved_task_exception()
    {
        var gateway = new FakeGitGateway();
        var branchesEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var branchesProceed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        gateway.OnGetBranches = async ct =>
        {
            branchesEntered.TrySetResult();
            await branchesProceed.Task;
            throw new InvalidOperationException("simulated gateway failure after joiner canceled");
        };

        var ctx = _host.Open(_repo.Path);
        var state = NewState(ctx, gateway);

        using var joinerCts = new CancellationTokenSource();
        var task = state.RefreshRefsAsync(joinerCts.Token);
        await branchesEntered.Task;

        // Joiner cancels early:
        joinerCts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);

        // Underlying shared task completes with fault:
        branchesProceed.TrySetResult(true);

        // Wait brief tick for coalescer pump to finish:
        await Task.Delay(50);

        // Verify unobserved task exception is not raised on GC finalizer thread:
        var unobservedSeen = false;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            unobservedSeen = true;
            e.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.False(unobservedSeen, "Shared coalesced faulted task leaked an unobserved exception to the finalizer thread.");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    [Fact(Timeout = 10000)]
    public async Task Disposing_repository_context_during_status_refresh_completes_cleanly_without_ObjectDisposedException()
    {
        var gateway = new FakeGitGateway();
        var statusEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var statusProceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        gateway.OnGetStatus = async ct =>
        {
            statusEntered.TrySetResult();
            await statusProceed.Task.WaitAsync(ct);
            return Historia.Models.RepositoryStatus.Empty;
        };

        // Finalize earlier tests' faulted tasks now, so the check below sees only this test's.
        GC.Collect();
        GC.WaitForPendingFinalizers();

        var ctx = _host.Open(_repo.Path);
        var state = NewState(ctx, gateway);

        var refreshTask = state.RefreshStatusAsync(ctx.Lifetime);
        await statusEntered.Task;

        // Dispose context while refresh is mid-flight:
        var disposeTask = ctx.DisposeAsync().AsTask();

        // Release status gateway (which will observe cancellation from ctx.Lifetime):
        statusProceed.TrySetResult();

        // Refresh task should complete as canceled (or succeed gracefully), never fault with ObjectDisposedException
        try
        {
            await refreshTask;
        }
        catch (OperationCanceledException)
        {
            // Expected cancellation
        }

        await disposeTask;

        // Verify ctx.Lifetime remains accessible without throwing ObjectDisposedException
        var token = ctx.Lifetime;
        Assert.True(token.IsCancellationRequested);

        // Finalizer check
        var unobservedSeen = false;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            unobservedSeen = true;
            e.SetObserved();
        }
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.False(unobservedSeen);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
    }

    private sealed class FakeGitGateway : IGitGateway
    {
        public string RepositoryPath => "C:\\fake\\repo";

        public Func<CancellationToken, Task<IReadOnlyList<BranchInfo>>>? OnGetBranches { get; set; }
        public Func<CancellationToken, Task<IReadOnlyList<RemoteInfo>>>? OnGetRemotes { get; set; }
        public Func<CancellationToken, Task<IReadOnlyList<TagInfo>>>? OnGetTags { get; set; }
        public Func<CancellationToken, Task<Historia.Models.RepositoryStatus>>? OnGetStatus { get; set; }
        public Func<CancellationToken, Task<RepositoryInfo>>? OnGetRepositoryInfo { get; set; }

        public int BranchesCalls;
        public int RemotesCalls;
        public int TagsCalls;
        public int StatusCalls;

        public Task<IReadOnlyList<BranchInfo>> GetBranchesAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref BranchesCalls);
            return OnGetBranches != null ? OnGetBranches(ct) : Task.FromResult<IReadOnlyList<BranchInfo>>([]);
        }

        public Task<IReadOnlyList<RemoteInfo>> GetRemotesAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref RemotesCalls);
            return OnGetRemotes != null ? OnGetRemotes(ct) : Task.FromResult<IReadOnlyList<RemoteInfo>>([]);
        }

        public Task<IReadOnlyList<TagInfo>> GetTagsAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref TagsCalls);
            return OnGetTags != null ? OnGetTags(ct) : Task.FromResult<IReadOnlyList<TagInfo>>([]);
        }

        public Task<Historia.Models.RepositoryStatus> GetStatusAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref StatusCalls);
            return OnGetStatus != null ? OnGetStatus(ct) : Task.FromResult(Historia.Models.RepositoryStatus.Empty);
        }

        public Task<RepositoryInfo> GetRepositoryInfoAsync(CancellationToken ct = default)
        {
            return OnGetRepositoryInfo != null ? OnGetRepositoryInfo(ct) : Task.FromResult(new RepositoryInfo
            {
                WorkingDirectory = RepositoryPath,
                GitDirectory = RepositoryPath + "\\.git",
                DisplayName = "fake",
                IsBare = false,
                IsHeadDetached = false,
                IsHeadUnborn = false,
                CurrentBranchName = "main",
            });
        }

        public Task<AheadBehind?> GetDivergenceAsync(string local, string remote, CancellationToken ct = default) => Task.FromResult<AheadBehind?>(null);
        public Task<CommitDetail> GetCommitDetailAsync(GitObjectId id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<FileStatusEntry>> GetCommitFilesAsync(GitObjectId id, int parentIndex, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(string? Name, string? Email)> GetConfiguredIdentityAsync(CancellationToken ct = default) => Task.FromResult<(string?, string?)>((null, null));
        public Task<FileDiff> GetFileDiffAsync(DiffRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<(string Path, string? Patch)>> GetStagedPatchesAsync(bool againstHeadParent, long? maxPatchBytes = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(string? Name, string? Email)> GetGlobalIdentityAsync(CancellationToken ct = default) => Task.FromResult<(string?, string?)>((null, null));
        public Task<bool> GetGpgSignConfiguredAsync(CancellationToken ct = default) => Task.FromResult(false);

        public Task StageAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UnstageAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DiscardAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<GitObjectId> CommitAsync(CommitRequest request, CancellationToken ct = default) => throw new NotImplementedException();
        public Task MergeAsync(string refish, bool noFastForward, CancellationToken ct = default) => throw new NotImplementedException();
        public Task ResetAsync(string target, GitResetMode mode, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> CountCommitsLeavingAsync(string target, CancellationToken ct = default) => throw new NotImplementedException();

        public Task CheckoutAsync(string refName, GitCheckoutOptions options, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<BranchInfo> CreateBranchAsync(string name, string startPoint, bool checkout, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteBranchAsync(string name, bool force, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RenameBranchAsync(string oldName, string newName, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetUpstreamAsync(string branchName, string upstreamRef, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<TagInfo> CreateTagAsync(string name, string target, string? message, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteTagAsync(string name, CancellationToken ct = default) => throw new NotImplementedException();

        public Task AddRemoteAsync(string name, string url, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RemoveRemoteAsync(string name, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetRemoteUrlAsync(string name, string url, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> RenameRemoteAsync(string oldName, string newName, CancellationToken ct = default) => throw new NotImplementedException();
        public Task SetRemotePushUrlAsync(string name, string? pushUrl, CancellationToken ct = default) => throw new NotImplementedException();

        public Task FetchAsync(string remote, GitFetchOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotImplementedException();
        public Task PullAsync(string remote, string branch, GitPullOptions options, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotImplementedException();
        public Task PushAsync(string remote, PushSpec spec, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotImplementedException();
        public Task DeleteRemoteBranchAsync(string remote, string branch, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotImplementedException();
        public Task PruneRemoteAsync(string name, IProgress<GitProgress>? progress, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
