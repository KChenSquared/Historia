namespace Historia.Tests;

/// <summary>
/// Verifies the per-context write lock serializes mutating Git operations, allows concurrent readers,
/// rejects re-entrant calls, cancels waiters on tab lifetime expiration, and releases on exceptions.
/// </summary>
[Trait("tier", "git")]
public sealed class RepositoryWriteLockTests : IAsyncDisposable
{
    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
    }

    [Fact(Timeout = 30000)]
    public async Task Two_writers_serialize()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("write-lock-seed", b =>
        {
            b.WriteFile("a.txt", "initial\n").Stage();
            b.Commit("c1");
        });
        var ctx = _host.Open(repo.Path);

        var firstHolding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<string>();

        var writer1 = Task.Run(async () =>
        {
            await ctx.WithWriteLockAsync(async ct =>
            {
                lock (order) order.Add("writer1-enter");
                firstHolding.SetResult();
                await releaseFirst.Task;
                lock (order) order.Add("writer1-exit");
            });
        });

        await firstHolding.Task;

        var writer2 = Task.Run(async () =>
        {
            await ctx.WithWriteLockAsync(ct =>
            {
                lock (order) order.Add("writer2-enter");
                return Task.CompletedTask;
            });
        });

        await Task.Delay(50);
        lock (order)
        {
            Assert.DoesNotContain("writer2-enter", order);
        }

        releaseFirst.SetResult();
        await Task.WhenAll(writer1, writer2);

        lock (order)
        {
            Assert.Equal(["writer1-enter", "writer1-exit", "writer2-enter"], order);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task Reader_runs_while_writer_holds_write_lock()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("write-lock-seed", b =>
        {
            b.WriteFile("a.txt", "initial\n").Stage();
            b.Commit("c1");
        });
        var ctx = _host.Open(repo.Path);

        var writerHolding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWriter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var writerTask = Task.Run(async () =>
        {
            await ctx.WithWriteLockAsync(async ct =>
            {
                writerHolding.SetResult();
                await releaseWriter.Task;
            });
        });

        await writerHolding.Task;

        var status = await ctx.Gateway.GetStatusAsync();
        Assert.NotNull(status);

        releaseWriter.SetResult();
        await writerTask;
    }

    [Fact]
    public async Task Nested_acquire_throws_InvalidOperationException()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("write-lock-seed", b =>
        {
            b.WriteFile("a.txt", "initial\n").Stage();
            b.Commit("c1");
        });
        var ctx = _host.Open(repo.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await ctx.WithWriteLockAsync(async ct =>
            {
                await ctx.WithWriteLockAsync(innerCt => Task.CompletedTask, ct);
            });
        });
    }

    [Fact(Timeout = 30000)]
    public async Task Tab_close_cancels_waiter()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("write-lock-seed", b =>
        {
            b.WriteFile("a.txt", "initial\n").Stage();
            b.Commit("c1");
        });
        var ctx = _host.Open(repo.Path);

        var writer1Holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWriter1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var writer1 = Task.Run(async () =>
        {
            await ctx.WithWriteLockAsync(async ct =>
            {
                writer1Holding.SetResult();
                await releaseWriter1.Task;
            });
        });

        await writer1Holding.Task;

        var waiterCanceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = Task.Run(async () =>
        {
            try
            {
                await ctx.WithWriteLockAsync(ct => Task.CompletedTask);
            }
            catch (OperationCanceledException)
            {
                waiterCanceled.SetResult(true);
            }
        });

        // The close waits for the running writer, so it is not awaited until the writer is released.
        var disposeTask = ctx.DisposeAsync().AsTask();

        var wasCanceled = await waiterCanceled.Task;
        Assert.True(wasCanceled);

        releaseWriter1.SetResult();
        await writer1;
        await waiter;
        await disposeTask;
    }

    [Fact(Timeout = 30000)]
    public async Task Work_started_on_a_synchronization_context_runs_on_a_different_thread()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("write-lock-seed", b =>
        {
            b.WriteFile("a.txt", "initial\n").Stage();
            b.Commit("c1");
        });
        var ctx = _host.Open(repo.Path);

        using var pump = new PumpContext();
        var callerThread = 0;
        var workThread = 0;
        await pump.RunAsync(async () =>
        {
            callerThread = Environment.CurrentManagedThreadId;
            await ctx.WithWriteLockAsync(_ =>
            {
                workThread = Environment.CurrentManagedThreadId;
                return Task.CompletedTask;
            });
        });

        Assert.Equal(pump.ThreadId, callerThread);
        Assert.NotEqual(0, workThread);
        Assert.NotEqual(callerThread, workThread);
    }

    [Fact(Timeout = 30000)]
    public async Task Nested_acquire_from_a_synchronization_context_still_throws()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("write-lock-seed", b =>
        {
            b.WriteFile("a.txt", "initial\n").Stage();
            b.Commit("c1");
        });
        var ctx = _host.Open(repo.Path);

        using var pump = new PumpContext();
        await Assert.ThrowsAsync<InvalidOperationException>(() => pump.RunAsync(async () =>
        {
            await ctx.WithWriteLockAsync(async ct =>
            {
                await ctx.WithWriteLockAsync(_ => Task.CompletedTask, ct);
            });
        }));

        // The lock is still usable afterwards.
        await ctx.WithWriteLockAsync(_ => Task.CompletedTask);
    }

    [Fact(Timeout = 30000)]
    public async Task GitWriteScope_is_active_inside_work_and_cleared_after()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("write-lock-seed", b =>
        {
            b.WriteFile("a.txt", "initial\n").Stage();
            b.Commit("c1");
        });
        var ctx = _host.Open(repo.Path);

        Assert.False(GitWriteScope.IsActive);
        var activeInWork = false;
        var activeAfterAwaitInWork = false;
        var activeInTaskRun = false;
        await ctx.WithWriteLockAsync(async ct =>
        {
            activeInWork = GitWriteScope.IsActive;
            await Task.Delay(10, ct);
            activeAfterAwaitInWork = GitWriteScope.IsActive;
            activeInTaskRun = await Task.Run(() => GitWriteScope.IsActive, ct);
        });

        Assert.True(activeInWork);
        Assert.True(activeAfterAwaitInWork);
        Assert.True(activeInTaskRun);
        Assert.False(GitWriteScope.IsActive);

        // Cleared after the work throws as well.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ctx.WithWriteLockAsync<int>(_ => throw new InvalidOperationException("boom")));
        Assert.False(GitWriteScope.IsActive);
    }

    [Fact(Timeout = 30000)]
    public async Task GitWriteScope_is_active_and_cleared_when_started_from_a_synchronization_context()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("write-lock-seed", b =>
        {
            b.WriteFile("a.txt", "initial\n").Stage();
            b.Commit("c1");
        });
        var ctx = _host.Open(repo.Path);

        using var pump = new PumpContext();
        var activeInWork = false;
        var activeAfterOnCaller = true;
        await pump.RunAsync(async () =>
        {
            await ctx.WithWriteLockAsync(_ =>
            {
                activeInWork = GitWriteScope.IsActive;
                return Task.CompletedTask;
            });
            activeAfterOnCaller = GitWriteScope.IsActive;
        });

        Assert.True(activeInWork);
        Assert.False(activeAfterOnCaller);
    }

    /// <summary>A single-thread synchronization context that stands in for the UI thread.</summary>
    private sealed class PumpContext : SynchronizationContext, IDisposable
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _thread;

        public PumpContext()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "test-pump" };
            _thread.Start();
        }

        public int ThreadId => _thread.ManagedThreadId;

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public Task RunAsync(Func<Task> body)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(async _ =>
            {
                try
                {
                    await body();
                    tcs.SetResult();
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            }, null);
            return tcs.Task;
        }

        private void Loop()
        {
            SetSynchronizationContext(this);
            foreach (var (callback, state) in _queue.GetConsumingEnumerable())
                callback(state);
        }

        public void Dispose() => _queue.CompleteAdding();
    }

    [Fact]
    public async Task Lock_released_on_exception()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("write-lock-seed", b =>
        {
            b.WriteFile("a.txt", "initial\n").Stage();
            b.Commit("c1");
        });
        var ctx = _host.Open(repo.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await ctx.WithWriteLockAsync<int>(ct => throw new InvalidOperationException("boom"));
        });

        var acquired = false;
        await ctx.WithWriteLockAsync(ct =>
        {
            acquired = true;
            return Task.CompletedTask;
        });

        Assert.True(acquired);
    }

    [Fact]
    public async Task PerfLog_records_wait_and_hold_marks()
    {
        // Flow-local: setting PerfLog.Enabled would turn on perf logging for every parallel test.
        PerfLog.OverrideEnabledForTesting(true);
        try
        {
            using var repo = TestRepositoryBuilder.FromTemplate("write-lock-seed", b =>
            {
                b.WriteFile("a.txt", "initial\n").Stage();
                b.Commit("c1");
            });
            var ctx = _host.Open(repo.Path);

            PerfLog.Flush(TimeSpan.FromSeconds(10));
            var startLineCount = File.Exists(PerfLog.LogPath)
                ? File.ReadAllLines(PerfLog.LogPath).Length
                : 0;

            await ctx.WithWriteLockAsync(async ct =>
            {
                await Task.Delay(15, ct);
            });

            Assert.True(PerfLog.Flush(TimeSpan.FromSeconds(10)));
            var lines = File.ReadAllLines(PerfLog.LogPath).Skip(startLineCount).ToList();
            Assert.Contains(lines, l => l.Contains("write lock hold") && l.Contains(nameof(PerfLog_records_wait_and_hold_marks)));
        }
        finally
        {
            PerfLog.OverrideEnabledForTesting(null);
        }
    }

    [Fact(Timeout = 30000)]
    public async Task CurrentWriteHolder_names_the_holding_operation_while_held_and_is_null_after()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("write-lock-seed", b =>
        {
            b.WriteFile("a.txt", "initial\n").Stage();
            b.Commit("c1");
        });
        var ctx = _host.Open(repo.Path);
        Assert.Null(ctx.CurrentWriteHolder);

        (string Label, TimeSpan Held)? seenWhileHeld = null;
        await ctx.WithWriteLockAsync(async ct =>
        {
            await Task.Delay(60, ct);
            seenWhileHeld = ctx.CurrentWriteHolder;
        }, label: "Commit");

        Assert.NotNull(seenWhileHeld);
        Assert.Equal("Commit", seenWhileHeld.Value.Label);
        Assert.True(seenWhileHeld.Value.Held >= TimeSpan.FromMilliseconds(20), $"held {seenWhileHeld.Value.Held}");
        Assert.Null(ctx.CurrentWriteHolder);
    }

    [Fact(Timeout = 30000)]
    public async Task CurrentWriteHolder_is_cleared_when_the_work_throws()
    {
        using var repo = TestRepositoryBuilder.FromTemplate("write-lock-seed", b =>
        {
            b.WriteFile("a.txt", "initial\n").Stage();
            b.Commit("c1");
        });
        var ctx = _host.Open(repo.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await ctx.WithWriteLockAsync<int>(ct => throw new InvalidOperationException("boom"), label: "Doomed"));

        Assert.Null(ctx.CurrentWriteHolder);
    }

    [Fact(Timeout = 30000)]
    public async Task A_waiters_perf_mark_names_the_holder_it_waited_behind()
    {
        // Flow-local: setting PerfLog.Enabled would turn on perf logging for every parallel test.
        PerfLog.OverrideEnabledForTesting(true);
        try
        {
            using var repo = TestRepositoryBuilder.FromTemplate("write-lock-seed", b =>
            {
                b.WriteFile("a.txt", "initial\n").Stage();
                b.Commit("c1");
            });
            var ctx = _host.Open(repo.Path);

            PerfLog.Flush(TimeSpan.FromSeconds(10));
            var startLineCount = File.Exists(PerfLog.LogPath)
                ? File.ReadAllLines(PerfLog.LogPath).Length
                : 0;

            var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var holder = Task.Run(() => ctx.WithWriteLockAsync(async ct =>
            {
                holding.SetResult();
                await release.Task;
            }, label: "SlowHolderOp"));
            await holding.Task;

            var waiter = Task.Run(() => ctx.WithWriteLockAsync(ct => Task.CompletedTask, label: "Waiter"));
            await Task.Delay(100);
            release.SetResult();
            await Task.WhenAll(holder, waiter);

            Assert.True(PerfLog.Flush(TimeSpan.FromSeconds(10)));
            var lines = File.ReadAllLines(PerfLog.LogPath).Skip(startLineCount).ToList();
            Assert.Contains(lines, l => l.Contains("write lock wait") && l.Contains("behind SlowHolderOp"));
        }
        finally
        {
            PerfLog.OverrideEnabledForTesting(null);
        }
    }
}
