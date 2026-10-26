namespace Historia.Services.Terminal;

public enum TerminalSessionState { Starting, Running, Exited, Failed }

/// <summary>
/// One live terminal: a pty, an <c>XTerm.NET</c> emulator, and the pump between them.
/// <para>
/// The emulator's screen buffer is guarded by a single-slot lock. The pty read loop mutates on a
/// background thread; events are raised on the UI dispatcher.
/// <c>BufferChanged</c> is coalesced on a ~16 ms trailing window so a flood renders once per
/// frame, not once per chunk — the parser still sees every byte in order.
/// </para>
/// <para>
/// Pty chunks go to the emulator as raw bytes, and the emulator carries a UTF-8 sequence split
/// across two chunks itself, so a character on a chunk boundary is never mangled.
/// The session does no per-row work on the output path: the control repaints on the coalesced
/// <c>BufferChanged</c> and reads the rows it draws under the gate.
/// </para>
/// </summary>
public sealed class TerminalSession : IDisposable
{
    private readonly IPtyConnection _pty;
    private readonly IUiDispatcher _dispatcher;
    private readonly XTermLib.Terminal _term;
    private readonly object _gate = new();
    private readonly IUiTimer _coalesceTimer;

    private bool _bufferDirtyPending;
    private bool _coalesceTimerArmed;
    private int _disposed;

    public Guid Id { get; } = Guid.NewGuid();
    public TerminalOrigin Origin { get; }
    public TerminalSessionState State { get; private set; } = TerminalSessionState.Starting;
    public int? ExitCode { get; private set; }

    /// <summary>OSC 0/2 title, already sanitised: control chars stripped, ≤ 60 chars.
    /// Null until the child sets one.</summary>
    public string? ReportedTitle { get; private set; }

    /// <summary>Last OSC 8 hyperlink target the child announced, if any (validated by the
    /// control before it is opened).</summary>
    public string? LastHyperlink { get; private set; }

    /// <summary>A one-line notice shown as the session's first line — e.g. the start directory
    /// fell back. Not a modal.</summary>
    public string? StartupNotice { get; }

    /// <summary>Live DEC key/keypad modes, read under the gate — the input the
    /// <see cref="KeyEncoder"/> needs.</summary>
    public TerminalModes CurrentModes
    {
        get { lock (_gate) return new TerminalModes(_term.ApplicationCursorKeys, _term.ApplicationKeypad); }
    }

    /// <summary>Whether the child has enabled bracketed-paste (DECSET 2004).</summary>
    public bool BracketedPasteMode
    {
        get { lock (_gate) return _term.BracketedPasteMode; }
    }

    public event EventHandler? BufferChanged;
    public event EventHandler? StateChanged;
    public event EventHandler? TitleChanged;

    /// <summary>A program asked to write the system clipboard (OSC 52). Raised on the UI
    /// dispatcher; whether to honour it is the service's call.</summary>
    public event EventHandler<TerminalClipboardWriteEventArgs>? ClipboardWriteRequested;

    /// <summary>Largest decoded clipboard write passed on; anything larger is dropped.</summary>
    public const int MaxClipboardWriteBytes = 1024 * 1024;

    /// <summary>The Kitty keyboard state the child has negotiated, read under the gate.</summary>
    public TerminalKeyboardProtocol KeyboardProtocol
    {
        get
        {
            lock (_gate)
            {
                if (!_term.KittyKeyboardActive)
                    return default;
                var flags = _term.KittyKeyboardState.Flags;
                return new TerminalKeyboardProtocol(
                    KittyActive: true,
                    ReportsEventTypes: (flags & XTermLib.Input.KittyKeyboardFlags.ReportEventTypes) != 0,
                    ReportsAllKeys: (flags & XTermLib.Input.KittyKeyboardFlags.ReportAllKeysAsEscapeCodes) != 0);
            }
        }
    }

