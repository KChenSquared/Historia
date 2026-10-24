namespace Historia.ViewModels;

/// <summary>
/// One row of the sidebar's agent queue: the repository folder, when the item was queued, and the
/// agent's note or the text that stands in for it. <see cref="Seen"/> dims the row once opened.
/// </summary>
public sealed partial class CmdrQueueItemViewModel : ObservableObject
{
    private readonly TimeProvider _time;

    public CmdrQueueItemViewModel(CmdrQueueItem item, TimeProvider? time = null, CmdrQueueViewModel? host = null)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        _time = time ?? TimeProvider.System;
        Host = host;
        Seen = item.Seen;
    }

    /// <summary>The list this row belongs to; the row's buttons bind its commands.</summary>
    public CmdrQueueViewModel? Host { get; }

    /// <summary>The queued item this row shows; replaced when the stored item changes.</summary>
    public CmdrQueueItem Item { get; private set; }

    /// <summary>The item's identity in the queue.</summary>
    public string Id => Item.Id;

    /// <summary>The repository's folder name.</summary>
    public string RepoName => Item.RepoName;

    /// <summary>The note, or the file name, commit subject, proposed subject or file count in its place.</summary>
    public string DisplayText => Item.DisplayText;

    /// <summary>When the item was queued, in local time: the short time for today's items, else the
    /// short date and time, in the current culture.</summary>
    public string TimeText
    {
        get
        {
            var zone = _time.LocalTimeZone;
            var created = TimeZoneInfo.ConvertTime(Item.CreatedAt, zone);
            var today = TimeZoneInfo.ConvertTime(_time.GetUtcNow(), zone);
            var format = created.Date == today.Date ? "t" : "g";
            return created.ToString(format, CultureInfo.CurrentCulture);
        }
    }

    /// <summary>The full text and what opening the item shows, such as <c>show 90236c7</c>.</summary>
    public string Tooltip => $"{DisplayText}\n{TargetText(Item)}";

    /// <summary>True once the user has opened the item.</summary>
    [ObservableProperty]
    public partial bool Seen { get; private set; }

    /// <summary>Takes the stored state of the same item, such as its seen flag.</summary>
    public void Update(CmdrQueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (ReferenceEquals(item, Item))
            return;
        Item = item;
        Seen = item.Seen;
        OnPropertyChanged(nameof(RepoName));
        OnPropertyChanged(nameof(DisplayText));
        OnPropertyChanged(nameof(Tooltip));
        OnPropertyChanged(nameof(TimeText));
    }

    /// <summary>Re-reads <see cref="TimeText"/>, which changes form when the local date changes.</summary>
    public void RefreshTime() => OnPropertyChanged(nameof(TimeText));

    /// <summary>A short command-like description of what opening <paramref name="item"/> shows.</summary>
    public static string TargetText(CmdrQueueItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var files = item.Paths.Count switch
        {
            0 => "",
            1 => " " + item.Paths[0],
            var n => $" {n} files",
        };
        return item.Target switch
        {
            ControlVerb.Show => $"show {Short(item.Rev)}",
            ControlVerb.Diff => $"diff {Short(item.Rev)}{files}",
            ControlVerb.Compare => $"compare {Short(item.Rev)} {Short(item.OtherRev)}{files}",
            ControlVerb.Changes => $"changes{(item.Staged ? " --staged" : "")}{files}",
            ControlVerb.ProposeCommit => item.StagedPaths.Count == 1
                ? "propose-commit 1 staged file"
                : $"propose-commit {item.StagedPaths.Count} staged files",
            _ => "open",
        };
    }

    private static string Short(string? sha) =>
        string.IsNullOrEmpty(sha) ? "?" : sha.Length <= 7 ? sha : sha[..7];
}
