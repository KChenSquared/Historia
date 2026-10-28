namespace Historia.Tests;

/// <summary>Behaviour of a single <see cref="TerminalSession"/> against a fake pty — OSC
/// hardening, the UTF-8 decoder, exit handling, resize.</summary>
[Trait("tier", "unit")]
public class TerminalSessionTests
{
    private static TerminalSession NewSession(out FakePtyConnection pty, int scrollback = 1000)
    {
        pty = new FakePtyConnection();
        var origin = TerminalOrigin.Home("/home/me");
        return new TerminalSession(origin, pty, new ImmediateUiDispatcher(), 80, 24, scrollback);
    }

    [Theory]
    [InlineData("plain title", "plain title")]
    [InlineData("has\ttabs\tand\nnewlines", "hastabsandnewlines")]
    [InlineData("   trimmed   ", "trimmed")]
    public void SanitiseTitle_StripsControlAndTrims(string input, string expected)
        => Assert.Equal(expected, TerminalSession.SanitiseTitle(input));

    [Fact]
    public void SanitiseTitle_TruncatesToSixty()
    {
        var s = TerminalSession.SanitiseTitle(new string('x', 200));
        Assert.Equal(60, s!.Length);
    }

    [Fact]
    public void SanitiseTitle_EmptyBecomesNull()
    {
        Assert.Null(TerminalSession.SanitiseTitle(""));
        Assert.Null(TerminalSession.SanitiseTitle("\x01\x02"));
    }

