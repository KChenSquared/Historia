namespace Historia.Services.Network;

/// <summary>Resolves an SSH host as written in a remote URL to the host name OpenSSH would
/// actually connect to, after <c>~/.ssh/config</c> <c>Host</c> / <c>Match</c> blocks.</summary>
public interface ISshHostResolver
{
    /// <summary>The <c>HostName</c> OpenSSH resolves <paramref name="host"/> to — the host
    /// itself when no config block renames it — or null when it cannot be determined.</summary>
    Task<string?> ResolveHostNameAsync(string host, CancellationToken ct);
}

/// <summary>
/// Runs <c>ssh -G &lt;host&gt;</c>, which prints the configuration after evaluating
/// <c>Host</c> and <c>Match</c> blocks and exits without connecting, and reads its
/// <c>hostname</c> line. Definite results are cached per host for the session; a lookup that
/// timed out is not, so the next call asks again. Any failure — no <c>ssh</c> on PATH, a
/// non-zero exit, a config it cannot parse, the timeout — yields null.
/// </summary>
public sealed class SshHostResolver : ISshHostResolver
{
    public static SshHostResolver Shared { get; } = new();

    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The outcome of one lookup: <see cref="Definite"/> is false when the answer is
    /// only "unknown for now" (the lookup timed out), which must not be remembered.</summary>
    internal readonly record struct Lookup(string? HostName, bool Definite);

    private readonly string? _configPath;
    private readonly Func<string, CancellationToken, Task<Lookup>> _lookup;
    private readonly ConcurrentDictionary<string, string?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private int _processLaunches;

    public SshHostResolver()
    {
        _lookup = RunSshAsync;
    }

    /// <summary>Reads <paramref name="configPath"/> (<c>ssh -F</c>) instead of the user's and
    /// the system's config files.</summary>
    internal SshHostResolver(string configPath)
    {
        _configPath = configPath;
        _lookup = RunSshAsync;
    }

    /// <summary>Resolves through <paramref name="lookup"/> instead of running <c>ssh</c>.</summary>
    internal SshHostResolver(Func<string, CancellationToken, Task<Lookup>> lookup) => _lookup = lookup;

    internal int ProcessLaunches => Volatile.Read(ref _processLaunches);

    /// <summary>The OpenSSH client <c>Process.Start</c> would find on PATH, or null.</summary>
    public static string? FindSshOnPath()
    {
        var name = OperatingSystem.IsWindows() ? "ssh.exe" : "ssh";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim().Trim('"'), name);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is skipped, as the OS loader would.
            }
        }
        return null;
    }

    public async Task<string?> ResolveHostNameAsync(string host, CancellationToken ct)
    {
        // A leading '-' would be read as an ssh option, not a host.
        if (string.IsNullOrWhiteSpace(host) || host.StartsWith('-') || host.Any(char.IsWhiteSpace))
            return null;

        if (_cache.TryGetValue(host, out var cached))
            return cached;

        var lookup = await _lookup(host, ct).ConfigureAwait(false);
        if (lookup.Definite)
            _cache[host] = lookup.HostName;
        return lookup.HostName;
    }

    private async Task<Lookup> RunSshAsync(string host, CancellationToken ct)
    {
        var ssh = FindSshOnPath();
        if (ssh is null)
        {
            AppLog.Write(AppLogLevel.Info, "network", $"ssh -G {host}: no ssh on PATH");
            return new Lookup(null, true);
        }

        var psi = new ProcessStartInfo(ssh)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (_configPath is not null)
        {
            psi.ArgumentList.Add("-F");
            psi.ArgumentList.Add(_configPath);
        }
        psi.ArgumentList.Add("-G");
        psi.ArgumentList.Add(host);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ResolveTimeout);

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            AppLog.Write(AppLogLevel.Info, "network", $"ssh -G {host}: could not start {ssh}: {ex.Message}");
            return new Lookup(null, true);
        }

        Interlocked.Increment(ref _processLaunches);
        using (process)
        {
            try
            {
                process.StandardInput.Close();
                // Dedicated threads: the pipes are read synchronously, so no thread-pool thread is
                // parked on them while ssh runs.
                var stdout = ReadToEndOnDedicatedThread(process.StandardOutput);
                var stderr = ReadToEndOnDedicatedThread(process.StandardError);
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                await Task.WhenAll(stdout, stderr).WaitAsync(timeout.Token).ConfigureAwait(false);

                if (process.ExitCode != 0)
                {
                    AppLog.Write(AppLogLevel.Info, "network", $"ssh -G {host} via {ssh}: exit {process.ExitCode}");
                    return new Lookup(null, true);
                }

                var hostName = ParseHostName(stdout.Result);
                AppLog.Write(AppLogLevel.Info, "network", $"ssh -G {host} via {ssh}: hostname {hostName ?? "(none)"}");
                return new Lookup(hostName, true);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                ct.ThrowIfCancellationRequested();
                AppLog.Write(AppLogLevel.Info, "network", $"ssh -G {host} via {ssh}: timed out");
                return new Lookup(null, false);
            }
        }
    }

    private static Task<string> ReadToEndOnDedicatedThread(StreamReader reader)
        => Task.Factory.StartNew(() =>
        {
            try { return reader.ReadToEnd(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { return ""; } // killed mid-read
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    internal static string? ParseHostName(string output)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("hostname ", StringComparison.OrdinalIgnoreCase))
            {
                var value = line["hostname ".Length..].Trim();
                return value.Length == 0 ? null : value;
            }
        }
        return null;
    }

    private static void TryKill(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
    }
}
