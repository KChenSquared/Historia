namespace Historia.Tests;

/// <summary>
/// The read-only guarantee of external control, checked at the source level: everything an
/// external tool can ask Historia to do may navigate, may queue a navigation for the user to open
/// later and may offer a commit message, and must never reach a repository write, a settings write,
/// a dialog or the user's own pending proposal. A scan of the host, the shared navigator and every
/// control file fails the build the moment a mutating call appears — a regression here produces no
/// error at runtime, which is why it is a test. The queue is the one list the host adds to, and only
/// through its add-only call.
/// </summary>
[Trait("tier", "unit")]
public class ExternalControlReadOnlyTests
{
    private const string HostFile = "Historia.App/Services/ExternalControlHost.cs";
    private const string ControlFolder = "Historia.Core/Services/ExternalControl";
    private const string NavigatorFile = ControlFolder + "/ControlNavigator.cs";

    /// <summary>Every git command line the resolver may run, all reads.</summary>
    private static readonly string[] ResolverArguments =
    [
        "Arguments = [\"rev-parse\", \"--verify\", \"-q\", \"--end-of-options\", rev + \"^{commit}\"]",
        "Arguments = [\"rev-parse\", \"--show-toplevel\"]",
        "Arguments = [\"log\", \"-1\", \"--format=%s\", \"--end-of-options\", commit.ToString()]",
        "Arguments = [\"diff\", \"--cached\", \"--name-only\", \"-z\"]",
    ];

    /// <summary>Tokens no control code may contain.</summary>
    private static readonly (Regex Pattern, string What)[] Forbidden =
    [
        (new(@"\bIGitGateway\b", RegexOptions.Compiled), "IGitGateway (every write goes through it)"),
        (new(@"\.Gateway\b", RegexOptions.Compiled), "a repository's Gateway"),
        (new(@"WithWriteLockAsync", RegexOptions.Compiled), "the repository write lock"),
        (new(@"\.UpdateAsync\s*\(", RegexOptions.Compiled), "a settings (or layout) write"),
        (new(@"\bIDialogService\b", RegexOptions.Compiled), "a dialog"),
        (new(@"\.CloseAsync\s*\(", RegexOptions.Compiled), "closing a tab"),
        (new(@"\bIPatchService\b", RegexOptions.Compiled), "staging patches"),
        (new(@"ClearPendingCommitMessage", RegexOptions.Compiled), "clearing a proposal (only the user's commit box does that)"),
        (new(@"\bIRecentRepositoryService\b", RegexOptions.Compiled), "the Recent list (opening goes through the workspace)"),
        (new(@"\b(File|Directory)\.(Write|Append|Delete|Move|Copy|Create|Replace|SetAttributes)", RegexOptions.Compiled), "a file-system write"),
        (new(@"\bProcess\.Start\b|\bnew\s+ProcessStartInfo\b", RegexOptions.Compiled), "starting a process"),
    ];

    /// <summary>The members the host and the navigator may use on each collaborator.</summary>
    private static readonly Dictionary<string, HashSet<string>> AllowedMembers = new()
    {
        ["_workspace"] = ["OpenAsync", "Focused", "EnsureInitialized"],
        ["state"] =
        [
            "SetSelectedCommit", "RequestRevealCommit", "SetOpenDiff", "SelectedCommitId", "OpenDiff",
            "RefreshStatusAsync", "Status", "ProposeCommitMessage", "PendingCommitMessage",
        ],
        ["ctx"] = ["FirstGraphPaint", "RepositoryPath", "State", "DisplayName"],
        ["_settings"] = ["Current"],
        ["_navigator"] = ["NavigateAsync"],
        ["_queue"] = ["Enqueue"],
    };

