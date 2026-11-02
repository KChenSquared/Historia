namespace Historia.Platform;

/// <summary>
/// Single-instance guard. The first process to start holds a named mutex and runs a tiny
/// named-pipe listener; a later launch hands its arguments to that listener and exits, so there
/// is only ever one Historia writing <c>settings.json</c> / <c>profiles.json</c> / <c>session.json</c>.
/// The same pipe also serves control requests: a client that opens with the control header gets a
/// JSON answer from the handler instead of being treated as a launch. Windows is the parity
/// target; the mutex and the pipe both work on macOS / Linux as well, and any failure here
/// degrades to "just start a normal instance".
/// </summary>
internal static class SingleInstance
{
    // Global\, not Local\: a named pipe has no per-session namespace at all (unlike Mutex/Event/
    // Semaphore, it always lives in the single machine-wide Npfs namespace), so a Local\ mutex
    // here left the two objects in different scopes. In a second Windows session (fast user
    // switching, two RDP sessions) that meant TryAcquirePrimary in session B always succeeded —
    // its own Local\ mutex was unclaimed — while RunServerAsync's pipe bind failed forever
    // because session A's Historia already owns the (machine-global) pipe name; B then spun
    // retrying that bind every 200ms and never received forwarded launches. Global\ matches what
    // this guard is actually protecting: one Historia per Windows user account, machine-wide,
    // since %LOCALAPPDATA% (settings.json / profiles.json / session.json) is per-user, not
    // per-session — the same account logged in twice must still resolve to a single writer.
    private const string MutexName = @"Global\Historia.SingleInstance.Mutex";

    /// <summary>Consecutive pipe-bind failures in <see cref="RunServerAsync"/> before it gives up
    /// rather than retrying forever — degrading to "no forwarding, act as a lone instance" per
    /// this class's own stated policy.</summary>
    private const int MaxConsecutiveServerFailures = 10;

    /// <summary>Concurrent pipe instances. A slow control request must not block a second launch's
    /// hand-off, and a slot is taken before an instance is created so the listener never asks the OS
    /// for more instances than it allows: creating one past the limit throws, and that would count
    /// toward <see cref="MaxConsecutiveServerFailures"/>.</summary>
    private const int MaxConnections = 4;

    /// <summary>How long a connected client may take to finish sending, to read its answer (the pipe
    /// has no output buffer, so a write waits for the reader) and to close, before the server drops
    /// it and frees the slot.</summary>
    private static readonly TimeSpan ClientIoTimeout = TimeSpan.FromSeconds(10);

    // Held for the process lifetime so the name stays claimed; never released explicitly (the
    // OS frees it on exit, and an abandoned mutex from a crash is reclaimable by the next launch).
    private static Mutex? _held;

    /// <summary>
    /// True when this process is the primary instance. On any platform/permission problem it
    /// returns true (behave as a lone instance) rather than blocking startup.
    /// </summary>
    public static bool TryAcquirePrimary()
    {
        try
        {
            _held = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (!createdNew)
            {
                _held.Dispose();
                _held = null;
            }
            return createdNew;
        }
        catch
        {
            return true;
        }
    }

    internal enum SecondInstanceOutcome
    {
        /// <summary>A live primary accepted this launch's arguments.</summary>
        Forwarded,
        /// <summary>The previous primary is gone; this process took the mutex and should start the UI.</summary>
        BecamePrimary,
        /// <summary>A primary still holds the mutex but never answered — likely shutting down.</summary>
        StillBlocked,
    }

    /// <summary>
    /// Resolve a non-primary launch: hand off to a live primary, or — if none answers (the
    /// previous instance may still be shutting down) — poll up to <paramref name="wait"/> for
    /// the mutex to free (take over) or the pipe to come back (hand off).
    /// </summary>
    public static SecondInstanceOutcome ResolveSecondInstance(IReadOnlyList<string> args, TimeSpan wait)
    {
        if (ForwardToPrimary(args))
            return SecondInstanceOutcome.Forwarded;

        var deadline = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(200);

            try
            {
                var m = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
                if (createdNew)
                {
                    _held = m;
                    return SecondInstanceOutcome.BecamePrimary;
                }
                m.Dispose();
            }
            catch { /* keep polling */ }

            if (ForwardToPrimary(args))
                return SecondInstanceOutcome.Forwarded;
        }