    public TerminalSession(
        TerminalOrigin origin,
        IPtyConnection pty,
        IUiDispatcher dispatcher,
        int columns,
        int rows,
        int scrollbackLines,
        string? startupNotice = null,
        bool kittyKeyboard = false)
    {
        Origin = origin ?? throw new ArgumentNullException(nameof(origin));
        _pty = pty ?? throw new ArgumentNullException(nameof(pty));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        StartupNotice = startupNotice;

        columns = Math.Clamp(columns, 1, 1000);
        rows = Math.Clamp(rows, 1, 1000);

        _term = new XTermLib.Terminal(new XTermLib.Options.TerminalOptions
        {
            Cols = columns,
            Rows = rows,
            Scrollback = Math.Max(0, scrollbackLines),
            TermName = "xterm-256color",
            KittyKeyboardEnabled = kittyKeyboard,
            // Writes always reach ClipboardWriteRequested; the service applies the user's
            // setting. Reads stay off: a program never sees the clipboard.
            ClipboardWriteEnabled = true,
            ClipboardReadEnabled = false,
            MaxClipboardBytes = MaxClipboardWriteBytes,
            // Historia neither renders nor handles these protocols, so the emulator must not
            // advertise or answer them.
            KittyGraphicsEnabled = false,
            ITerm2ImagesEnabled = false,
            KittyNotificationsEnabled = false,
            PointerShapesEnabled = false,
        });

        _coalesceTimer = _dispatcher.CreateTimer(TimeSpan.FromMilliseconds(16), OnCoalesceTick);

        _term.TitleChanged += OnEmulatorTitleChanged;
        _term.OscReceived += OnEmulatorOsc;
        _term.HyperlinkChanged += (_, e) => LastHyperlink = e.IsCleared ? null : e.Url;
        _term.DataReceived += OnEmulatorData;
        _term.ClipboardWriteRequested += OnEmulatorClipboardWrite;

        // Before subscribing: the pty may already hold the shell's first output, and attaching
        // releases it, so the notice must be in the buffer first to stay the first line.
        if (!string.IsNullOrEmpty(startupNotice))
            FeedString("\x1b[33m" + startupNotice + "\x1b[0m\r\n");

        State = TerminalSessionState.Running;

        _pty.OutputReceived += OnPtyOutput;
        _pty.Exited += OnPtyExited;
    }

    // ── input ───────────────────────────────────────────────────────────────────────────────

    public void Write(ReadOnlySpan<byte> input)
    {
        if (Volatile.Read(ref _disposed) != 0 || input.IsEmpty)
            return;
        _pty.Write(input);
    }

    public void Resize(int columns, int rows)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        columns = Math.Clamp(columns, 1, 1000);
        rows = Math.Clamp(rows, 1, 1000);

