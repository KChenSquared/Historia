namespace Historia.Tests;

[Trait("tier", "unit")]
public class ViewModelBaseTests
{
    private sealed class Probe : ViewModelBase
    {
        private string _name = "";

        public int DisposedCallbacks { get; private set; }
        public List<string> Log { get; } = [];

        public string Name
        {
            get => _name;
            set => SetProperty(ref _name, value);
        }

        public void Hook(Action subscribe, Action unsubscribe) => Subscribe(subscribe, unsubscribe);

        public T Own<T>(T disposable) where T : IDisposable => Track(disposable);

        protected override void OnDisposed()
        {
            DisposedCallbacks++;
            Log.Add("OnDisposed");
        }
    }

    private sealed class Recorder(List<string> log, string name) : IDisposable
    {
        public int Disposals { get; private set; }

        public void Dispose()
        {
            Disposals++;
            log.Add(name);
        }
    }

    [Fact]
    public void Subscribe_runs_the_subscription_now_and_the_unsubscription_on_dispose()
    {
        var vm = new Probe();
        var subscribed = 0;
        var unsubscribed = 0;

        vm.Hook(() => subscribed++, () => unsubscribed++);

        Assert.Equal(1, subscribed);
        Assert.Equal(0, unsubscribed);
        Assert.False(vm.IsDisposed);

        vm.Dispose();

        Assert.Equal(1, unsubscribed);
        Assert.True(vm.IsDisposed);
    }

    [Fact]
    public void Dispose_is_idempotent()
    {
        var vm = new Probe();
        var unsubscribed = 0;
        var tracked = vm.Own(new Recorder(vm.Log, "tracked"));
        vm.Hook(() => { }, () => unsubscribed++);

        vm.Dispose();
        vm.Dispose();

        Assert.Equal(1, unsubscribed);
        Assert.Equal(1, tracked.Disposals);
        Assert.Equal(1, vm.DisposedCallbacks);
    }

    [Fact]
    public void Dispose_releases_subscriptions_and_tracked_items_in_reverse_order()
    {
        var vm = new Probe();
        var log = new List<string>();

        vm.Own(new Recorder(log, "first tracked"));
        vm.Hook(() => log.Add("subscribe a"), () => log.Add("unsubscribe a"));
        vm.Own(new Recorder(log, "second tracked"));
        vm.Hook(() => log.Add("subscribe b"), () => log.Add("unsubscribe b"));
        log.Clear();

        vm.Dispose();

        Assert.Equal(["unsubscribe b", "second tracked", "unsubscribe a", "first tracked"], log);
    }

    [Fact]
    public void OnDisposed_runs_once_after_every_release()
    {
        var vm = new Probe();
        vm.Hook(() => { }, () => vm.Log.Add("unsubscribe"));
        vm.Own(new Recorder(vm.Log, "tracked"));

        vm.Dispose();

        Assert.Equal(["tracked", "unsubscribe", "OnDisposed"], vm.Log);
    }

    [Fact]
    public void A_teardown_step_that_throws_does_not_stop_the_others()
    {
        var vm = new Probe();
        var ran = new List<string>();
        vm.Hook(() => { }, () => ran.Add("first"));
        vm.Hook(() => { }, () => throw new InvalidOperationException("boom"));
        vm.Hook(() => { }, () => ran.Add("last"));

        vm.Dispose();

        Assert.Equal(["last", "first"], ran);
        Assert.Equal(1, vm.DisposedCallbacks);
    }

    [Fact]
    public void After_dispose_nothing_new_is_subscribed_and_new_items_are_disposed_at_once()
    {
        var vm = new Probe();
        vm.Dispose();

        var subscribed = 0;
        vm.Hook(() => subscribed++, () => { });
        var late = vm.Own(new Recorder(vm.Log, "late"));

        Assert.Equal(0, subscribed);
        Assert.Equal(1, late.Disposals);
    }

    [Fact]
    public void A_dispose_inside_subscribe_still_unsubscribes()
    {
        var vm = new Probe();
        var unsubscribed = 0;

        vm.Hook(() => vm.Dispose(), () => unsubscribed++);

        Assert.True(vm.IsDisposed);
        Assert.Equal(1, unsubscribed);
        Assert.Equal(1, vm.DisposedCallbacks);
    }

    [Fact]
    public async Task Track_racing_dispose_on_another_thread_disposes_every_item_exactly_once()
    {
        // An item tracked just before Dispose takes its snapshot is released by Dispose; one
        // tracked just after is released at once. Neither path may lose or repeat an item.
        for (var round = 0; round < 500; round++)
        {
            var vm = new Probe();
            var items = Enumerable.Range(0, 64).Select(_ => new CountingDisposable()).ToArray();
            using var start = new Barrier(2);

            var tracker = Task.Run(() =>
            {
                start.SignalAndWait();
                foreach (var item in items)
                    vm.Own(item);
            });
            var disposer = Task.Run(() =>
            {
                start.SignalAndWait();
                vm.Dispose();
            });
            await Task.WhenAll(tracker, disposer);

            Assert.All(items, item => Assert.Equal(1, item.Disposals));
        }
    }