        return SecondInstanceOutcome.StillBlocked;
    }

    /// <summary>
    /// Windows-only lightweight "still closing — Retry / Cancel" prompt (there is no Avalonia UI
    /// this early). Returns true if the user chose Retry. Elsewhere returns false (just abort).
    /// </summary>
    public static bool PromptRetry()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        const uint MB_RETRYCANCEL = 0x00000005;
        const uint MB_ICONINFORMATION = 0x00000040;
        const uint MB_SETFOREGROUND = 0x00010000;
        const uint MB_TOPMOST = 0x00040000;
        const int IDRETRY = 4;

        try
        {
            return MessageBoxW(IntPtr.Zero,
                "Another Historia window is still closing.\n\nWait a moment, then click Retry — or Cancel to abort this launch.",
                "Historia",
                MB_RETRYCANCEL | MB_ICONINFORMATION | MB_SETFOREGROUND | MB_TOPMOST) == IDRETRY;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>Best-effort hand-off of <paramref name="args"/> to the running primary. False if none answered.</summary>
    public static bool ForwardToPrimary(IReadOnlyList<string> args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", ControlPipe.PipeName, PipeDirection.Out);
            client.Connect(1500);
            using var w = new StreamWriter(client) { AutoFlush = true };
            foreach (var a in args)
                w.WriteLine(a);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Runs the primary's listener until <paramref name="ct"/> trips. A client that opens with the
    /// control header sends one JSON request, which <paramref name="onControl"/> answers. Any other
    /// client is a second launch sending zero or more newline-separated argument lines;
    /// <paramref name="onArgs"/> is invoked with them (the caller marshals to a UI thread if it
    /// needs one). A single empty connection (no lines) is still delivered as an empty array so the
    /// primary can just raise/focus its window.
    /// </summary>
    public static async Task RunServerAsync(
        Action<string[]> onArgs,
        Func<ControlRequest, CancellationToken, Task<ControlResponse>> onControl,
        CancellationToken ct,
        string? pipeName = null,
        TimeSpan? clientIoTimeout = null)
    {
        pipeName ??= ControlPipe.PipeName;
        var ioTimeout = clientIoTimeout ?? ClientIoTimeout;
        using var slots = new SemaphoreSlim(MaxConnections, MaxConnections);
        // Off Windows every instance of a pipe name shares one listening socket, closed when the
        // last instance is disposed. Clients waiting in its backlog while all slots are busy would
        // be reset when the busy connections finish together; this idle instance never accepts
        // and keeps the socket open for the life of the listener.
        using var keepListening = OperatingSystem.IsWindows() ? null : TryCreateServer(pipeName);
        var consecutiveFailures = 0;
        while (!ct.IsCancellationRequested)
        {
            var slotHeld = false;
            NamedPipeServerStream? server = null;
            try
            {
                await slots.WaitAsync(ct).ConfigureAwait(false);
                slotHeld = true;

                server = CreateServer(pipeName);
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                consecutiveFailures = 0;

                var connected = server;
                server = null;
                slotHeld = false;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ServeConnectionAsync(connected, onArgs, onControl, ioTimeout, ct).ConfigureAwait(false);
                    }
                    catch
                    {
                        // A malformed or vanished client only costs its own connection.
                    }
                    finally
                    {
                        await connected.DisposeAsync().ConfigureAwait(false);
                        slots.Release();
                    }
                }, CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // A bind failure that never clears (e.g. another process/session holding the
                // pipe name — see the MutexName comment) would otherwise retry forever, 5 times a
                // second, for the life of the process. Give up after a bounded run of failures
                // and act as a lone instance, per this class's own stated degrade policy.
                if (++consecutiveFailures >= MaxConsecutiveServerFailures)
                    return;
                try { await Task.Delay(200, ct).ConfigureAwait(false); } catch { return; }
            }
            finally
            {
                if (server is not null)
                    await server.DisposeAsync().ConfigureAwait(false);
                if (slotHeld)
                    slots.Release();
            }
        }
    }

    private static async Task ServeConnectionAsync(
        NamedPipeServerStream server,
        Action<string[]> onArgs,
        Func<ControlRequest, CancellationToken, Task<ControlResponse>> onControl,
        TimeSpan ioTimeout,
        CancellationToken ct)
    {
        using var reader = ControlPipe.CreateReader(server);
        using var writer = ControlPipe.CreateWriter(server);

        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        readCts.CancelAfter(ioTimeout);
        var read = await ControlPipe.ReadAsync(reader, readCts.Token).ConfigureAwait(false);

        ControlResponse reply;
        switch (read.Kind)
        {
            case ControlReadKind.Empty:
                onArgs([]);
                return;
            case ControlReadKind.Legacy:
                onArgs(read.Lines!.ToArray());
                return;
            case ControlReadKind.Invalid:
                reply = read.Failure!;
                break;
            default:
                try
                {
                    reply = await onControl(read.Request!, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch
                {
                    reply = ControlResponse.Fail(ControlError.Internal, "Historia could not handle the request.");
                }
                break;
        }

        using var writeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        writeCts.CancelAfter(ioTimeout);
        try
        {
            await ControlPipe.WriteResponseAsync(writer, reply, writeCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            // The client never read its answer, or is gone; drop the connection and free the slot.
            return;
        }

        // Let the client read its answer and close first; disposing the server end right away
        // can discard a reply the client has not read yet.
        using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        drainCts.CancelAfter(ioTimeout);
        try
        {
            await reader.ReadToEndAsync(drainCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // The client is gone or never closed; the answer was already written.
        }
    }

    /// <summary>Builds one listener pipe instance, restricted to the current user: on Windows via
    /// an explicit ACL — without one, any user on the machine could connect and hand this listener
    /// arguments that get opened and bring the window forward — and elsewhere with
    /// <see cref="PipeOptions.CurrentUserOnly"/>. A second concurrent instance additionally needs
    /// the <see cref="PipeAccessRights.CreateNewInstance"/> right.</summary>
    private static NamedPipeServerStream CreateServer(string pipeName)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new PipeSecurity();
            var self = WindowsIdentity.GetCurrent().User;
            if (self is not null)
            {
                security.AddAccessRule(new PipeAccessRule(
                    self, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
            }

            return NamedPipeServerStreamAcl.Create(
                pipeName, PipeDirection.InOut, MaxConnections, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                inBufferSize: 0, outBufferSize: 0, pipeSecurity: security);
        }

        // One more instance than connections: the listener's idle keep-alive instance.
        return new NamedPipeServerStream(
            pipeName, PipeDirection.InOut, MaxConnections + 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    private static NamedPipeServerStream? TryCreateServer(string pipeName)
    {
        try
        {
            return CreateServer(pipeName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The accept loop's own bind retries and degrade policy cover a name that is taken.
            return null;
        }
    }
}
