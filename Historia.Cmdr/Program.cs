namespace Historia.Cmdr;

/// <summary>
/// <c>historia-cmdr</c>: the command-line client for Historia's local control pipe. It waits for the
/// answer, prints it as one line of JSON and exits with a code an agent can branch on. It can start
/// Historia when none is running. Everything it sends is navigation, a commit-message proposal, or
/// either of those queued for the user to open later.
/// </summary>
internal static class Program
{
    private static readonly TimeSpan FirstConnectTimeout = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan LaunchWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan LaunchRetryInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RunCap = TimeSpan.FromSeconds(60);

    private static async Task<int> Main(string[] args)
    {
        var parsed = ControlArgs.Parse(args, Directory.GetCurrentDirectory());
        if (parsed.ShowHelp)
        {
            Console.Out.WriteLine(ControlArgs.UsageText);
            return ControlExitCode.Ok;
        }

        if (parsed.ShowVersion)
        {
            Console.Out.WriteLine(Historia.Diagnostics.AppVersion.Current);
            return ControlExitCode.Ok;
        }

        if (parsed.IsUsageError)
            return UsageError(parsed.UsageError!);

        var request = parsed.Request!;
        if ((request.Target ?? request.Verb) == ControlVerb.ProposeCommit && parsed.MessageFile is not null)
        {
            string text;
            try
            {
                text = ReadMessage(parsed.MessageFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return UsageError($"Cannot read the message file: {ex.Message}");
            }

            if (!ControlInputValidation.NormalizeCommitMessage(text, out var normalized, out var error))
                return UsageError(error!);
            request = request with { CommitMessage = normalized };
        }

        using var cap = new CancellationTokenSource(RunCap);
        try
        {
            return await RunAsync(request, parsed.NoLaunch, cap.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Print(ControlResponse.Fail(ControlError.Timeout, "historia-cmdr gave up waiting."));
        }
    }

    private static async Task<int> RunAsync(ControlRequest request, bool noLaunch, CancellationToken ct)
    {
        var connected = await ControlPipe.ConnectClientAsync(FirstConnectTimeout, ct).ConfigureAwait(false);
        if (connected.Outcome == ControlConnectOutcome.Unreachable && ControlVerbs.MayLaunch(request.Verb) && !noLaunch)
            connected = await LaunchAndConnectAsync(ct).ConfigureAwait(false);

        switch (connected.Outcome)
        {
            case ControlConnectOutcome.Incompatible:
                return Print(ControlResponse.Fail(ControlError.Unreachable,
                    "The running Historia is too old for historia-cmdr; restart it."));
            case ControlConnectOutcome.Unreachable:
                return Print(ControlResponse.Fail(ControlError.Unreachable));
        }

        using var client = connected.Stream!;
        if (request.WantsFocus && ControlVerbs.MayLaunch(request.Verb))
            TryAllowForeground(client);

        using var writer = ControlPipe.CreateWriter(client);
        using var reader = ControlPipe.CreateReader(client);
        await ControlPipe.WriteRequestAsync(writer, request, ct).ConfigureAwait(false);
        var response = await ControlPipe.ReadResponseAsync(reader, ct).ConfigureAwait(false);

        if (response is not null && ControlVerbs.IsQueueUnsupported(request, response))
            return Print(ControlResponse.Fail(ControlError.Unreachable,
                "The running Historia is too old to queue items; restart it."));

        return Print(response ?? ControlResponse.Fail(ControlError.Internal, "Historia closed the connection without answering."));
    }

    private static async Task<ControlConnectResult> LaunchAndConnectAsync(CancellationToken ct)
    {
        var exe = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "Historia.exe" : "Historia");
        if (!File.Exists(exe))
            return new ControlConnectResult(ControlConnectOutcome.Unreachable, null);

        try
        {
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = AppContext.BaseDirectory })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new ControlConnectResult(ControlConnectOutcome.Unreachable, null);
        }

        var deadline = DateTime.UtcNow + LaunchWait;
        while (DateTime.UtcNow < deadline)
        {
            var attempt = await ControlPipe.ConnectClientAsync(LaunchRetryInterval, ct).ConfigureAwait(false);
            if (attempt.Outcome != ControlConnectOutcome.Unreachable)
                return attempt;
        }

        return new ControlConnectResult(ControlConnectOutcome.Unreachable, null);
    }

    private static string ReadMessage(string source)
    {
        if (source != "-")
            return File.ReadAllText(Path.GetFullPath(source), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        using var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return stdin.ReadToEnd();
    }

    private static int Print(ControlResponse response)
    {
        Console.Out.WriteLine(ControlJson.Serialize(response));
        Console.Out.Flush();
        return ControlExitCode.For(response);
    }

    private static int UsageError(string message)
    {
        Console.Error.WriteLine("historia-cmdr: " + message);
        Console.Error.WriteLine(ControlArgs.UsageText);
        return ControlExitCode.Usage;
    }

    /// <summary>Lets the Historia process we are talking to bring its window to the front. Best
    /// effort: Windows may still only flash the taskbar button.</summary>
    private static void TryAllowForeground(NamedPipeClientStream client)
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            if (GetNamedPipeServerProcessId(client.SafePipeHandle.DangerousGetHandle(), out var pid))
                AllowSetForegroundWindow(pid);
        }
        catch
        {
            // A missing privilege or an odd handle only costs the focus nicety.
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint serverProcessId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
