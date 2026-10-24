namespace Historia.Tests;

[Trait("tier", "unit")]
public class CmdrQueueViewModelTests
{
    private const string Sha = "90236c7a1b2c3d4e5f60718293a4b5c6d7e8f901";

    private sealed class FixedTime(DateTimeOffset utcNow, TimeZoneInfo zone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
        public override TimeZoneInfo LocalTimeZone => zone;
    }

    private sealed class Harness : IDisposable
    {
        public FakeCmdrQueueService Queue { get; } = new();
        public FakeSettingsService Settings { get; }
        public FakeControlNavigator Navigator { get; } = new();
        public NavigationBus Nav { get; } = new();
        public FakeNotificationService Notifications { get; } = new();
        public List<string> Routes { get; } = [];
        public CmdrQueueViewModel Vm { get; }

        public Harness(AppSettings? settings = null, params CmdrQueueItem[] items)
        {
            Settings = new FakeSettingsService(settings);
            foreach (var item in items)
                Queue.Enqueue(item);
            Queue.Calls.Clear();
            Nav.NavigationRequested += Routes.Add;
            Vm = new CmdrQueueViewModel(Queue, Settings, Navigator, Nav, Notifications, new ImmediateUiDispatcher());
        }

        public void Dispose() => Vm.Dispose();
    }

    private static CmdrQueueItem Item(string note, DateTimeOffset? at = null) => new()
    {
        CreatedAt = at ?? DateTimeOffset.UtcNow,
        RepoRoot = @"C:\repos\app",
        RepoName = "app",
        Target = ControlVerb.Show,
        Rev = Sha,
        Note = note,
    };

    private static List<string> Texts(CmdrQueueViewModel vm) => vm.Items.Select(i => i.DisplayText).ToList();

    [Fact]
    public void Items_are_oldest_first_by_default()
    {
        using var h = new Harness(null, Item("one"), Item("two"), Item("three"));

        Assert.False(h.Vm.NewestFirst);
        Assert.Equal("Oldest first", h.Vm.SortText);
        Assert.Equal(["one", "two", "three"], Texts(h.Vm));
        Assert.False(h.Vm.IsEmpty);
    }

    [Fact]
    public void The_saved_order_is_used_at_construction()
    {
        using var h = new Harness(new AppSettings { CmdrQueueNewestFirst = true }, Item("one"), Item("two"));

        Assert.True(h.Vm.NewestFirst);
        Assert.Equal(["two", "one"], Texts(h.Vm));
    }

    [Fact]
    public async Task Toggling_the_sort_reorders_and_persists_it()
    {
        using var h = new Harness(null, Item("one"), Item("two"), Item("three"));
        var rows = h.Vm.Items.ToList();

        await h.Vm.ToggleSortCommand.ExecuteAsync(null);

        Assert.True(h.Vm.NewestFirst);
        Assert.Equal("Newest first", h.Vm.SortText);
        Assert.True(h.Settings.Current.CmdrQueueNewestFirst);
        Assert.Equal(["three", "two", "one"], Texts(h.Vm));
        Assert.All(h.Vm.Items, row => Assert.Contains(row, rows));

        await h.Vm.ToggleSortCommand.ExecuteAsync(null);

        Assert.False(h.Settings.Current.CmdrQueueNewestFirst);
        Assert.Equal(["one", "two", "three"], Texts(h.Vm));
    }

    [Fact]
    public void A_new_item_lands_at_the_end_of_the_chosen_order()
    {
        using var h = new Harness(new AppSettings { CmdrQueueNewestFirst = true }, Item("one"));

        h.Queue.Enqueue(Item("two"));

        Assert.Equal(["two", "one"], Texts(h.Vm));
    }

    [Fact]
    public void Remove_calls_the_queue_and_asks_nothing()
    {
        var keep = Item("keep");
        var drop = Item("drop");
        using var h = new Harness(null, keep, drop);

        h.Vm.RemoveCommand.Execute(h.Vm.Items[1]);

        Assert.Equal([$"Remove:{drop.Id}"], h.Queue.Calls);
        Assert.Equal(["keep"], Texts(h.Vm));
        Assert.Empty(h.Navigator.Calls);
        // The list has no way to ask: it is not given a dialog service.
        Assert.DoesNotContain(typeof(CmdrQueueViewModel).GetConstructors().SelectMany(c => c.GetParameters()),
            p => p.ParameterType == typeof(IDialogService));
    }

