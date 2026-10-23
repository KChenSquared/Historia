namespace Historia.Models;

/// <summary>
/// One navigation an agent queued through <c>historia-cmdr</c> for the user to open later: the
/// repository, what to show there, and an optional note from the agent. The item is a pointer,
/// not the user's work — removing it changes nothing in any repository.
/// </summary>
public sealed record CmdrQueueItem
{
    /// <summary>Stable identity for seen and remove calls.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>When the item was queued, in UTC.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>The repository's work-tree root, as a full native path.</summary>
    public string RepoRoot { get; init; } = "";

    /// <summary>The leaf folder name of <see cref="RepoRoot"/>.</summary>
    public string RepoName { get; init; } = "";

    /// <summary>The navigation verb to run when the item is opened. Stored in the same kebab form
    /// the control protocol uses (<c>propose-commit</c>); the property-level converter outranks
    /// the store's general enum converter.</summary>
    [JsonConverter(typeof(ControlKebabEnumConverter<ControlVerb>))]
    public ControlVerb Target { get; init; }

    /// <summary>The commit to show, or the first side of a compare, as a full SHA.</summary>
    public string? Rev { get; init; }

    /// <summary>The second side of a compare, as a full SHA.</summary>
    public string? OtherRev { get; init; }

    /// <summary>Repo-relative paths with forward slashes, in the order the agent gave them.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>1-based line to scroll to in the first file.</summary>
    public int? Line { get; init; }

    /// <summary>Shows the staged side of a working-copy change.</summary>
    public bool Staged { get; init; }

    /// <summary>The proposed commit message for a queued commit proposal.</summary>
    public string? CommitMessage { get; init; }

    /// <summary>The subject line of <see cref="Rev"/> when the target shows a commit.</summary>
    public string? CommitSubject { get; init; }

    /// <summary>The paths that were staged when a commit proposal was queued.</summary>
    public IReadOnlyList<string> StagedPaths { get; init; } = [];

    /// <summary>The agent's short note, already normalized to one line.</summary>
    public string? Note { get; init; }

    /// <summary>True once the user has opened the item.</summary>
    public bool Seen { get; init; }

    /// <summary>
    /// The text the list shows: the note when there is one; otherwise the file name for a
    /// single-file target, the commit subject for a commit, the proposed subject line for a
    /// commit proposal, a file count for several files, and "Open repository" for an open.
    /// </summary>
    [JsonIgnore]
    public string DisplayText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Note))
                return Note;

            return Target switch
            {
                ControlVerb.Diff or ControlVerb.Compare when Paths.Count > 0 => LeafName(Paths[0]),
                ControlVerb.Diff or ControlVerb.Compare => ShortSha(Rev) ?? "Changes to review",
                ControlVerb.Changes when Paths.Count == 1 => LeafName(Paths[0]),
                ControlVerb.Changes when Paths.Count > 1 => $"{Paths.Count} files to review",
                ControlVerb.Changes => "Changes to review",
                ControlVerb.Show => !string.IsNullOrWhiteSpace(CommitSubject) ? CommitSubject : ShortSha(Rev) ?? "Commit",
                ControlVerb.ProposeCommit => FirstLine(CommitMessage) ?? "Proposed commit",
                _ => "Open repository",
            };
        }
    }

    private static string LeafName(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }

    private static string? ShortSha(string? sha) =>
        string.IsNullOrEmpty(sha) ? null : sha.Length <= 7 ? sha : sha[..7];

    private static string? FirstLine(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return null;
        var trimmed = message.TrimStart();
        var end = trimmed.IndexOfAny(['\r', '\n']);
        return (end < 0 ? trimmed : trimmed[..end]).TrimEnd();
    }
}
