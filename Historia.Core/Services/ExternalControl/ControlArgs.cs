namespace Historia.Services.ExternalControl;

/// <summary>The outcome of parsing a <c>historia-cmdr</c> command line.</summary>
public sealed record ControlArgsResult
{
    public ControlRequest? Request { get; init; }
    public string? UsageError { get; init; }
    public bool ShowHelp { get; init; }
    public bool ShowVersion { get; init; }

    /// <summary>Do not start Historia when none is running. Client-only; never sent.</summary>
    public bool NoLaunch { get; init; }

    /// <summary>The <c>-F</c> argument of <c>propose-commit</c> (<c>-</c> is standard input). The
    /// client reads and normalizes it; the parser does not touch the file system.</summary>
    public string? MessageFile { get; init; }

    public bool IsUsageError => UsageError is not null;
}

/// <summary>Parses the <c>historia-cmdr</c> command line into a <see cref="ControlRequest"/>. A
/// navigation verb is queued for the user to open later (<see cref="ControlVerb.Queue"/>) unless
/// <c>--now</c> asks for it to be shown at once.</summary>
public static class ControlArgs
{
    /// <summary>The most files one queued <c>changes</c> may name.</summary>
    public const int MaxQueuedFiles = 200;

    public const string UsageText = """
        usage:
          historia-cmdr ping
          historia-cmdr selection
          historia-cmdr open                                [-C <repo>] [<queue options>]
          historia-cmdr show    <rev>                       [-C <repo>] [<queue options>]
          historia-cmdr diff    <rev> <file>       [--line N] [-C <repo>] [<queue options>]
          historia-cmdr compare <base> <head> <file> [--line N] [-C <repo>] [<queue options>]
          historia-cmdr changes <file>... [--staged] [--line N] [-C <repo>] [<queue options>]
          historia-cmdr propose-commit (-m <text>... | -F <file|->) [-C <repo>] [<queue options>]
          historia-cmdr --help | --version

        queue options:
          --note <text>   a short message shown on the queued item (at most 200 characters)
          --now           show it in Historia at once instead of queueing it (no --note);
                          changes then takes exactly one file
          --no-focus      with --now, do not bring Historia's window to the front
          --no-launch     do not start Historia when it is not running

        By default the command is queued: Historia lists it for the user to open later and does
        not switch the view. Prints one JSON object. Exit codes: 0 ok, 1 command error,
        2 Historia unreachable or control disabled, 64 usage.
        """;

