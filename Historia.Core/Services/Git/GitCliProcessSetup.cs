namespace Historia.Services.Git;

/// <summary>
/// Process-setup helpers shared by every Historia-launched <c>git.exe</c> invocation: the
/// deterministic <c>-c</c>/<c>--no-optional-locks</c> prefix and the environment
/// variables that route credential prompts through Historia's own askpass helper.
/// Shared by <see cref="GitCliService"/> and <see cref="GitLogStream"/> — a long-lived
/// streaming process, not a request/response invocation — so the two use the exact same setup
/// and never drift. Internal: not part of <see cref="IGitCliService"/>'s surface.
/// </summary>
internal static class GitCliProcessSetup
{
    /// <summary>Prepended to every invocation so output is machine-parseable and read-only
    /// commands never take the index lock. <c>merge.conflictStyle=zdiff3</c> makes every conflict
    /// Historia creates (merge, pull, rebase and its <c>--continue</c> stops, cherry-pick, revert,
    /// stash apply/pop, <c>checkout -m</c>) carry the common-ancestor section, so the resolver can
    /// offer "Use base". Being on the command line it overrides the user's own
    /// <c>merge.conflictStyle</c> only inside Historia-launched processes; their terminal is
    /// unaffected.</summary>
    public static readonly string[] DeterministicPrefix =
    [
        "-c", "color.ui=false",
        "-c", "core.pager=cat",
        "-c", "core.quotepath=false",
        "-c", "merge.conflictStyle=zdiff3",
        "--no-optional-locks",
    ];

    // The Historia executable re-invoked with a private switch acts as the askpass helper.
    // No separate binary is installed. Computed once — it never varies per invocation.
    private static readonly string AskPassHelperPath = Environment.ProcessPath ?? string.Empty;

    /// <summary>Built per invocation; the Historia process's own environment is never mutated.</summary>
    public static void ComposeEnvironment(
        ProcessStartInfo psi, GitProfile? profile, IReadOnlyDictionary<string, string>? extraEnvironment)
    {
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        psi.Environment["LC_ALL"] = "C"; // Stable, parseable English message text.

        if (!string.IsNullOrEmpty(AskPassHelperPath))
        {
            psi.Environment["GIT_ASKPASS"] = AskPassHelperPath;
            psi.Environment["SSH_ASKPASS"] = AskPassHelperPath;
            psi.Environment["HISTORIA_ASKPASS"] = "1"; // Marks a re-invocation as the helper.
        }

        if (profile?.SshKeyPath is { } key && !string.IsNullOrWhiteSpace(key))
        {
            // The key path is passed via its own variable so a path with spaces or quotes is
            // never substituted into the command string itself.
            psi.Environment["HISTORIA_SSH_KEY"] = key;
            psi.Environment["GIT_SSH_COMMAND"] = "ssh -i \"$HISTORIA_SSH_KEY\" -o IdentitiesOnly=yes";
        }

        if (extraEnvironment is not null)
        {
            foreach (var (k, v) in extraEnvironment)
                psi.Environment[k] = v;
        }
    }

    /// <summary>Queues a kill of the whole process tree on <see cref="ProcessKiller"/>'s thread and
    /// returns at once; "already gone" cases are swallowed there. The process may still be alive
    /// when this returns — await <see cref="ProcessKiller.KillAsync"/> before disposing it.</summary>
    public static void TryKill(Process process) => ProcessKiller.Kill(process);
}