        // Resize runs from layout; a failing reflow or pty resize is logged, never thrown into it.
        var reflowed = false;
        try
        {
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) != 0)
                    return;
                _term.Resize(columns, rows);
                reflowed = true;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write(AppLogLevel.Warn, "terminal", $"Emulator resize to {columns}x{rows} failed: {ex.GetType().Name}: {ex.Message}", ex);
        }

        // The pty follows the emulator, never leads it: the child must not see a size the emulator lacks.
        if (reflowed && Volatile.Read(ref _disposed) == 0)
        {
            try
            {
                _pty.Resize(columns, rows);
            }
            catch (Exception ex) when (Volatile.Read(ref _disposed) != 0)
            {
                // Disposed while resizing; the pty is already gone.
                _ = ex;
            }
            catch (Exception ex)
            {
                AppLog.Write(AppLogLevel.Warn, "terminal", $"Pty resize to {columns}x{rows} failed: {ex.GetType().Name}: {ex.Message}", ex);
            }
        }

        MarkBufferDirty();
    }

    public void Kill() => _pty.Kill();

    /// <summary>
    /// Encodes a key under the Kitty keyboard protocol. False when the protocol is not active, and
    /// the caller encodes it the legacy way. True with empty <paramref name="bytes"/> means the
    /// key sends nothing (a bare modifier, or a release the flags do not report).
    /// </summary>
    public bool TryEncodeKittyKey(TerminalKeyInput input, TerminalKeyEventKind kind, out byte[] bytes)
    {
        var ev = new XTermLib.Options.KeyEvent
        {
            Key = input.Key ?? string.Empty,
            Code = input.Code ?? string.Empty,
            ShiftKey = input.Modifiers.HasFlag(TerminalModifiers.Shift),
            AltKey = input.Modifiers.HasFlag(TerminalModifiers.Alt),
            CtrlKey = input.Modifiers.HasFlag(TerminalModifiers.Control),
        };
        var type = kind switch
        {
            TerminalKeyEventKind.Repeat => XTermLib.Input.KittyKeyboardEventType.Repeat,
            TerminalKeyEventKind.Release => XTermLib.Input.KittyKeyboardEventType.Release,
            _ => XTermLib.Input.KittyKeyboardEventType.Press,
        };

        string? encoded;
        // One gate acquisition for the check and the encode, so a CSI < u that lands in between
        // cannot encode a key under flags that were already popped.
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) != 0 || !_term.KittyKeyboardActive)
            {
                bytes = [];
                return false;
            }
            encoded = _term.GenerateKittyKeyInput(ev, type);
        }

        bytes = string.IsNullOrEmpty(encoded) ? [] : Encoding.UTF8.GetBytes(encoded);
        return true;
    }

    // ── buffer read (under the gate) ────────────────────────────────────────────────────────

    /// <summary>Reads the emulator buffer under the session gate. Nothing XTerm.NET owns may
    /// escape <paramref name="read"/>.</summary>
    public T ReadBuffer<T>(Func<ITerminalBufferView, T> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        lock (_gate)
            return read(new BufferView(_term));
    }

    // ── pty → emulator pump ─────────────────────────────────────────────────────────────────

    private void OnPtyOutput(ReadOnlyMemory<byte> chunk)
    {
        if (Volatile.Read(ref _disposed) != 0 || chunk.IsEmpty)
            return;

        lock (_gate)
        {
            // Dispose tears the emulator down under this gate, so a chunk that was already in
            // flight when the session closed must not reach it.
            if (Volatile.Read(ref _disposed) != 0)
                return;
            _term.Write(chunk.Span);
        }
        MarkBufferDirty();
    }

    // The emulator drops a UTF-8 sequence the last pty chunk left unfinished (it emits U+FFFD)
    // when a string arrives, so text written here never completes a stale partial sequence.
    private void FeedString(string s)
    {
        lock (_gate)
        {
            // Dispose tears the emulator down under this gate, so text that was already in
            // flight when the session closed must not reach it.
            if (Volatile.Read(ref _disposed) != 0)
                return;
            _term.Write(s);
        }
        MarkBufferDirty();
    }

    private void OnEmulatorData(object? sender, XTermLib.Events.TerminalEvents.DataEventArgs e)
    {
        // The emulator's own replies (DSR, focus events, DECRQSS…) go straight back to the pty.
        if (!string.IsNullOrEmpty(e.Data))
            _pty.Write(Encoding.UTF8.GetBytes(e.Data));
    }

    // ── exit ────────────────────────────────────────────────────────────────────────────────

    private void OnPtyExited(int exitCode)
    {
        // The exit callback runs on the pty's own thread and can already be in flight when the
        // session is disposed; unsubscribing does not recall it.
        if (Volatile.Read(ref _disposed) != 0 || State is TerminalSessionState.Exited)
            return;

        ExitCode = exitCode;
        State = TerminalSessionState.Exited;
        FeedString($"\r\n\x1b[90m[process exited with code {exitCode}]\x1b[0m\r\n\x1b[90mPress Enter to start a new shell.\x1b[0m\r\n");
        RaiseOnDispatcher(StateChanged);
    }

    // ── OSC hardening ─────────────────────────────────────────────────────────────

    private void OnEmulatorTitleChanged(object? sender, XTermLib.Events.TerminalEvents.TitleChangeEventArgs e)
        => ApplyReportedTitle(e.Title);

    private void OnEmulatorOsc(object? sender, XTermLib.Events.TerminalEvents.OscReceivedEventArgs e)
    {
        switch (e.Code)
        {
            case 0 or 2:
                ApplyReportedTitle(e.Data);
                break;
        }
    }

    // Runs inside _term.Write, under the gate: filter here, raise on the dispatcher.
    private void OnEmulatorClipboardWrite(object? sender, XTermLib.Events.TerminalEvents.ClipboardWriteEventArgs e)
    {
        var plain = e.Formats.FirstOrDefault(f => f.MimeType == "text/plain");
        if (ClipboardTextFrom(plain.Data) is not { } text)
            return;

        var args = new TerminalClipboardWriteEventArgs(text);
        _dispatcher.Post(() =>
        {
            if (Volatile.Read(ref _disposed) == 0)
                ClipboardWriteRequested?.Invoke(this, args);
        });
    }

    /// <summary>The text a clipboard write carries, or null when it must be dropped: no data,
    /// over <see cref="MaxClipboardWriteBytes"/>, or nothing left once NULs are stripped. An empty
    /// write is xterm's "clear" idiom, which Historia never honours.</summary>
    internal static string? ClipboardTextFrom(byte[]? data)
    {
        if (data is null || data.Length == 0 || data.Length > MaxClipboardWriteBytes)
            return null;
        var text = Encoding.UTF8.GetString(data).Replace("\0", string.Empty, StringComparison.Ordinal);
        return text.Length == 0 ? null : text;
    }

    private void ApplyReportedTitle(string? raw)
    {
        var clean = SanitiseTitle(raw);
        if (clean is not null && LooksLikeExecutablePath(clean))
            clean = null;
        if (clean == ReportedTitle)
            return;
        ReportedTitle = clean;
        RaiseOnDispatcher(TitleChanged);
    }

    /// <summary>A Windows console reports the launching executable's full path as its initial
    /// OSC 0 title — e.g. <c>C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe</c>. On a
    /// pty resize (which every tab switch triggers) conhost re-emits it, so the tab caption
    /// flips from the origin name to that path. It is noise, not a caption: reject a rooted
    /// path that ends in <c>.exe</c> and keep falling back to <see cref="TerminalOrigin.Title"/>.</summary>
    internal static bool LooksLikeExecutablePath(string title)
        => title.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
           && (title.StartsWith('/') || title.StartsWith('\\')
               || (title.Length > 2 && char.IsAsciiLetter(title[0]) && title[1] == ':'));

    internal static string? SanitiseTitle(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return null;

        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsControl(ch))
                continue;
            sb.Append(ch);
            if (sb.Length >= 60)
                break;
        }
        var s = sb.ToString().Trim();
        return s.Length == 0 ? null : s;
    }

    // ── coalescing ──────────────────────────────────────────────────────────────────────────

    private void MarkBufferDirty()
    {
        lock (_gate)
        {
            _bufferDirtyPending = true;

            // Called once per pty chunk — under heavy output that's thousands of calls a
            // second. Only the first call in a coalescing window needs to reach the UI thread
            // at all; the timer itself is what actually gates the render, and re-posting once
            // per chunk while a window is already open just floods the dispatcher queue.
            if (_coalesceTimerArmed)
                return;
            _coalesceTimerArmed = true;
        }

        _dispatcher.Post(() =>
        {
            if (Volatile.Read(ref _disposed) == 0)
                _coalesceTimer.Start();
        });
    }

    private void OnCoalesceTick()
    {
        bool raise;
        lock (_gate)
        {
            raise = _bufferDirtyPending;
            _bufferDirtyPending = false;
            _coalesceTimerArmed = false;
        }
        _coalesceTimer.Stop();
        if (raise)
            BufferChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaiseOnDispatcher(EventHandler? handler)
    {
        if (handler is null)
            return;
        _dispatcher.Post(() => handler(this, EventArgs.Empty));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _pty.OutputReceived -= OnPtyOutput;
        _pty.Exited -= OnPtyExited;
        _term.TitleChanged -= OnEmulatorTitleChanged;
        _term.OscReceived -= OnEmulatorOsc;
        _term.DataReceived -= OnEmulatorData;
        _term.ClipboardWriteRequested -= OnEmulatorClipboardWrite;

        try { _coalesceTimer.Dispose(); } catch { /* best effort */ }
        try { _pty.Dispose(); } catch { /* best effort */ }
        lock (_gate)
        {
            try { _term.Dispose(); } catch { /* best effort */ }
        }
    }

    // ── buffer view ─────────────────────────────────────────────────────────────────────────

    private sealed class BufferView(XTermLib.Terminal term) : ITerminalBufferView
    {
        public int Columns => term.Cols;
        public int Rows => term.Rows;
        public int CursorColumn => term.Buffer.X;
        public int CursorRow => term.Buffer.YBase + term.Buffer.Y;
        public bool CursorVisible => term.CursorVisible;
        public bool IsAlternateBuffer => term.IsAlternateBufferActive;
        public int TotalLines => term.Buffer.Length;
        public int ViewportTop => term.Buffer.YDisp;

        public string GetLineText(int line)
        {
            if (line < 0 || line >= term.Buffer.Length)
                return string.Empty;
            return (term.GetLine(line) ?? string.Empty).TrimEnd();
        }

        public TerminalCellSnapshot GetCell(int line, int column)
        {
            if (line < 0 || line >= term.Buffer.Length)
                return Empty;

            var bl = term.Buffer.GetLine(line);
            if (bl is null || column < 0 || column >= bl.Length)
                return Empty;

            var cell = bl[column];
            var a = cell.Attributes;

            var flags = TerminalCellFlags.None;
            if (a.IsBold()) flags |= TerminalCellFlags.Bold;
            if (a.IsDim()) flags |= TerminalCellFlags.Dim;
            if (a.IsItalic()) flags |= TerminalCellFlags.Italic;
            if (a.IsUnderline()) flags |= TerminalCellFlags.Underline;
            if (a.IsBlink()) flags |= TerminalCellFlags.Blink;
            if (a.IsInverse()) flags |= TerminalCellFlags.Inverse;
            if (a.IsInvisible()) flags |= TerminalCellFlags.Hidden;
            if (a.IsStrikethrough()) flags |= TerminalCellFlags.Strikethrough;

            return new TerminalCellSnapshot(
                cell.CodePoint,
                cell.Content ?? string.Empty,
                cell.Width,
                TerminalColor.FromEmulator(a.GetFgColor(), a.GetFgColorMode(), isBackground: false),
                TerminalColor.FromEmulator(a.GetBgColor(), a.GetBgColorMode(), isBackground: true),
                flags);
        }

        private static readonly TerminalCellSnapshot Empty =
            new(32, " ", 1, TerminalColor.DefaultForeground, TerminalColor.DefaultBackground, TerminalCellFlags.None);
    }
}