    public static ControlArgsResult Parse(IReadOnlyList<string> args, string cwd)
    {
        if (args.Count == 0)
            return Usage("A command is required.");

        if (args[0] is "--help" or "-h" or "-?")
            return new ControlArgsResult { ShowHelp = true };
        if (args[0] is "--version")
            return new ControlArgsResult { ShowVersion = true };

        if (!ControlVerbs.TryParse(args[0], out var verb) || verb == ControlVerb.Queue)
            return Usage($"Unknown command '{args[0]}'.");

        string absoluteCwd;
        try
        {
            absoluteCwd = Path.GetFullPath(cwd);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Usage("The working directory is not a valid path.");
        }

        var queueable = ControlVerbs.IsQueueable(verb);
        var takesLine = verb is ControlVerb.Diff or ControlVerb.Compare or ControlVerb.Changes;

        var positionals = new List<string>();
        var messages = new List<string>();
        string? repoOption = null;
        string? messageFile = null;
        string? rawNote = null;
        int? line = null;
        var staged = false;
        var noFocus = false;
        var noLaunch = false;
        var now = false;
        var optionsEnded = false;

        for (var i = 1; i < args.Count; i++)
        {
            var a = args[i];
            if (optionsEnded || a.Length == 0 || a[0] != '-' || a == "-")
            {
                positionals.Add(a);
                continue;
            }

            switch (a)
            {
                case "--":
                    optionsEnded = true;
                    break;
                case "-C" when queueable:
                    if (!TryTakeValue(args, ref i, out repoOption))
                        return Usage("-C needs a repository path.");
                    break;
                case "--no-focus" when queueable:
                    noFocus = true;
                    break;
                case "--no-launch" when queueable:
                    noLaunch = true;
                    break;
                case "--now" when queueable:
                    now = true;
                    break;
                case "--note" when queueable:
                    if (rawNote is not null)
                        return Usage("--note can be given once.");
                    if (!TryTakeValue(args, ref i, out rawNote))
                        return Usage("--note needs a message.");
                    break;
                case "--staged" when verb == ControlVerb.Changes:
                    staged = true;
                    break;
                case "--line" when takesLine:
                    if (!TryTakeValue(args, ref i, out var rawLine)
                        || !int.TryParse(rawLine, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                        || n <= 0)
                        return Usage("--line needs a positive line number.");
                    line = n;
                    break;
                case "-m" when verb == ControlVerb.ProposeCommit:
                    if (!TryTakeValue(args, ref i, out var message))
                        return Usage("-m needs a message.");
                    messages.Add(message!);
                    break;
                case "-F" when verb == ControlVerb.ProposeCommit:
                    if (!TryTakeValue(args, ref i, out messageFile))
                        return Usage("-F needs a file, or - for standard input.");
                    break;
                default:
                    return Usage($"Unknown option '{a}' for '{args[0]}'.");
            }
        }

        var queued = queueable && !now;

        string? note = null;
        if (rawNote is not null)
        {
            if (now)
                return Usage("--note is for queued commands; it cannot be used with --now.");
            if (!ControlInputValidation.NormalizeNote(rawNote, out note, out var noteError))
                return Usage(noteError!);
        }

        if (verb == ControlVerb.Changes && queued)
        {
            if (positionals.Count is < 1 or > MaxQueuedFiles)
                return Usage($"'changes' takes 1 to {MaxQueuedFiles} files, got {positionals.Count}.");
        }
        else
        {
            var expected = verb switch
            {
                ControlVerb.Show => 1,
                ControlVerb.Diff => 2,
                ControlVerb.Compare => 3,
                ControlVerb.Changes => 1,
                _ => 0,
            };
            if (positionals.Count != expected)
            {
                var suffix = verb == ControlVerb.Changes ? " with --now" : "";
                return Usage($"'{args[0]}' takes {expected} argument{(expected == 1 ? "" : "s")}{suffix}, got {positionals.Count}.");
            }
        }

        string? repo = null;
        if (queueable)
        {
            try
            {
                repo = repoOption is null ? absoluteCwd : Path.GetFullPath(repoOption, absoluteCwd);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return Usage("-C is not a valid path.");
            }
        }

        string? rev = null, otherRev = null;
        IReadOnlyList<string> files = [];
        switch (verb)
        {
            case ControlVerb.Show:
                rev = positionals[0];
                break;
            case ControlVerb.Diff:
                (rev, files) = (positionals[0], [positionals[1]]);
                break;
            case ControlVerb.Compare:
                (rev, otherRev, files) = (positionals[0], positionals[1], [positionals[2]]);
                break;
            case ControlVerb.Changes:
                files = positionals;
                break;
        }

        foreach (var r in new[] { rev, otherRev })
        {
            if (r is not null && !ControlInputValidation.TryValidateRevision(r, out var revError))
                return Usage(revError!);
        }

        foreach (var file in files)
        {
            if (file.Length == 0 || file.Contains('\0'))
                return Usage("A file path is required.");
        }

        string? commitMessage = null;
        if (verb == ControlVerb.ProposeCommit)
        {
            if (messages.Count > 0 && messageFile is not null)
                return Usage("Use -m or -F, not both.");
            if (messages.Count == 0 && messageFile is null)
                return Usage("propose-commit needs -m <text> or -F <file|->.");

            if (messages.Count > 0)
            {
                if (!ControlInputValidation.NormalizeCommitMessage(string.Join("\n\n", messages), out var normalized, out var messageError))
                    return Usage(messageError!);
                commitMessage = normalized;
            }
        }

        var request = new ControlRequest
        {
            Verb = queued ? ControlVerb.Queue : verb,
            Target = queued ? verb : null,
            Note = note,
            Cwd = absoluteCwd,
            Repo = repo,
            Rev = rev,
            OtherRev = otherRev,
            Path = files.Count > 0 ? files[0] : null,
            Paths = files.Count > 1 ? files : null,
            Line = line,
            Staged = staged,
            Focus = !queued && !noFocus,
            CommitMessage = commitMessage,
        };

        if (ControlJson.Serialize(request).Length >= ControlPipe.MaxLineChars)
            return Usage("The file names are too long to send in one request.");

        return new ControlArgsResult
        {
            NoLaunch = noLaunch,
            MessageFile = messageFile,
            Request = request,
        };
    }

    private static ControlArgsResult Usage(string error) => new() { UsageError = error };

    private static bool TryTakeValue(IReadOnlyList<string> args, ref int i, out string? value)
    {
        if (i + 1 >= args.Count)
        {
            value = null;
            return false;
        }

        value = args[++i];
        return true;
    }
}