    private sealed class CountingDisposable : IDisposable
    {
        private int _disposals;

        public int Disposals => Volatile.Read(ref _disposals);

        public void Dispose() => Interlocked.Increment(ref _disposals);
    }

    [Fact]
    public void Setting_a_property_raises_PropertyChanged_once_and_only_on_change()
    {
        var vm = new Probe();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.Name = "main";
        vm.Name = "main";

        Assert.Equal(["Name"], raised);
    }

    // The optional-dependency convention rests on ActivatorUtilities falling back to a
    // parameter's default when the provider cannot resolve it. These tests pin that behaviour.

    private sealed class ConsumerViewModel(IDialogService dialogs, IClipboardService? clipboard = null) : ViewModelBase
    {
        public IDialogService Dialogs { get; } = dialogs;
        public IClipboardService Clipboard { get; } = clipboard ?? NullClipboardService.Instance;
    }

    [Fact]
    public void ActivatorUtilities_uses_the_default_for_an_optional_service_the_provider_lacks()
    {
        var dialogs = new FakeDialogService();
        var services = new ServiceCollection().AddSingleton<IDialogService>(dialogs).BuildServiceProvider();

        var vm = ActivatorUtilities.CreateInstance<ConsumerViewModel>(services);

        Assert.Same(dialogs, vm.Dialogs);
        Assert.Same(NullClipboardService.Instance, vm.Clipboard);
    }

    [Fact]
    public void ActivatorUtilities_succeeds_for_an_optional_service_against_an_empty_collection_when_the_required_one_is_passed()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var dialogs = new FakeDialogService();

        var vm = ActivatorUtilities.CreateInstance<ConsumerViewModel>(services, dialogs);

        Assert.Same(dialogs, vm.Dialogs);
        Assert.Same(NullClipboardService.Instance, vm.Clipboard);
    }

    [Fact]
    public void ActivatorUtilities_prefers_a_registered_service_over_the_default()
    {
        var clipboard = new FakeClipboardService();
        var services = new ServiceCollection()
            .AddSingleton<IDialogService>(new FakeDialogService())
            .AddSingleton<IClipboardService>(clipboard)
            .BuildServiceProvider();

        var vm = ActivatorUtilities.CreateInstance<ConsumerViewModel>(services);

        Assert.Same(clipboard, vm.Clipboard);
    }

    [Fact]
    public void ActivatorUtilities_prefers_an_explicit_argument_over_the_provider_and_the_default()
    {
        var registered = new FakeClipboardService();
        var explicitClipboard = new FakeClipboardService();
        var services = new ServiceCollection()
            .AddSingleton<IDialogService>(new FakeDialogService())
            .AddSingleton<IClipboardService>(registered)
            .BuildServiceProvider();

        var vm = ActivatorUtilities.CreateInstance<ConsumerViewModel>(services, explicitClipboard);

        Assert.Same(explicitClipboard, vm.Clipboard);
    }

    [Fact]
    public void ActivatorUtilities_still_requires_a_required_service()
    {
        var services = new ServiceCollection().BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => ActivatorUtilities.CreateInstance<ConsumerViewModel>(services));
    }

    [Fact]
    public async Task The_core_defaults_for_the_ui_seams_do_nothing()
    {
        await NullClipboardService.Instance.SetTextAsync("text");
        Assert.Null(await NullRemoteDialogs.Instance.ShowAddAsync([]));
        Assert.Null(await NullRemoteDialogs.Instance.ShowEditAsync(
            new RemoteInfo { Name = "origin", Url = "https://example.test/repo.git" },
            [], new Dictionary<string, string>()));
    }

    [Fact]
    public void CommitDraftStore_keeps_a_draft_per_repository_and_forgets_an_empty_one()
    {
        var store = new CommitDraftStore();

        Assert.Equal("", store.Get("tab-1"));

        store.Set("tab-1", "fix the thing");
        store.Set("tab-2", "other");

        Assert.Equal("fix the thing", store.Get("tab-1"));
        Assert.Equal("other", store.Get("tab-2"));

        store.Set("tab-1", "");

        Assert.Equal("", store.Get("tab-1"));
        Assert.Equal("other", store.Get("tab-2"));

        store.Set("tab-2", null);

        Assert.Equal("", store.Get("tab-2"));
    }
}
