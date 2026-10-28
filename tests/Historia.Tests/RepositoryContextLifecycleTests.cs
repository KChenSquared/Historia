namespace Historia.Tests;

/// <summary>
/// Lifecycle tests for <see cref="RepositoryContext"/>: open/close, lifetime cancellation,
/// self-operation refresh suppression, and write lock behaviour across tab closure.
/// </summary>
[Trait("tier", "git")]
public sealed class RepositoryContextLifecycleTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    [Fact]
    public async Task Open_and_close_completes_cleanly()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("lifecycle-open-seed", b =>
        {
            b.WriteFile("f.txt", "1\n").Stage();
            b.Commit("initial");
        });

        var ctx = _host.Open(repo.Path);
        Assert.False(ctx.IsBare);
        Assert.Equal(Path.TrimEndingDirectorySeparator(repo.Path), Path.TrimEndingDirectorySeparator(ctx.RepositoryPath));
        Assert.False(ctx.Lifetime.IsCancellationRequested);

        await ctx.DisposeAsync();
    }

    [Fact]
    public async Task Closing_cancels_lifetime()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("lifecycle-lifetime-seed", b =>
        {
            b.WriteFile("f.txt", "1\n").Stage();
            b.Commit("initial");
        });

        var ctx = _host.Open(repo.Path);
        Assert.False(ctx.Lifetime.IsCancellationRequested);

        await ctx.DisposeAsync();
        Assert.True(ctx.Lifetime.IsCancellationRequested);
    }

    [Fact]
    public async Task BeginSelfOperation_tracks_depth_and_scope()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("lifecycle-selfop-seed", b =>
        {
            b.WriteFile("f.txt", "1\n").Stage();
            b.Commit("initial");
        });

        await using var ctx = _host.Open(repo.Path);
        Assert.False(ctx.IsSelfOperationInProgress);

        using (ctx.BeginSelfOperation())
        {
            Assert.True(ctx.IsSelfOperationInProgress);
        }

        Assert.False(ctx.IsSelfOperationInProgress);
    }

    [Fact]
    public async Task Write_lock_waiter_is_cancelled_on_context_close()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("lifecycle-writelock-seed", b =>
        {
            b.WriteFile("f.txt", "1\n").Stage();
            b.Commit("initial");
        });

        var ctx = _host.Open(repo.Path);
        var holdTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquiredTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstHolder = Task.Run(async () =>
        {
            await ctx.WithWriteLockAsync(async _ =>
            {
                acquiredTcs.SetResult();
                await holdTcs.Task;
            });
        });

        await acquiredTcs.Task;

        var secondWaiterStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWaiter = Task.Run(async () =>
        {
            secondWaiterStarted.SetResult();
            await ctx.WithWriteLockAsync(async _ => await Task.CompletedTask);
        });

        await secondWaiterStarted.Task;
        await Task.Delay(50); // Give secondWaiter time to enter wait

        // Closing the context cancels Lifetime, which aborts the waiting second call. The close
        // itself waits for the first holder, so it is released before the close is awaited.
        var disposeTask = ctx.DisposeAsync().AsTask();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await secondWaiter);

        holdTcs.SetResult();
        await firstHolder;
        await disposeTask;
    }

    [Fact(Timeout = 60000)]
    public async Task Closing_while_a_write_holds_the_lock_waits_for_it_and_the_write_completes()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("lifecycle-closewait-seed", b =>
        {
            b.WriteFile("f.txt", "1\n").Stage();
            b.Commit("initial");
        });

        var ctx = _host.Open(repo.Path);
        var acquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetimeCancelledInWork = false;

        var write = Task.Run(() => ctx.WithWriteLockAsync(async ct =>
        {
            acquired.SetResult();
            await release.Task;
            lifetimeCancelledInWork = ct.IsCancellationRequested;
            return 42;
        }));
        await acquired.Task;

        var dispose = ctx.DisposeAsync().AsTask();
        await Task.Delay(300);
        Assert.False(dispose.IsCompleted, "close finished while a write still held the lock");
        Assert.True(ctx.Lifetime.IsCancellationRequested);

        release.SetResult();
        Assert.Equal(42, await write);
        await dispose;
        Assert.True(lifetimeCancelledInWork);
        Assert.Null(ctx.CurrentWriteHolder);
    }

    [Fact(Timeout = 60000)]
    public async Task Closing_from_inside_a_write_does_not_wait_for_itself()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("lifecycle-closeinside-seed", b =>
        {
            b.WriteFile("f.txt", "1\n").Stage();
            b.Commit("initial");
        });

        var ctx = _host.Open(repo.Path);

        await ctx.WithWriteLockAsync(async _ =>
        {
            await ctx.DisposeAsync();
        });

        Assert.True(ctx.Lifetime.IsCancellationRequested);
        Assert.Null(ctx.CurrentWriteHolder);
    }

    [Fact(Timeout = 60000)]
    public async Task A_write_requested_after_close_is_cancelled_without_running()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("lifecycle-afterclose-seed", b =>
        {
            b.WriteFile("f.txt", "1\n").Stage();
            b.Commit("initial");
        });

        var ctx = _host.Open(repo.Path);
        await ctx.DisposeAsync();

        var ran = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ctx.WithWriteLockAsync(_ => { ran = true; return Task.CompletedTask; }));
        Assert.False(ran);
    }

    [Fact]
    public async Task Nested_write_lock_throws()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("lifecycle-nested-seed", b =>
        {
            b.WriteFile("f.txt", "1\n").Stage();
            b.Commit("initial");
        });

        await using var ctx = _host.Open(repo.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await ctx.WithWriteLockAsync(async _ =>
            {
                await ctx.WithWriteLockAsync(async _ => await Task.CompletedTask);
            });
        });
    }
}