    [Fact]
    public void Removing_the_last_item_shows_the_empty_state()
    {
        var only = Item("only");
        using var h = new Harness(null, only);

        h.Vm.RemoveCommand.Execute(h.Vm.Items[0]);

        Assert.True(h.Vm.IsEmpty);
        Assert.Empty(h.Vm.Items);
    }

    [Fact]
    public async Task Open_marks_the_item_seen_before_navigating_to_its_stored_target()
    {
        var item = Item("look") with
        {
            Target = ControlVerb.Diff,
            Paths = ["src/a.cs"],
            Line = 12,
        };
        using var h = new Harness(null, item);
        var callsAtNavigate = new List<string>();
        h.Navigator.OnNavigate = _ => callsAtNavigate.AddRange(h.Queue.Calls);

        await h.Vm.OpenCommand.ExecuteAsync(h.Vm.Items[0]);

        Assert.Equal([$"MarkSeen:{item.Id}"], callsAtNavigate);
        var target = Assert.Single(h.Navigator.Calls).Target;
        Assert.Equal(item.RepoRoot, target.RepoPath);
        Assert.Equal(item.RepoRoot, target.Cwd);
        Assert.Equal(ControlVerb.Diff, target.Verb);
        Assert.Equal(Sha, target.Rev);
        Assert.Equal(["src/a.cs"], target.Paths);
        Assert.Equal(12, target.Line);
        Assert.True(h.Vm.Items[0].Seen);
        Assert.Empty(h.Notifications.Published);
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("settings", true)]
    [InlineData("repo", false)]
    [InlineData("rebase?onto=main", false)]
    [InlineData("profiles", false)]
    public async Task A_successful_open_shows_the_repository_over_home_and_settings(string route, bool expectRepo)
    {
        using var h = new Harness(null, Item("look"));
        h.Nav.ReportRoute(route);

        await h.Vm.OpenCommand.ExecuteAsync(h.Vm.Items[0]);

        if (expectRepo)
            Assert.Equal(["repo"], h.Routes);
        else
            Assert.Empty(h.Routes);
    }

