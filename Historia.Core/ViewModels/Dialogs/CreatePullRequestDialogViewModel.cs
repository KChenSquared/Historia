namespace Historia.ViewModels;

/// <summary>
/// The Create pull request dialog: a source and a target branch chosen from editable lists, a Push
/// button for an unpushed local source, and the GitHub compare link to open. Sources are every
/// local branch (<c>local.&lt;name&gt;</c>) and remote-tracking branch (<c>&lt;remote&gt;.&lt;name&gt;</c>);
/// targets are remote-tracking branches only. Typing in either box ranks its list like the sidebar
/// branch search. The link is only ever opened by the Open command.
/// </summary>
public sealed partial class CreatePullRequestDialogViewModel : ViewModelBase
{
    private const string LocalPrefix = "local";

    private sealed record Entry(string Display, BranchInfo Branch, string? Remote, string ShortName)
    {
        public bool IsLocal => Remote is null;
    }

    private readonly CreatePullRequestRequest _request;
    private readonly IExternalLauncher _launcher;
    private readonly CancellationTokenSource _lifetime = new();
    private IReadOnlyList<BranchInfo> _branches;
    private IReadOnlyList<Entry> _sourceEntries = [];
    private IReadOnlyList<Entry> _targetEntries = [];

    public CreatePullRequestDialogViewModel(CreatePullRequestRequest request, IExternalLauncher launcher)
    {
        _request = request;
        _launcher = launcher;
        _branches = request.Branches;
        SourceItems = TargetItems = FilteredSourceItems = FilteredTargetItems = [];
        Url = "";
        SourceText = "";
        TargetText = "";
        BuildEntries();

        var source = Find(_sourceEntries, request.InitialSource)
            ?? _sourceEntries.FirstOrDefault(e => e.IsLocal && e.Branch.IsCurrent)
            ?? _sourceEntries.FirstOrDefault();
        SourceText = source?.Display ?? "";
        TargetText = DefaultTarget(source)?.Display ?? "";
        UpdateUrl();
    }

    /// <summary>Raised when the dialog should close.</summary>
    public event EventHandler? CloseRequested;

    /// <summary>Every source choice: local branches first, then remote-tracking branches.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> SourceItems { get; private set; }

    /// <summary>Every target choice: remote-tracking branches only.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> TargetItems { get; private set; }

    /// <summary>The source choices matching <see cref="SourceText"/>, best match first; all of them when it is empty or exact.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> FilteredSourceItems { get; private set; }

    /// <summary>The target choices matching <see cref="TargetText"/>, best match first; all of them when it is empty or exact.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> FilteredTargetItems { get; private set; }

    /// <summary>The text in the source box.</summary>
    [ObservableProperty]
    public partial string SourceText { get; set; }

    /// <summary>The text in the target box.</summary>
    [ObservableProperty]
    public partial string TargetText { get; set; }

    /// <summary>The source choice the text names, or null when it names none. Setting it fills the text.</summary>
    public string? SelectedSource
    {
        get => Find(_sourceEntries, SourceText)?.Display;
        set
        {
            if (value is not null)
                SourceText = value;
        }
    }

    /// <summary>The target choice the text names, or null when it names none. Setting it fills the text.</summary>
    public string? SelectedTarget
    {
        get => Find(_targetEntries, TargetText)?.Display;
        set
        {
            if (value is not null)
                TargetText = value;
        }
    }

    /// <summary>The compare link; empty while none can be built.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUrl))]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    public partial string Url { get; private set; }

    /// <summary>Why <see cref="Url"/> is empty, or null while it is set.</summary>
    [ObservableProperty]
    public partial string? UrlProblem { get; private set; }

    /// <summary>True while a compare link is available.</summary>
    public bool HasUrl => Url.Length > 0;

    /// <summary>True while the Push button is running.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PushCommand))]
    public partial bool IsPushing { get; private set; }

    /// <summary>The remote a local branch is pushed to: its upstream's remote, else <c>origin</c>, else the
    /// first configured remote. Null when there is no remote.</summary>
    public static string? ResolvePushRemote(BranchInfo local, IReadOnlyList<RemoteInfo> remotes) =>
        UpstreamOf(local, remotes)?.Remote
        ?? remotes.FirstOrDefault(r => r.Name == "origin")?.Name
        ?? remotes.FirstOrDefault()?.Name;

    /// <summary>The remote and branch name <paramref name="local"/> tracks, or null without an upstream.
    /// A remote name may contain a slash, so the longest configured remote that prefixes the upstream wins.</summary>
    public static (string Remote, string Branch)? UpstreamOf(BranchInfo local, IReadOnlyList<RemoteInfo> remotes)
    {
        if (local.UpstreamName is not { Length: > 0 } upstream)
            return null;

        var remote = remotes
            .Where(r => upstream.StartsWith(r.Name + "/", StringComparison.Ordinal))
            .OrderByDescending(r => r.Name.Length)
            .FirstOrDefault();
        return remote is null ? null : (remote.Name, upstream[(remote.Name.Length + 1)..]);
    }

