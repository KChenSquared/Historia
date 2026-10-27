namespace Historia.Services.Git;

/// <inheritdoc cref="IGitExecutableLocator"/>
public sealed partial class GitExecutableLocator : IGitExecutableLocator
{
    /// <summary>How long a git probe (<c>--version</c>, <c>--exec-path</c>) may run before it is killed.</summary>
    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The per-architecture directories of a Git for Windows install that hold the real
    /// <c>bin\git.exe</c>, in the order they are preferred.
    /// </summary>
    private static readonly string[] DirectGitPrefixes = ["ucrt64", "mingw64", "clangarm64"];

    private readonly object _sync = new();
    private readonly TaskCompletionSource<GitExecutableInfo> _resolvedTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private GitExecutableInfo _current = GitExecutableInfo.NotFound;

    public GitExecutableInfo Current
    {
        get { lock (_sync) return _current; }
    }

    public Task<GitExecutableInfo> WhenResolvedAsync(CancellationToken ct = default)
    {
        return ct.CanBeCanceled ? WaitWithCancellationAsync(ct) : _resolvedTcs.Task;

        async Task<GitExecutableInfo> WaitWithCancellationAsync(CancellationToken token)
        {
            return await _resolvedTcs.Task.WaitAsync(token).ConfigureAwait(false);
        }
    }

    public async Task<GitExecutableInfo> ResolveAsync(string? userOverridePath, CancellationToken ct = default)
    {
        try
        {
            return await ResolveCoreAsync(userOverridePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Every CLI call awaits the first resolution; a failed one must still release them
            // (they then fail classified as "git not found") rather than wait forever.
            _resolvedTcs.TrySetResult(GitExecutableInfo.NotFound with { NotFoundReason = $"Git resolution failed: {ex.Message}" });
            throw;
        }
    }

    private async Task<GitExecutableInfo> ResolveCoreAsync(string? userOverridePath, CancellationToken ct)
    {
        var discovered = await Task.Run(() => Discover(userOverridePath), ct).ConfigureAwait(false);
        GitExecutableInfo info;

        if (discovered is null)
        {
            info = GitExecutableInfo.NotFound;
        }
        else
        {
            var path = await ChooseGitPathAsync(discovered.Value.LauncherPath, discovered.Value.Path, ProbeTimeout, ct).ConfigureAwait(false);
            var version = await CaptureVersionAsync(path, ProbeTimeout, ct).ConfigureAwait(false);
            if (version is null || version < GitExecutableInfo.MinimumVersion)
            {
                var reason = version is null
                    ? $"Git at '{path}' did not report a valid version."
                    : $"Git {version} was found at '{path}', but Git {GitExecutableInfo.MinimumVersion} or newer is required.";
                info = new GitExecutableInfo
                {
                    Found = false,
                    Path = path,
                    Version = version,
                    NotFoundReason = reason,
                };
            }
            else
            {
                info = new GitExecutableInfo { Found = true, Path = path, Version = version };
            }
        }

        lock (_sync)
            _current = info;

        _resolvedTcs.TrySetResult(info);
        return info;
    }

    /// <summary>A discovered git: the path to run and the path it was derived from.</summary>
    private readonly record struct DiscoveredGit(string Path, string LauncherPath);

    private static DiscoveredGit? Discover(string? userOverridePath)
    {
        // 1. Explicit user override; used exactly as given.
        if (!string.IsNullOrWhiteSpace(userOverridePath) && File.Exists(userOverridePath))
            return new DiscoveredGit(userOverridePath, userOverridePath);

        // 2. PATH.
        var onPath = FindOnPath(Environment.GetEnvironmentVariable("PATH"));
        if (onPath is not null)
            return Direct(onPath);

        // 3. Well-known install locations.
        foreach (var candidate in WellKnownLocations())
        {
            if (File.Exists(candidate))
                return Direct(candidate);
        }

        // 4. Git for Windows registry key.
        if (OperatingSystem.IsWindows())
        {
            var fromRegistry = DiscoverFromWindowsRegistry();
            return fromRegistry is null ? null : Direct(fromRegistry);
        }

        return null;

        static DiscoveredGit Direct(string launcher) => new(PreferDirectGit(launcher), launcher);
    }

    /// <summary>
    /// The real git binary behind a Git for Windows launcher. <c>cmd\git.exe</c> and <c>bin\git.exe</c>
    /// start the real <c>&lt;prefix&gt;\bin\git.exe</c> as a child process; when the install holds
    /// <c>ucrt64</c>, <c>mingw64</c> or <c>clangarm64</c> (checked in that order) with one, that path is
    /// returned so each call runs a single process. Any other path is returned unchanged.
    /// </summary>
    internal static string PreferDirectGit(string path)
    {
        try
        {
            if (!string.Equals(Path.GetFileName(path), "git.exe", StringComparison.OrdinalIgnoreCase))
                return path;

            var directory = Path.GetDirectoryName(path);
            var directoryName = Path.GetFileName(directory);
            if (!string.Equals(directoryName, "cmd", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(directoryName, "bin", StringComparison.OrdinalIgnoreCase))
                return path;

            var installRoot = Path.GetDirectoryName(directory);
            if (installRoot is null)
                return path;

            foreach (var prefix in DirectGitPrefixes)
            {
                var candidate = Path.Combine(installRoot, prefix, "bin", "git.exe");
                if (File.Exists(candidate))
                    return candidate;
            }
        }
        catch (ArgumentException)
        {
            // Malformed path; leave it as discovered.
        }

        return path;
    }

    /// <summary>
    /// <paramref name="candidatePath"/> when it differs from <paramref name="launcherPath"/> and answers
    /// <c>--exec-path</c>; otherwise <paramref name="launcherPath"/>, with the reason logged.
    /// </summary>
    internal static async Task<string> ChooseGitPathAsync(string launcherPath, string candidatePath, TimeSpan timeout, CancellationToken ct = default)
    {
        if (string.Equals(launcherPath, candidatePath, StringComparison.OrdinalIgnoreCase))
            return launcherPath;

        var probe = await RunProbeAsync(candidatePath, "--exec-path", timeout, ct).ConfigureAwait(false);
        if (probe is { ExitCode: 0 } && !string.IsNullOrWhiteSpace(probe.Value.Output))
            return candidatePath;

        var why = probe is null
            ? "did not answer in time or could not start"
            : $"exited with code {probe.Value.ExitCode}";
        AppLog.Write(AppLogLevel.Info, "git", $"Direct git '{candidatePath}' {why} for --exec-path; using '{launcherPath}'.");
        return launcherPath;
    }

    /// <summary>The git binary's file name: <c>git.exe</c> on Windows, <c>git</c> elsewhere.</summary>
    internal static string ExecutableName => OperatingSystem.IsWindows() ? "git.exe" : "git";

    /// <summary>The first <see cref="ExecutableName"/> in the directories of a PATH-style value.</summary>
    internal static string? FindOnPath(string? pathVar)
    {
        foreach (var dir in (pathVar ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, ExecutableName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // Malformed PATH segment; skip.
            }
        }

        return null;
    }

    private static IEnumerable<string> WellKnownLocations()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[]
                     {
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                         Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     })
            {
                if (!string.IsNullOrEmpty(root))
                    yield return Path.Combine(root, "Git", "cmd", "git.exe");
            }

            yield break;
        }