    [Fact]
    public async Task A_second_open_cancels_the_first()
    {
        var first = Item("first");
        var second = Item("second");
        using var h = new Harness(null, first, second);
        var held = new TaskCompletionSource<(ControlResponse, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Navigator.Next(async (_, ct) =>
        {
            await using (ct.Register(() => held.TrySetCanceled(ct)))
                return await held.Task;
        });

        var firstOpen = h.Vm.OpenCommand.ExecuteAsync(h.Vm.Items[0]);
        Assert.False(firstOpen.IsCompleted);
        var firstToken = h.Navigator.Calls[0].Token;

        await h.Vm.OpenCommand.ExecuteAsync(h.Vm.Items[1]);
        await firstOpen;

        Assert.True(firstToken.IsCancellationRequested);
        Assert.False(h.Navigator.Calls[1].Token.IsCancellationRequested);
        Assert.Equal(2, h.Navigator.Calls.Count);
        Assert.Equal(["repo"], h.Routes);
        Assert.Empty(h.Notifications.Published);
    }

    [Fact]
    public async Task A_failed_open_publishes_a_warning_and_keeps_the_item()
    {
        var item = Item("gone");
        using var h = new Harness(null, item);
        h.Navigator.Next((_, _) => Task.FromResult<(ControlResponse, string?)>(
            (ControlResponse.Fail(ControlError.RevNotFound, "That commit is gone."), "app")));

        await h.Vm.OpenCommand.ExecuteAsync(h.Vm.Items[0]);

        var notice = Assert.Single(h.Notifications.Published);
        Assert.Equal(NotificationSeverity.Warning, notice.Severity);
        Assert.Contains("app", notice.Message);
        Assert.Contains("That commit is gone.", notice.Message);
        Assert.Single(h.Vm.Items);
        Assert.Single(h.Queue.Items);
        Assert.Empty(h.Routes);
    }

    [Fact]
    public async Task A_navigator_exception_publishes_a_warning()
    {
        using var h = new Harness(null, Item("boom"));
        h.Navigator.Next((_, _) => Task.FromException<(ControlResponse, string?)>(new InvalidOperationException("Broken.")));

        await h.Vm.OpenCommand.ExecuteAsync(h.Vm.Items[0]);

        var notice = Assert.Single(h.Notifications.Published);
        Assert.Equal(NotificationSeverity.Warning, notice.Severity);
        Assert.Contains("Broken.", notice.Message);
    }

    [Fact]
    public void Dispose_unsubscribes_from_the_queue()
    {
        var h = new Harness(null, Item("one"));
        Assert.Equal(1, h.Queue.ChangedSubscriberCount);

        h.Dispose();

        Assert.Equal(0, h.Queue.ChangedSubscriberCount);
    }

    [Fact]
    public void Time_text_is_the_short_time_today_and_the_date_and_time_before()
    {
        var plusTwo = TimeSpan.FromHours(2);
        var zone = TimeZoneInfo.CreateCustomTimeZone("plus-two", plusTwo, "plus-two", "plus-two");
        var time = new FixedTime(new DateTimeOffset(2026, 10, 22, 20, 0, 0, TimeSpan.Zero), zone);
        var culture = System.Globalization.CultureInfo.CurrentCulture;

        var today = new DateTimeOffset(2026, 10, 22, 13, 4, 0, TimeSpan.Zero);
        Assert.Equal(today.ToOffset(plusTwo).ToString("t", culture),
            new CmdrQueueItemViewModel(Item("a", today), time).TimeText);

        // 23:30 UTC is already the next day two hours east, so it reads as today there.
        var lateUtc = new DateTimeOffset(2026, 10, 21, 23, 30, 0, TimeSpan.Zero);
        Assert.Equal(lateUtc.ToOffset(plusTwo).ToString("t", culture),
            new CmdrQueueItemViewModel(Item("b", lateUtc), time).TimeText);

        var older = new DateTimeOffset(2026, 10, 20, 9, 15, 0, TimeSpan.Zero);
        var olderText = new CmdrQueueItemViewModel(Item("c", older), time).TimeText;
        Assert.Equal(older.ToOffset(plusTwo).ToString("g", culture), olderText);
        Assert.NotEqual(older.ToOffset(plusTwo).ToString("t", culture), olderText);
    }

    private sealed class ManualClock(DateTimeOffset utcNow, TimeZoneInfo zone) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = utcNow;
        public List<(TimerCallback Callback, TimeSpan Due)> Timers { get; } = [];
        public override DateTimeOffset GetUtcNow() => Now;
        public override TimeZoneInfo LocalTimeZone => zone;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timers.Add((callback, dueTime));
            return new IdleTimer();
        }

        private sealed class IdleTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void Rows_take_the_date_once_local_midnight_passes()
    {
        var plusTwo = TimeSpan.FromHours(2);
        var zone = TimeZoneInfo.CreateCustomTimeZone("plus-two", plusTwo, "plus-two", "plus-two");
        // 22:00 local; the item was queued at 23:00 local the same day.
        var clock = new ManualClock(new DateTimeOffset(2026, 10, 22, 20, 0, 0, TimeSpan.Zero), zone);
        var created = new DateTimeOffset(2026, 10, 22, 21, 0, 0, TimeSpan.Zero);
        var queue = new FakeCmdrQueueService();
        queue.Enqueue(Item("late", created));
        using var vm = new CmdrQueueViewModel(queue, new FakeSettingsService(), new FakeControlNavigator(),
            new NavigationBus(), new FakeNotificationService(), new ImmediateUiDispatcher(), clock);
        var row = Assert.Single(vm.Items);
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        Assert.Equal(created.ToOffset(plusTwo).ToString("t", culture), row.TimeText);

        var (callback, due) = Assert.Single(clock.Timers);
        Assert.Equal(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(1), due);

        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        clock.Now += due;
        callback(null);

        Assert.Contains(nameof(CmdrQueueItemViewModel.TimeText), changed);
        Assert.Equal(created.ToOffset(plusTwo).ToString("g", culture), row.TimeText);
        Assert.Equal(2, clock.Timers.Count);
    }

    [Fact]
    public void The_tooltip_has_the_full_text_and_the_target()
    {
        var show = new CmdrQueueItemViewModel(Item("Check the parser change"));
        Assert.Equal("Check the parser change\nshow 90236c7", show.Tooltip);

        var changes = new CmdrQueueItemViewModel(new CmdrQueueItem
        {
            RepoRoot = @"C:\repos\app",
            RepoName = "app",
            Target = ControlVerb.Changes,
            Paths = ["a.cs", "b.cs", "c.cs"],
        });
        Assert.Equal("3 files to review\nchanges 3 files", changes.Tooltip);
    }
}