    [RelayCommand(CanExecute = nameof(CanPush))]
    private async Task PushAsync()
    {
        if (Find(_sourceEntries, SourceText) is not { IsLocal: true } source
            || ResolvePushRemote(source.Branch, _request.Remotes) is not { } remote)
            return;

        IsPushing = true;
        try
        {
            if (await _request.Push(source.ShortName, remote, _lifetime.Token))
            {
                _branches = _request.RefreshBranches();
                BuildEntries();
                UpdateSourceFilter();
                UpdateTargetFilter();
                UpdateUrl();
            }
        }
        catch (OperationCanceledException)
        {
            // Closing the dialog cancels a running push.
        }
        finally
        {
            IsPushing = false;
            NotifyPushChanged();
        }
    }

    private bool CanPush() =>
        !IsPushing
        && Find(_sourceEntries, SourceText) is { IsLocal: true } source
        && ResolvePushRemote(source.Branch, _request.Remotes) is not null;

    [RelayCommand(CanExecute = nameof(HasUrl))]
    private void Open() => _launcher.OpenUrl(Url);

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    protected override void OnDisposed()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    partial void OnSourceTextChanged(string value)
    {
        UpdateSourceFilter();
        OnPropertyChanged(nameof(SelectedSource));
        UpdateUrl();
        NotifyPushChanged();
    }

    partial void OnTargetTextChanged(string value)
    {
        UpdateTargetFilter();
        OnPropertyChanged(nameof(SelectedTarget));
        UpdateUrl();
    }

    private void NotifyPushChanged() => PushCommand.NotifyCanExecuteChanged();

    private void BuildEntries()
    {
        var sources = new List<Entry>();
        var targets = new List<Entry>();

        foreach (var b in _branches.Where(b => !b.IsRemote))
            sources.Add(new Entry($"{LocalPrefix}.{b.Name}", b, null, b.Name));

        foreach (var b in _branches.Where(b => b.IsRemote && b.RemoteName is { Length: > 0 }))
        {
            var remote = b.RemoteName!;
            if (!b.Name.StartsWith(remote + "/", StringComparison.Ordinal))
                continue;

            var shortName = b.Name[(remote.Length + 1)..];
            if (shortName == "HEAD")
                continue;

            var entry = new Entry($"{remote}.{shortName}", b, remote, shortName);
            sources.Add(entry);
            targets.Add(entry);
        }

        _sourceEntries = sources;
        _targetEntries = targets;
        SourceItems = sources.Select(e => e.Display).ToList();
        TargetItems = targets.Select(e => e.Display).ToList();
    }

    private Entry? DefaultTarget(Entry? source)
    {
        var remote = source is null ? null : source.Remote ?? ResolvePushRemote(source.Branch, _request.Remotes);
        var onRemote = _targetEntries.Where(t => t.Remote == remote).ToList();

        return PreferredBranch(onRemote) ?? PreferredBranch(_targetEntries);
    }

    private static Entry? PreferredBranch(IReadOnlyList<Entry> entries) =>
        entries.FirstOrDefault(e => e.ShortName == "main")
        ?? entries.FirstOrDefault(e => e.ShortName == "master")
        ?? entries.FirstOrDefault();

    private static Entry? Find(IReadOnlyList<Entry> entries, string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed)
            ? null
            : entries.FirstOrDefault(e => string.Equals(e.Display, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateSourceFilter() =>
        FilteredSourceItems = Filter(_sourceEntries, SourceItems, SourceText);

    private void UpdateTargetFilter() =>
        FilteredTargetItems = Filter(_targetEntries, TargetItems, TargetText);

    private static IReadOnlyList<string> Filter(IReadOnlyList<Entry> entries, IReadOnlyList<string> all, string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || Find(entries, trimmed) is not null)
            return all;

        return RefSearch.Rank(entries, e => e.Display, FuzzyScorer.PrepareQuery(trimmed))
            .Select(r => r.Item.Display)
            .ToList();
    }

    private void UpdateUrl()
    {
        var (url, problem) = BuildUrl();
        Url = url ?? "";
        UrlProblem = problem;
    }

    private (string? Url, string? Problem) BuildUrl()
    {
        if (Find(_sourceEntries, SourceText) is not { } source)
            return (null, "Choose a source branch.");
        if (Find(_targetEntries, TargetText) is not { } target)
            return (null, "Choose a target branch.");

        string headRemote, headBranch;
        if (source.IsLocal)
        {
            var upstream = UpstreamOf(source.Branch, _request.Remotes);
            var remote = upstream?.Remote ?? ResolvePushRemote(source.Branch, _request.Remotes);
            if (remote is null)
                return (null, "This repository has no remotes.");

            headRemote = remote;
            headBranch = upstream?.Branch ?? source.ShortName;
            if (!_branches.Any(b => b.IsRemote && b.RemoteName == headRemote && b.Name == $"{headRemote}/{headBranch}"))
                return (null, "Push the source branch first.");
        }
        else
        {
            headRemote = source.Remote!;
            headBranch = source.ShortName;
        }

        var targetUrl = RemoteUrl(target.Remote!);
        var headUrl = RemoteUrl(headRemote);
        if (targetUrl is null || headUrl is null)
            return (null, "The remote has no URL.");

        var built = PullRequestUrl.Build(targetUrl, target.ShortName, headUrl, headBranch);
        return (built.Url, built.Problem);
    }

    private string? RemoteUrl(string remote) =>
        _request.Remotes.FirstOrDefault(r => r.Name == remote)?.Url;
}
