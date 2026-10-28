namespace Historia.Tests;

/// <summary>
/// "Only new repositories" asks the recents service about every entry's folder, all at once, and
/// skips the ones that are missing or do not answer; it never touches the disk itself.
/// </summary>
[Trait("tier", "unit")]
public sealed class DefaultProfileFlowTests : IAsyncDisposable
{
    private sealed class ScriptedRecents : IRecentRepositoryService
    {
        private readonly object _sync = new();
        private readonly List<string> _asked = [];

        public List<RecentRepository> Entries { get; } = [];
        public Dictionary<string, TaskCompletionSource<bool?>> Answers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<RecentRepository> Items => [.. Entries];

        public List<string> Asked
        {
            get { lock (_sync) return [.. _asked]; }
        }

        public Task<bool?> IsStaleAsync(string path)
        {
            lock (_sync)
                _asked.Add(path);
            return Answers[path].Task;
        }

        public event EventHandler? Changed { add { } remove { } }

        public Task AddAsync(string path, string displayName, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class UnusedIdentity : IRepoIdentityService
    {
        public Task<RepoIdentityKey> ResolveAsync(RepositoryContext context, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private readonly MiniHost _host = new();

    public async ValueTask DisposeAsync() => await _host.DisposeAsync();

    [Fact(Timeout = 30000)]
    public async Task Only_new_repositories_asks_the_service_about_every_entry_at_once_and_skips_the_unanswered()
    {
        var recents = new ScriptedRecents();
        foreach (var name in new[] { "gone", "hung", "failed" })
        {
            var path = Path.Combine(TestScratch.Root, "historia-dpf-" + name);
            recents.Entries.Add(new RecentRepository { Path = path, DisplayName = name, LastOpenedAt = DateTimeOffset.Now });
            recents.Answers[path] = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        var profiles = new ProfileService(_host.Settings, new FakeVault(), _host.Cli);
        var dialogs = new FakeDialogService().EnqueueChoice(1);
        var notifications = new FakeNotificationService();
        var sc = new ServiceCollection();
        sc.AddSingleton<IProfileService>(profiles);
        sc.AddSingleton<IDialogService>(dialogs);
        sc.AddSingleton<INotificationService>(notifications);
        sc.AddSingleton<IRecentRepositoryService>(recents);
        sc.AddSingleton<IRepoIdentityService>(new UnusedIdentity());
        await using var sp = sc.BuildServiceProvider();

        var work = await profiles.CreateProfileAsync(new GitProfileCreateRequest
        {
            DisplayName = "Work",
            GitUserName = "Work User",
            GitUserEmail = "work@example.com",
        });

        var flow = DefaultProfileFlow.SetAsync(sp, work);

        // All three are asked before any answers, so a hung one cannot hold the others up.
        var sw = Stopwatch.StartNew();
        while (recents.Asked.Count < 3)
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), "the entries were not all asked at once");
            await Task.Delay(5);
        }

        recents.Answers[recents.Entries[0].Path].SetResult(true); // missing
        recents.Answers[recents.Entries[1].Path].SetResult(null); // no answer in time
        recents.Answers[recents.Entries[2].Path].SetResult(null); // the check failed
        await flow.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(work.Id, profiles.GetDefaultProfile()?.Id);
        Assert.Single(notifications.Published);
    }
}