    [Fact]
    public void Osc0_SetsReportedTitle_Sanitised()
    {
        using var session = NewSession(out var pty);
        string? seen = null;
        session.TitleChanged += (_, _) => seen = session.ReportedTitle;

        pty.EmitOutput("\x1b]0;my-shell\x07");

        Assert.Equal("my-shell", session.ReportedTitle);
        Assert.Equal("my-shell", seen);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe")]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe")]
    [InlineData("/usr/bin/bash.exe")]
    public void LooksLikeExecutablePath_RejectsRootedExe(string title)
        => Assert.True(TerminalSession.LooksLikeExecutablePath(title));

    [Theory]
    [InlineData("powershell.exe")]        // bare name, not rooted
    [InlineData("Building myapp.exe")]    // sentence that happens to end in .exe
    [InlineData("~/projects/app")]
    [InlineData("my-shell")]
    public void LooksLikeExecutablePath_AllowsEverythingElse(string title)
        => Assert.False(TerminalSession.LooksLikeExecutablePath(title));

    [Fact]
    public void Osc0_ExecutablePathTitle_IsRejected_KeepsOriginFallback()
    {
        using var session = NewSession(out var pty);

        pty.EmitOutput("\x1b]0;C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\x07");

        Assert.Null(session.ReportedTitle);
    }

    private static (TerminalSession Session, FakePtyConnection Pty, RecordingUiDispatcher Dispatcher, List<string> Seen) NewClipboardSession()
    {
        var dispatcher = new RecordingUiDispatcher();
        var pty = new FakePtyConnection();
        var session = new TerminalSession(TerminalOrigin.Home("/home/me"), pty, dispatcher, 80, 24, 1000);
        var seen = new List<string>();
        session.ClipboardWriteRequested += (_, e) => seen.Add(e.Text);
        return (session, pty, dispatcher, seen);
    }

    [Fact]
    public void Osc52Write_RaisesClipboardWriteOnDispatcher()
    {
        var (session, pty, dispatcher, seen) = NewClipboardSession();
        using var _ = session;

        pty.EmitOutput("\x1b]52;c;aGk=\x07");

        Assert.Empty(seen); // posted, never raised inline under the session gate
        dispatcher.DrainPosted();
        Assert.Equal(["hi"], seen);
    }

    [Fact]
    public void Osc52Write_EmptyPayload_Ignored()
    {
        var (session, pty, dispatcher, seen) = NewClipboardSession();
        using var _ = session;

        pty.EmitOutput("\x1b]52;c;\x07");
        dispatcher.DrainPosted();

        Assert.Empty(seen);
    }

    [Fact]
    public void Osc52Write_OverCap_Ignored()
    {
        // XTerm's parser already drops an OSC payload of 1 << 20 chars or more
        // (EscapeSequenceParser.MaxOscPayloadChars), which decodes to under 1 MiB, so the cap
        // itself is pinned on the filter.
        Assert.Null(TerminalSession.ClipboardTextFrom(new byte[TerminalSession.MaxClipboardWriteBytes + 1]));
        Assert.NotNull(TerminalSession.ClipboardTextFrom(Enumerable.Repeat((byte)'a', TerminalSession.MaxClipboardWriteBytes).ToArray()));

        var (session, pty, dispatcher, seen) = NewClipboardSession();
        using var _ = session;
        var huge = Convert.ToBase64String(Enumerable.Repeat((byte)'a', 800_000).ToArray());
        pty.EmitOutput("\x1b]52;c;" + huge + "\x07");
        dispatcher.DrainPosted();

        Assert.Empty(seen);
    }

    [Fact]
    public void Osc52Write_StripsNul()
    {
        var (session, pty, dispatcher, seen) = NewClipboardSession();
        using var _ = session;
        var payload = Convert.ToBase64String(new byte[] { (byte)'a', 0, (byte)'b', 0 });

        pty.EmitOutput("\x1b]52;c;" + payload + "\x07");
        dispatcher.DrainPosted();

        Assert.Equal(["ab"], seen);
    }

    [Fact]
    public void Osc52Write_InvalidBase64_Ignored()
    {
        // XTerm hands invalid base64 on as an empty write (xterm's "clear" idiom, see
        // InputHandler.Osc.cs HandleClipboard); Historia never clears the clipboard.
        var (session, pty, dispatcher, seen) = NewClipboardSession();
        using var _ = session;

        pty.EmitOutput("\x1b]52;c;!!not-base64!!\x07");
        dispatcher.DrainPosted();

        Assert.Empty(seen);
    }

    [Fact]
    public void SplitUtf8AcrossChunks_DecodesWithoutMangling()
    {
        using var session = NewSession(out var pty);
        var bytes = System.Text.Encoding.UTF8.GetBytes("héllo"); // 'é' is two bytes
        pty.EmitOutput(bytes[..2]);
        pty.EmitOutput(bytes[2..]);

        var line = session.ReadBuffer(v => v.GetLineText(0));
        Assert.Contains("héllo", line);
    }

    [Fact]
    public void FourByteEmojiSplitThreeWays_RendersOneCell()
    {
        using var session = NewSession(out var pty);
        var bytes = new byte[] { 0xF0, 0x9F, 0x98, 0x80 }; // U+1F600, 😀
        pty.EmitOutput(bytes[..1]);
        pty.EmitOutput(bytes[1..3]);
        pty.EmitOutput(bytes[3..]);

        var cell = session.ReadBuffer(v => v.GetCell(0, 0));
        Assert.Equal("😀", cell.Content);
        Assert.Equal(2, cell.Width);
    }

    [Fact]
    public void InvalidUtf8_RendersReplacementAndContinues()
    {
        using var session = NewSession(out var pty);
        pty.EmitOutput(new byte[] { 0xC3, 0x28, 0x41 }); // C3 wants a continuation byte; 28 is '('

        var line = session.ReadBuffer(v => v.GetLineText(0));
        Assert.StartsWith("�(A", line);
    }

    [Fact]
    public void ExitBannerAfterTruncatedSequence_IsIntact()
    {
        using var session = NewSession(out var pty);
        pty.EmitOutput(new byte[] { 0xE2, 0x82 }); // the first two bytes of a three-byte sequence
        pty.EmitExit(0);

        var text = session.ReadBuffer(v =>
            string.Join("\n", Enumerable.Range(0, v.TotalLines).Select(v.GetLineText)));
        Assert.Contains("[process exited with code 0]", text);
        Assert.Contains("Press Enter to start a new shell.", text);
    }

    [Fact]
    public void ShellExit_MovesToExitedAndAnnounces()
    {
        using var session = NewSession(out var pty);
        pty.EmitExit(3);

        Assert.Equal(TerminalSessionState.Exited, session.State);
        Assert.Equal(3, session.ExitCode);
        var text = session.ReadBuffer(v =>
            string.Join("\n", Enumerable.Range(0, v.TotalLines).Select(v.GetLineText)));
        Assert.Contains("process exited with code 3", text);
    }

    [Fact]
    public void Resize_ForwardsToPty()
    {
        using var session = NewSession(out var pty);
        session.Resize(100, 40);
        Assert.Contains((100, 40), pty.Resizes);
    }

    [Fact]
    public void Resize_WhenThePtyThrows_DoesNotEscapeAndStillReflowsTheEmulator()
    {
        var pty = new ThrowingResizePty();
        using var session = new TerminalSession(
            TerminalOrigin.Home("/home/me"), pty, new ImmediateUiDispatcher(), 80, 24, 1000);

        var ex = Record.Exception(() => session.Resize(100, 40));

        Assert.Null(ex);
        Assert.Equal(1, pty.ResizeCalls);
        Assert.Equal(100, session.ReadBuffer(v => v.Columns));
        Assert.Equal(40, session.ReadBuffer(v => v.Rows));
    }

    [Fact]
    public void Resize_AfterDispose_IsIgnored()
    {
        var session = NewSession(out var pty);
        session.Dispose();

        session.Resize(100, 40);

        Assert.Empty(pty.Resizes);
    }

    /// <summary>A pty whose resize always fails, as a pty that has already gone away would.</summary>
    private sealed class ThrowingResizePty : IPtyConnection
    {
        public int ResizeCalls { get; private set; }

        public event Action<ReadOnlyMemory<byte>>? OutputReceived { add { } remove { } }
        public event Action<int>? Exited { add { } remove { } }

        public void Write(ReadOnlySpan<byte> data) { }

        public void Resize(int columns, int rows)
        {
            ResizeCalls++;
            throw new InvalidOperationException("pty is gone");
        }

        public void Kill() { }
        public void Dispose() { }
    }

    [Fact]
    public void RapidOutput_PostsToTheDispatcherOnceWhileACoalesceWindowIsOpen()
    {
        // A deferred dispatcher: unlike ImmediateUiDispatcher, Post/Start don't run inline, so
        // multiple pty chunks can queue up before anything actually reaches the "UI thread" —
        // exposing the one-Post-per-chunk bug that ImmediateUiDispatcher's synchronous chain
        // can't observe (each of its calls resolves fully before the next MarkBufferDirty runs).
        var dispatcher = new RecordingUiDispatcher();
        var pty = new FakePtyConnection();
        using var session = new TerminalSession(TerminalOrigin.Home("/home/me"), pty, dispatcher, 80, 24, 1000);

        for (var i = 0; i < 50; i++)
            pty.EmitOutput($"line {i}\r\n");

        // A coalescing window is still open (the timer hasn't ticked), so only the first chunk's
        // MarkBufferDirty call should have reached the dispatcher.
        Assert.Equal(1, dispatcher.PostCount);

        dispatcher.DrainPosted();
        Assert.Equal(1, dispatcher.LastTimer!.StartCount);

        var raised = 0;
        session.BufferChanged += (_, _) => raised++;
        dispatcher.LastTimer!.Fire(); // simulate the 16ms coalescing tick
        Assert.Equal(1, raised);

        // The window closed on that tick — new output opens a fresh one and posts again.
        pty.EmitOutput("more\r\n");
        Assert.Equal(2, dispatcher.PostCount);
    }

    /// <summary>A pty whose callbacks stay wired after the session unsubscribes — standing in for a
    /// callback that was already running on the pty's own thread when the session closed.</summary>
    private sealed class StickyPty : IPtyConnection
    {
        private Action<ReadOnlyMemory<byte>>? _output;
        private Action<int>? _exited;

        public event Action<ReadOnlyMemory<byte>>? OutputReceived { add => _output += value; remove { } }
        public event Action<int>? Exited { add => _exited += value; remove { } }

        public void Write(ReadOnlySpan<byte> data) { }
        public void Resize(int columns, int rows) { }
        public void Kill() { }
        public void Dispose() { }

        public void EmitOutput(string text) => _output?.Invoke(System.Text.Encoding.UTF8.GetBytes(text));
        public void EmitExit(int code) => _exited?.Invoke(code);
    }

    [Fact]
    public void Exit_after_dispose_is_ignored()
    {
        var pty = new StickyPty();
        var session = new TerminalSession(TerminalOrigin.Home("/home/me"), pty, new ImmediateUiDispatcher(), 80, 24, 1000);
        var stateChanges = 0;
        session.StateChanged += (_, _) => stateChanges++;
        session.Dispose();

        var ex = Record.Exception(() => pty.EmitExit(7));

        Assert.Null(ex);
        Assert.Equal(TerminalSessionState.Running, session.State);
        Assert.Equal(0, stateChanges);
    }

    [Fact]
    public async Task Output_racing_dispose_never_throws()
    {
        for (var i = 0; i < 200; i++)
        {
            var pty = new StickyPty();
            var session = new TerminalSession(TerminalOrigin.Home("/home/me"), pty, new ImmediateUiDispatcher(), 80, 24, 1000);
            using var ready = new Barrier(2);

            var emitter = Task.Run(() =>
            {
                ready.SignalAndWait();
                for (var n = 0; n < 200; n++)
                    pty.EmitOutput($"line {n}\r\n");
            });
            var closer = Task.Run(() =>
            {
                ready.SignalAndWait();
                Thread.SpinWait(i * 50);
                session.Dispose();
            });

            await Task.WhenAll(emitter, closer);
        }
    }
}