        // A desktop launch on macOS does not inherit the shell's PATH, so Homebrew and MacPorts
        // installs are not on it.
        yield return "/usr/bin/git";
        yield return "/usr/local/bin/git";
        yield return "/opt/homebrew/bin/git";
        yield return "/opt/local/bin/git";
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? DiscoverFromWindowsRegistry()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = hklm.OpenSubKey(@"SOFTWARE\GitForWindows");
                if (key?.GetValue("InstallPath") is string installPath)
                {
                    var candidate = Path.Combine(installPath, "cmd", "git.exe");
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Registry unavailable; fall through.
            }
        }

        return null;
    }

    /// <summary>The version <c>git --version</c> reports, or null when it fails, is unparsable or exceeds <paramref name="timeout"/>.</summary>
    internal static async Task<Version?> CaptureVersionAsync(string gitPath, TimeSpan timeout, CancellationToken ct = default)
    {
        var probe = await RunProbeAsync(gitPath, "--version", timeout, ct).ConfigureAwait(false);
        return probe is null ? null : ParseVersion(probe.Value.Output);
    }

    /// <summary>
    /// Runs git with one argument and returns its exit code and standard output, or null when it cannot
    /// start or does not finish within <paramref name="timeout"/>; the process tree is then killed.
    /// </summary>
    private static async Task<(int ExitCode, string Output)?> RunProbeAsync(string gitPath, string argument, TimeSpan timeout, CancellationToken ct)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);
        Process? process = null;
        try
        {
            var psi = new ProcessStartInfo(gitPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add(argument);
            GitCliProcessSetup.ComposeEnvironment(psi, null, null);

            process = Process.Start(psi);
            if (process is null)
                return null;

            // Both pipes drain while git runs; stderr is read only so it can never fill.
            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            var output = await outputTask.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            await errorTask.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            return (process.ExitCode, output);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await KillBeforeDisposeAsync(process).ConfigureAwait(false);
            AppLog.Write(AppLogLevel.Warn, "git", $"'{gitPath} {argument}' did not finish within {timeout.TotalSeconds:0.#} s and was stopped.");
            return null;
        }
        catch (OperationCanceledException)
        {
            await KillBeforeDisposeAsync(process).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    // A disposed Process can no longer be killed, so the kill is attempted first.
    private static Task KillBeforeDisposeAsync(Process? process) =>
        process is null ? Task.CompletedTask : ProcessKiller.KillAsync(process);

    internal static Version? ParseVersion(string output)
    {
        // "git version 2.51.0.windows.2"
        var match = GitVersionRegex().Match(output);
        if (!match.Success)
            return null;

        var major = int.Parse(match.Groups["major"].Value);
        var minor = int.Parse(match.Groups["minor"].Value);
        var patch = match.Groups["patch"].Success ? int.Parse(match.Groups["patch"].Value) : 0;
        return new Version(major, minor, patch);
    }

    [GeneratedRegex(@"git version (?<major>\d+)\.(?<minor>\d+)(?:\.(?<patch>\d+))?", RegexOptions.CultureInvariant)]
    private static partial Regex GitVersionRegex();
}