    [Fact]
    public void Control_code_reaches_no_write_path()
    {
        var root = RepoRoot();
        var offenders = new List<string>();

        foreach (var (rel, text) in ControlSources(root))
            offenders.AddRange(FindForbidden(rel, text));

        Assert.True(offenders.Count == 0,
            "External control must only navigate and propose — it reached a write path:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void The_host_and_the_navigator_use_only_the_allowed_members_of_their_collaborators()
    {
        var root = RepoRoot();
        var offenders = new List<string>();
        foreach (var rel in new[] { HostFile, NavigatorFile })
        {
            var text = StripComments(File.ReadAllText(Path.Combine(root, rel)));
            offenders.AddRange(FindDisallowedMembers(text).Select(m => $"{rel}: {m}"));
        }

        Assert.True(offenders.Count == 0,
            "External control used a member outside the navigate, queue and propose allowlist:\n  "
            + string.Join("\n  ", offenders)
            + "\nA new verb that is neither navigation nor a proposal the user must act on does not ship as an agent verb.");
    }

    [Fact]
    public void Only_the_revision_resolver_starts_git_and_it_only_reads()
    {
        var root = RepoRoot();
        var runners = ControlSources(root)
            .Where(s => Regex.IsMatch(s.Text, @"\.RunAsync\s*\("))
            .Select(s => s.Rel)
            .ToList();

        Assert.Equal([ControlFolder + "/RevisionResolver.cs"], runners);

        var resolver = StripComments(File.ReadAllText(Path.Combine(root, ControlFolder, "RevisionResolver.cs")));
        foreach (var arguments in ResolverArguments)
            Assert.Contains(arguments, resolver);
        Assert.Equal(ResolverArguments.Length, Regex.Matches(resolver, @"\bArguments\s*=").Count);
    }

    [Fact]
    public void The_scan_catches_a_mutating_call()
    {
        Assert.NotEmpty(FindForbidden("x.cs", "await ctx.Gateway.StageAsync(paths);"));
        Assert.NotEmpty(FindForbidden("x.cs", "await _settings.UpdateAsync(s => s);"));
        Assert.NotEmpty(FindForbidden("x.cs", "state.ClearPendingCommitMessage();"));
        Assert.NotEmpty(FindForbidden("x.cs", "File.WriteAllText(p, t);"));
        Assert.NotEmpty(FindDisallowedMembers("state.SetSelectedPaths(x); _workspace.CloseAsync(c);"));
        Assert.NotEmpty(FindDisallowedMembers("ctx.State.SetSelectedPaths(x);"));
        Assert.NotEmpty(FindDisallowedMembers("_workspace.Focused.State.ClearPendingCommitMessage();"));
        Assert.NotEmpty(FindDisallowedMembers("_workspace.Focused.Gateway.Stage(x);"));
        Assert.NotEmpty(FindDisallowedMembers("_queue.Remove(id);"));
        Assert.NotEmpty(FindDisallowedMembers("_queue.MarkSeen(id);"));
        Assert.Empty(FindDisallowedMembers("_queue.Enqueue(item); await _navigator.NavigateAsync(target, ct);"));
        Assert.Empty(FindForbidden("x.cs", "state.SetOpenDiff(null);"));
        Assert.Empty(FindDisallowedMembers("state.SetOpenDiff(null); ctx.State.ProposeCommitMessage(m);"));
        Assert.Empty(FindDisallowedMembers("ctx.State.ProposeCommitMessage(m); ctx.State.OpenDiff"));
        Assert.Empty(FindDisallowedMembers("_workspace.Focused.State.SelectedCommitId"));
    }

    private static List<string> FindForbidden(string rel, string text)
    {
        var stripped = StripComments(text);
        var found = new List<string>();
        foreach (var (pattern, what) in Forbidden)
        {
            if (pattern.IsMatch(stripped))
                found.Add($"{rel}: {what}");
        }
        return found;
    }

    /// <summary>Receivers named directly, and the two properties through which a chained call
    /// reaches the same collaborators: <c>.State</c> is a repository's state, <c>.Focused</c> its
    /// context.</summary>
    private static readonly (Regex Pattern, string? Receiver)[] MemberAccess =
    [
        (new(@"\b(_workspace|state|ctx|_settings|_navigator|_queue)\.(\w+)", RegexOptions.Compiled), null),
        (new(@"\.State\.(\w+)", RegexOptions.Compiled), "state"),
        (new(@"\.Focused\.(\w+)", RegexOptions.Compiled), "ctx"),
    ];

    private static List<string> FindDisallowedMembers(string strippedText)
    {
        var found = new List<string>();
        foreach (var (pattern, fixedReceiver) in MemberAccess)
        {
            foreach (Match m in pattern.Matches(strippedText))
            {
                var receiver = fixedReceiver ?? m.Groups[1].Value;
                var member = m.Groups[^1].Value;
                if (!AllowedMembers[receiver].Contains(member))
                    found.Add($"{receiver}.{member}");
            }
        }
        return found.Distinct().ToList();
    }

    private static string StripComments(string text) => Regex.Replace(text, @"//.*$", "", RegexOptions.Multiline);

    private static List<(string Rel, string Text)> ControlSources(string root)
    {
        var files = Directory.GetFiles(Path.Combine(root, ControlFolder), "*.cs", SearchOption.AllDirectories)
            .Append(Path.Combine(root, HostFile))
            .ToList();
        Assert.True(files.Count >= 7, "Expected the host, the navigator and the control files to exist.");

        return files
            .Select(f => (Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllText(f)))
            .ToList();
    }

    /// <summary>Walks up from the test binary to the repo root; a worktree's top-level <c>.git</c>
    /// is a file, so accept either.</summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var gitEntry = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitEntry) || File.Exists(gitEntry))
                break;
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
