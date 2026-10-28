namespace Historia.Controls;

/// <summary>
/// The custom-drawn embedded terminal. A monospace grid of coloured cells — a simpler
/// drawing problem than <see cref="CommitGraphControl"/>: no lane assignment, no DAG layout, no
/// bezier routing. Windowed render, pooled brushes/pens, a per-codepoint glyph cache, all
/// miss-counted through the Core <see cref="RenderResourceCache{TKey,TValue}"/>.
/// </summary>
public sealed class TerminalControl : Control
{
    private const double DefaultFontSize = 13;
    private const int MinGrid = 1;
    private const int MaxGrid = 1000;

    private IThemeService _theme = default!;
    private IUiDispatcher _ui = default!;
    private ISettingsService _settings = default!;
    private IExternalLauncher _launcher = default!;
    private bool _wired;

    private TerminalSession? _session;
    private EffectiveTheme _themeMode = EffectiveTheme.Dark;

    private Typeface _typeface = new(FontFamily.Default);
    private IGlyphTypeface? _glyphTypeface;
    // Regular, bold, italic and bold-italic glyph typefaces by FaceIndex; a face the family lacks
    // (or would only synthesise) holds the regular one.
    private readonly IGlyphTypeface?[] _glyphFaces = new IGlyphTypeface?[4];
    private double _cellWidth = 8;
    private double _cellHeight = 16;
    private double _lastFontSize = DefaultFontSize;

    private int _cols = 80;
    private int _rows = 24;
    private double _scrollOffsetLines;               // 0 = bottom (follow); >0 = scrolled back
    private DispatcherTimer? _resizeDebounce;

    // selection (viewport-absolute line + column)
    private bool _selecting;
    private (int Line, int Col)? _selAnchor;
    private (int Line, int Col)? _selHead;

    private IUiTimer? _blinkTimer;
    private bool _blinkOn = true;
    private WindowBase? _hostWindow;

    // Keys whose press went through the Kitty encoder and are still held; a second KeyDown is a
    // repeat, and only these keys get a release, reported as the press was.
    private readonly Dictionary<int, KittyHeldKey> _kittyKeysDown = [];
    // The text of a key the Kitty encoder already sent, so its TextInput is not sent again.
    private string? _swallowTextInput;

    private readonly RenderResourceCache<uint, IBrush> _brushes;
    private readonly RenderResourceCache<PenKey, IPen> _pens;
    private readonly RenderResourceCache<int, GlyphMetric> _glyphs;

    // Drawn text runs by the cell a run starts at. A run is reused while its text, face and (for a
    // fallback layout, which bakes its brush) colour are unchanged. GlyphRun owns a
    // reference-counted platform object that only Dispose releases (DrawGlyphRun records its own
    // reference), so a replaced, dropped or unused run is disposed: entries not drawn in a frame
    // are swept at its end, and the cap is only a safety net.
    private const int MaxCachedRuns = 4096;
    private readonly Dictionary<RunKey, CachedRun> _runs = [];
    private long _runMisses;
    private long _frameNumber;
    private int _frameRunsTouched;

    // Per-control scratch for building one text run; grown on demand, never per run.
    private char[] _runText = new char[256];
    private int _runTextLength;
    private ushort[] _runIndices = new ushort[256];

    // Frame state read by the buffer callback, which is built once instead of per frame.
    private readonly Func<ITerminalBufferView, int> _renderBuffer;
    private readonly Action _invalidate;
    private readonly Action _pushResizeAction;
    private bool _resizePosted;
    private bool _tornDown;
    private DrawingContext? _frameDc;
    private uint _frameBgArgb;
    private uint _frameCursorArgb;
    private int _frameCols;
    private int _frameRows;
    private int _frameMetMax;
    private int _frameMetValue;
    private double _ascentPx;

    private readonly record struct PenKey(uint Argb, double Thickness, int Style);
    private readonly record struct GlyphMetric(ushort Index, double Advance, bool Found);
    private readonly record struct RunKey(int ScreenRow, int StartCol);

    // A run drawn from the terminal font's glyphs (Run) or, when a glyph is missing from it, shaped
    // with font fallback (Layout); exactly one is set.
    private sealed class CachedRun(GlyphRun? run, TextLayout? layout, char[] text, int face, uint fgArgb)
    {
        public GlyphRun? Run { get; } = run;
        public TextLayout? Layout { get; } = layout;
        public char[] Text { get; } = text;
        public int Face { get; } = face;
        public uint FgArgb { get; } = fgArgb;
        public long Frame { get; set; }

        public bool Matches(ReadOnlySpan<char> text, int face, uint fgArgb)
            => Face == face && (Layout is null || FgArgb == fgArgb) && text.SequenceEqual(Text);

        public void Draw(DrawingContext dc, IBrush brush, Point origin)
        {
            if (Run is { } run)
                dc.DrawGlyphRun(brush, run);
            else
                Layout!.Draw(dc, origin);
        }

        public void Dispose()
        {
            Run?.Dispose();
            Layout?.Dispose();
        }
    }

    public TerminalControl()
    {
        Focusable = true;
        ClipToBounds = true;

        _brushes = new RenderResourceCache<uint, IBrush>(
            1024, argb => new ImmutableSolidColorBrush(AvColor.FromUInt32(argb)));
        _pens = new RenderResourceCache<PenKey, IPen>(
            256, k => new ImmutablePen(new ImmutableSolidColorBrush(AvColor.FromUInt32(k.Argb)), k.Thickness));
        _glyphs = new RenderResourceCache<int, GlyphMetric>(4096, ResolveGlyph);

        _renderBuffer = RenderBuffer;
        _invalidate = InvalidateVisual;
        _pushResizeAction = PushResizeFromPost;
    }

    // ── lifecycle ───────────────────────────────────────────────────────────────────────────

    public void Attach(IServiceProvider services)
    {
        if (_wired)
            return;
        _tornDown = false;
        _theme = services.GetRequiredService<IThemeService>();
        _ui = services.GetRequiredService<IUiDispatcher>();
        _settings = services.GetRequiredService<ISettingsService>();
        _launcher = services.GetRequiredService<IExternalLauncher>();
        _themeMode = _theme.Effective;
        _theme.ThemeChanged += OnThemeChanged;
        _settings.Changed += OnSettingsChanged;
        _wired = true;
        RemeasureFont();
    }

    public TerminalSession? Session => _session;

    /// <summary>Raised when the user presses Enter in a session whose shell has exited — the
    /// dock restarts it ("no auto-respawn").</summary>
    public event EventHandler? RestartRequested;

    public void SetSession(TerminalSession? session)
    {
        if (ReferenceEquals(_session, session))
            return;

        if (_session is not null)
            _session.BufferChanged -= OnSessionBufferChanged;

        _session = session;
        _scrollOffsetLines = 0;
        _selAnchor = _selHead = null;
        _kittyKeysDown.Clear();
        _swallowTextInput = null;

        if (_session is not null)
        {
            _session.BufferChanged += OnSessionBufferChanged;
            PushResizeToSession();
        }

        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _blinkTimer ??= _ui?.CreateTimer(TimeSpan.FromMilliseconds(530), OnBlinkTick);

        _hostWindow = TopLevel.GetTopLevel(this) as WindowBase;
        if (_hostWindow is not null)
            _hostWindow.PropertyChanged += OnHostWindowPropertyChanged;
        UpdateBlink();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ReleaseHostWindow();
        UpdateBlink();
        ClearRuns();
    }

    private void ReleaseHostWindow()
    {
        if (_hostWindow is null)
            return;
        _hostWindow.PropertyChanged -= OnHostWindowPropertyChanged;
        _hostWindow = null;
    }

    // Activated is raised before the window restores focus and before IsActive is set, so the
    // blink follows the property, which changes last.
    private void OnHostWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == WindowBase.IsActiveProperty)
            UpdateBlink();
    }

    /// <summary>The cursor blinks only while the control has focus in an active window and the
    /// setting is on; otherwise the timer is stopped, so an idle terminal never repaints, and the
    /// cursor is left solid.</summary>
    private void UpdateBlink()
    {
        if (_blinkTimer is null)
            return;

        var blink = _settings?.Current.TerminalCursorBlink ?? true;
        if (IsFocused && (_hostWindow?.IsActive ?? true) && blink && this.GetVisualRoot() is not null)
        {
            _blinkTimer.Start();
            return;
        }

        _blinkTimer.Stop();
        if (!_blinkOn)
        {
            _blinkOn = true;
            InvalidateVisual();
        }
    }

    public void Teardown()
    {
        if (_session is not null)
            _session.BufferChanged -= OnSessionBufferChanged;
        if (_wired)
        {
            _theme.ThemeChanged -= OnThemeChanged;
            _settings.Changed -= OnSettingsChanged;
            _wired = false;
        }
        ReleaseHostWindow();
        _blinkTimer?.Dispose();
        _blinkTimer = null;
        _resizeDebounce?.Stop();
        _tornDown = true;
        ClearRuns();
    }

    private void OnThemeChanged(object? sender, ThemeChangedEventArgs e)
    {
        _themeMode = e.NewTheme;
        _brushes.Clear();
        _pens.Clear();
        InvalidateVisual();
    }

    // Settings changes can arrive on any thread, and font work touches the visual tree.
    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.OldSettings.TerminalFontFamily != e.NewSettings.TerminalFontFamily
            || e.OldSettings.TerminalFontSize != e.NewSettings.TerminalFontSize)
        {
            if (_ui.CheckAccess())
                ApplyFontChange();
            else
                _ui.Post(ApplyFontChange);
        }

        if (e.OldSettings.TerminalCursorBlink != e.NewSettings.TerminalCursorBlink)
        {
            if (_ui.CheckAccess())
                UpdateBlink();
            else
                _ui.Post(UpdateBlink);
        }
    }

    internal (double Width, double Height) CellSize => (_cellWidth, _cellHeight);

    private void ApplyFontChange()
    {
        if (!_wired)
            return;

        RemeasureFont();
        RecomputeGrid();
        PushResizeToSession();
        InvalidateVisual();
    }

    private void OnSessionBufferChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(_invalidate, DispatcherPriority.Render);

    private void OnBlinkTick()
    {
        _blinkOn = !_blinkOn;
        InvalidateVisual();
    }

    // ── font metrics ──────────────────────────────────────────────────────────────

    private void RemeasureFont()
    {
        var family = _settings?.Current.TerminalFontFamily;
        var size = _settings?.Current.TerminalFontSize ?? DefaultFontSize;
        if (size < 6) size = DefaultFontSize;

        _lastFontSize = size;

        var ff = string.IsNullOrWhiteSpace(family)
            ? new FontFamily(PlatformMonospaceStack())
            : new FontFamily(family);
        _typeface = new Typeface(ff);

        _glyphTypeface = null;
        Array.Clear(_glyphFaces);
        try
        {
            if (FontManager.Current.TryGetGlyphTypeface(_typeface, out var gtf))
                _glyphTypeface = gtf;
        }
        catch { _glyphTypeface = null; }

        _glyphFaces[0] = _glyphTypeface;
        for (var face = 1; face < _glyphFaces.Length; face++)
            _glyphFaces[face] = ResolveStyledFace(face) ?? _glyphTypeface;

        // Advance width of 'M', line height from the typeface metrics.
        var probe = new FormattedText("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _typeface, size, Brushes.White);
        _cellWidth = Math.Max(1, Math.Round(probe.WidthIncludingTrailingWhitespace));
        _cellHeight = Math.Max(1, Math.Ceiling(probe.Height));

        _ascentPx = 0;
        if (_glyphTypeface is { } measured)
        {
            var em = measured.Metrics.DesignEmHeight == 0 ? 1000 : measured.Metrics.DesignEmHeight;
            _ascentPx = -measured.Metrics.Ascent * (size / em);
        }

        _glyphs.Clear();
        ClearRuns();
    }

    // The family's real bold/italic face, or null when it has none: a face the platform would
    // only synthesise (FontSimulations) is not used, so bold is never smeared regular.
    private IGlyphTypeface? ResolveStyledFace(int face)
    {
        try
        {
            if (FontManager.Current.TryGetGlyphTypeface(FaceTypeface(face), out var gtf)
                && gtf.FontSimulations == FontSimulations.None)
            {
                return gtf;
            }
        }
        catch { /* the regular face stands in */ }
        return null;
    }

    private void ClearRuns()
    {
        foreach (var cached in _runs.Values)
            cached.Dispose();
        _runs.Clear();
    }

    // Disposes every entry the frame that just ended did not draw (a row scrolled away, text
    // that changed shape or colour, a smaller grid).
    private void SweepUnusedRuns()
    {
        if (_runs.Count == _frameRunsTouched)
            return;
        foreach (var (key, cached) in _runs)
        {
            if (cached.Frame == _frameNumber)
                continue;
            cached.Dispose();
            _runs.Remove(key);
        }
    }

    private static string PlatformMonospaceStack()
    {
        if (OperatingSystem.IsWindows()) return "Cascadia Mono, Consolas, Courier New";
        if (OperatingSystem.IsMacOS()) return "SF Mono, Menlo, Monaco";
        return "DejaVu Sans Mono, Liberation Mono, monospace";
    }

    private const int GlyphFaceShift = 22;

    private static int GlyphKey(int codepoint, int face) => codepoint | (face << GlyphFaceShift);

    private GlyphMetric ResolveGlyph(int key)
    {
        var codepoint = key & ((1 << GlyphFaceShift) - 1);
        var gtf = _glyphFaces[key >> GlyphFaceShift];
        if (gtf is null)
            return new GlyphMetric(0, _cellWidth, false);
        try
        {
            if (gtf.TryGetGlyph((uint)codepoint, out var gi))
            {
                var em = gtf.Metrics.DesignEmHeight == 0 ? 1000 : gtf.Metrics.DesignEmHeight;
                var adv = gtf.GetGlyphAdvance(gi) * (_lastFontSize / em);
                return new GlyphMetric(gi, adv, true);
            }
        }
        catch { /* fall through */ }
        return new GlyphMetric(0, _cellWidth, false);
    }

    // ── sizing ──────────────────────────────────────────────────────────────────────────────

    internal static bool HasUsableSize(Size bounds, double cellWidth, double cellHeight)
        => cellWidth > 0 && cellHeight > 0 && bounds.Width >= cellWidth && bounds.Height >= cellHeight;

    protected override Size MeasureOverride(Size availableSize) => availableSize;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (!HasUsableSize(e.PreviousSize, _cellWidth, _cellHeight)
            && HasUsableSize(e.NewSize, _cellWidth, _cellHeight))
        {
            // Layout is still running: the session resize waits until it has finished.
            _resizeDebounce?.Stop();
            RecomputeGrid();
            PostPushResize();
            return;
        }

        RecomputeGrid();
        DebouncePushResize();
    }

    private void RecomputeGrid()
    {
        if (!HasUsableSize(Bounds.Size, _cellWidth, _cellHeight))
            return;
        _cols = Math.Clamp((int)(Bounds.Width / _cellWidth), MinGrid, MaxGrid);
        _rows = Math.Clamp((int)(Bounds.Height / _cellHeight), MinGrid, MaxGrid);
    }

    private void DebouncePushResize()
    {
        _resizeDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        _resizeDebounce.Stop();
        _resizeDebounce.Tick -= OnResizeDebounceTick;
        _resizeDebounce.Tick += OnResizeDebounceTick;
        _resizeDebounce.Start();
    }

    private void OnResizeDebounceTick(object? sender, EventArgs e)
    {
        _resizeDebounce!.Stop();
        PushResizeToSession();
    }

    private void PostPushResize()
    {
        if (_resizePosted)
            return;
        _resizePosted = true;
        Dispatcher.UIThread.Post(_pushResizeAction, DispatcherPriority.Background);
    }

    private void PushResizeFromPost()
    {
        _resizePosted = false;
        if (_tornDown)
            return;
        PushResizeToSession();
    }

    private void PushResizeToSession()
    {
        RecomputeGrid();

        // Trap: an unlaid-out dock has 0×0 bounds; resizing to that shrinks ConPTY to 1×1,
        // and since ConPTY keeps no scrollback, a prompt the shell has already printed is
        // lost. The session keeps its spawn size until the control has real bounds, and
        // OnSizeChanged delivers them.
        // TerminalSession.Resize logs its own failures and never throws.
        if (HasUsableSize(Bounds.Size, _cellWidth, _cellHeight))
            _session?.Resize(_cols, _rows);

        InvalidateVisual();
    }

    // ── render ────────────────────────────────────────────────────────────────────

    public override void Render(DrawingContext dc)
    {
        if (_tornDown)
            return;

        var palette = HistoriaPalette.For(_themeMode);
        _frameBgArgb = ArgbOf(palette["term-bg"]);
        dc.FillRectangle(Brush(_frameBgArgb), new Rect(Bounds.Size));

        if (_session is null)
            return;

        _frameDc = dc;
        _frameNumber++;
        _frameRunsTouched = 0;
        _frameCursorArgb = ArgbOf(palette["term-cursor"]);
        (_frameCols, _frameRows) = (_cols, _rows);
        (_frameMetMax, _frameMetValue) = (0, 0);

        try
        {
            _session.ReadBuffer(_renderBuffer);
            SweepUnusedRuns();
        }
        finally
        {
            _frameDc = null;
        }

        var metrics = new ScrollMetrics(_frameMetMax, _frameMetValue, _frameRows);
        if (metrics != _lastMetrics)
        {
            _lastMetrics = metrics;
            Dispatcher.UIThread.Post(() => ScrollMetricsChanged?.Invoke(this, EventArgs.Empty));
        }
    }

    // Runs under the session gate, with the frame's state in the _frame* fields.
    private int RenderBuffer(ITerminalBufferView view)
    {
        var dc = _frameDc!;
        var (cols, rows) = (_frameCols, _frameRows);
        var total = view.TotalLines;
        // The emulator's own viewport top (YDisp) is the authoritative bottom-anchor: it
        // follows the emulator through scrolls and reflows instead of being re-derived from
        // the line count.
        var bottomTop = Math.Clamp(view.ViewportTop, 0, Math.Max(0, total - 1));
        var top = Math.Clamp(bottomTop - (int)Math.Round(_scrollOffsetLines), 0, bottomTop);
        (_frameMetMax, _frameMetValue) = (bottomTop, top);

        for (var screenRow = 0; screenRow < rows; screenRow++)
        {
            var line = top + screenRow;
            if (line >= total)
                break;
            DrawRow(dc, view, line, screenRow, cols);
        }

        DrawCursor(dc, view, top, rows, cols);
        return 0;
    }

    // ── scroll metrics (drives the dock's vertical ScrollBar) ────────────────────────────────

    /// <summary>Scrollback geometry in line units: <paramref name="Max"/> lines are hidden above
    /// the viewport at the top-most scroll, <paramref name="Value"/> is how many are hidden right
    /// now (0 = following the bottom), <paramref name="Viewport"/> is the visible row count.</summary>
    public readonly record struct ScrollMetrics(int Max, int Value, int Viewport);

    private ScrollMetrics _lastMetrics = new(-1, -1, -1);

    /// <summary>Latest metrics from the last render — no buffer lock needed.</summary>
    public ScrollMetrics CurrentScrollMetrics => _lastMetrics;

    /// <summary>Raised (on the dispatcher) after a render whose scroll metrics changed.</summary>
    public event EventHandler? ScrollMetricsChanged;

    /// <summary>Scroll so <paramref name="value"/> lines are hidden above the viewport
    /// (<c>value == Max</c> → top, <c>0</c> → follow the bottom). Driven by the dock ScrollBar.</summary>
    public void SetScrollValue(int value)
    {
        var max = _lastMetrics.Max;
        if (max <= 0)
            return;
        _scrollOffsetLines = Math.Clamp(max - value, 0, max);
        InvalidateVisual();
    }

    private void DrawRow(DrawingContext dc, ITerminalBufferView view, int line, int screenRow, int cols)
    {
        var y = screenRow * _cellHeight;
        var defaultBg = _frameBgArgb;

        // Pass A — backgrounds: merge adjacent equal-bg cells into one rect.
        var runStart = 0;
        uint runBg = 0;
        var haveRun = false;
        for (var col = 0; col <= cols; col++)
        {
            uint bg = col < cols ? ResolveBg(view.GetCell(line, col)) : defaultBg + 1; // sentinel to flush
            if (haveRun && bg == runBg)
                continue;
            if (haveRun && runBg != defaultBg)
            {
                dc.FillRectangle(Brush(runBg),
                    new Rect(runStart * _cellWidth, y, (col - runStart) * _cellWidth, _cellHeight));
            }
            runStart = col;
            runBg = bg;
            haveRun = true;
        }

        // Pass B — text runs: merge adjacent equal (fg, flags).
        var b = 0;
        while (b < cols)
        {
            var cell = view.GetCell(line, b);
            if (cell.Width == 0 || cell.CodePoint == 0 || cell.CodePoint == 32)
            {
                b++;
                continue;
            }

            var (fg, flags) = ResolveFg(cell);
            var start = b;
            _runTextLength = 0;
            while (b < cols)
            {
                var c = view.GetCell(line, b);
                if (c.Width == 0) { b++; continue; }
                if (c.CodePoint is 0 or 32) break;
                var (cfg, cflags) = ResolveFg(c);
                if (cfg != fg || cflags != flags) break;
                AppendRunText(c);
                b += Math.Max(1, c.Width);
            }
            if (b == start) { b++; continue; }

            DrawTextRun(dc, screenRow, start, y, fg, flags);
        }
    }

    // Appends the cell's text to the run buffer: its grapheme content when it has any, else its
    // code point encoded in place (U+FFFD for a value that is not a scalar).
    private void AppendRunText(in TerminalCellSnapshot cell)
    {
        var content = cell.Content;
        if (content.Length > 0)
        {
            EnsureRunTextCapacity(content.Length);
            content.CopyTo(0, _runText, _runTextLength, content.Length);
            _runTextLength += content.Length;
            return;
        }

        EnsureRunTextCapacity(2);
        if (Rune.TryCreate(cell.CodePoint, out var rune))
            _runTextLength += rune.EncodeToUtf16(_runText.AsSpan(_runTextLength));
        else
            _runText[_runTextLength++] = '�';
    }

    private void EnsureRunTextCapacity(int extra)
    {
        if (_runTextLength + extra > _runText.Length)
            Array.Resize(ref _runText, Math.Max(_runText.Length * 2, _runTextLength + extra));
    }

    private void DrawTextRun(DrawingContext dc, int screenRow, int startCol, double y, uint fgArgb, TerminalCellFlags flags)
    {
        var brush = Brush(fgArgb);
        var text = _runText.AsSpan(0, _runTextLength);
        var key = new RunKey(screenRow, startCol);
        var face = FaceIndex(flags);

        if (!_runs.TryGetValue(key, out var run) || !run.Matches(text, face, fgArgb))
            run = CreateRun(key, text, face, fgArgb, brush, y);

        if (run.Frame != _frameNumber)
        {
            run.Frame = _frameNumber;
            _frameRunsTouched++;
        }
        run.Draw(dc, brush, new Point(startCol * _cellWidth, y));

        DrawTextDecorations(dc, text.Length, startCol, y, fgArgb, flags);
    }

    // Shapes the run, replaces any run cached for the cell and disposes it. The terminal font's
    // glyphs draw it when it has them all; otherwise (missing glyph, no glyph typeface) it is
    // shaped with font fallback.
    private CachedRun CreateRun(RunKey key, ReadOnlySpan<char> text, int face, uint fgArgb, IBrush brush, double y)
    {
        _runMisses++;
        // The cached run keeps its character memory, so it gets a copy.
        var chars = text.ToArray();
        var cached = TryShapeGlyphRun(key, chars, face, y + _ascentPx) is { } glyphRun
            ? new CachedRun(glyphRun, null, chars, face, fgArgb)
            : new CachedRun(null, new TextLayout(new string(chars), FaceTypeface(face), _lastFontSize, brush), chars, face, fgArgb);

        DropRun(key);
        if (_runs.Count >= RunCacheCap)
            ClearRuns();
        _runs[key] = cached;
        return cached;
    }

    // Safety net only: positions bound the cache to one entry per cell, and the end-of-frame
    // sweep keeps stale ones out.
    private int RunCacheCap => Math.Max(MaxCachedRuns, _frameRows * _frameCols);

    // Null when a glyph is missing from the face's typeface.
    private GlyphRun? TryShapeGlyphRun(RunKey key, char[] chars, int face, double baseline)
    {
        var gtf = _glyphFaces[face];
        if (gtf is null)
            return null;

        if (_runIndices.Length < chars.Length)
            _runIndices = new ushort[Math.Max(_runIndices.Length * 2, chars.Length)];

        var count = 0;
        foreach (var rune in new ReadOnlySpan<char>(chars).EnumerateRunes())
        {
            var m = _glyphs.Get(GlyphKey(rune.Value, face));
            if (!m.Found)
                return null;
            _runIndices[count++] = m.Index;
        }

        return new GlyphRun(gtf, _lastFontSize, chars.AsMemory(), new ArraySegment<ushort>(_runIndices, 0, count),
            baselineOrigin: new Point(key.StartCol * _cellWidth, baseline));
    }

    private void DropRun(RunKey key)
    {
        if (_runs.Remove(key, out var old))
            old.Dispose();
    }

    private void DrawTextDecorations(DrawingContext dc, int cellCount, int startCol, double y, uint fgArgb, TerminalCellFlags flags)
    {
        var x0 = startCol * _cellWidth;
        var x1 = x0 + cellCount * _cellWidth;
        if (flags.HasFlag(TerminalCellFlags.Underline))
        {
            var uy = y + _cellHeight - 1.5;
            dc.DrawLine(Pen(fgArgb, 1, 0), new Point(x0, uy), new Point(x1, uy));
        }
        if (flags.HasFlag(TerminalCellFlags.Strikethrough))
        {
            var sy = y + _cellHeight / 2;
            dc.DrawLine(Pen(fgArgb, 1, 1), new Point(x0, sy), new Point(x1, sy));
        }
    }

    private void DrawCursor(DrawingContext dc, ITerminalBufferView view, int top, int rows, int cols)
    {
        if (!view.CursorVisible)
            return;
        var screenRow = view.CursorRow - top;
        if (screenRow < 0 || screenRow >= rows)
            return;
        var col = Math.Clamp(view.CursorColumn, 0, cols - 1);
        var rect = new Rect(col * _cellWidth, screenRow * _cellHeight, _cellWidth, _cellHeight);
        var cursorBrush = Brush(_frameCursorArgb);

        var style = _settings?.Current.TerminalCursorStyle ?? TerminalCursorStyle.Block;
        var focused = IsFocused;

        if (!focused)
        {
            dc.DrawRectangle(null, Pen(_frameCursorArgb, 1, UnfocusedCursorPenStyle), rect);
            return;
        }
        if (!_blinkOn)
            return;

        switch (style)
        {
            case TerminalCursorStyle.Underline:
                dc.FillRectangle(cursorBrush, new Rect(rect.X, rect.Bottom - 2, rect.Width, 2));
                break;
            case TerminalCursorStyle.Bar:
                dc.FillRectangle(cursorBrush, new Rect(rect.X, rect.Y, 2, rect.Height));
                break;
            default:
                dc.FillRectangle(cursorBrush, rect);
                break;
        }
    }

    // ── colour resolution ───────────────────────────────────────────────────────────────────

    private uint ResolveBg(TerminalCellSnapshot cell)
    {
        var (bg, _) = ApplyInverse(cell);
        return bg;
    }

    private (uint Fg, TerminalCellFlags Flags) ResolveFg(TerminalCellSnapshot cell)
    {
        var (_, fg) = ApplyInverse(cell);
        var flags = cell.Flags;
        if (flags.HasFlag(TerminalCellFlags.Hidden))
            fg = ResolveBg(cell);
        if (flags.HasFlag(TerminalCellFlags.Dim))
            fg = Blend(fg, ResolveBg(cell), 0.4);
        return (fg, flags);
    }

    /// <summary>Resolves fg/bg, swapping them first when the cell is inverse so
    /// inverse cells still merge into runs.</summary>
    private (uint Bg, uint Fg) ApplyInverse(TerminalCellSnapshot cell)
    {
        var fg = ArgbOf(TerminalColorResolver.Resolve(cell.Foreground, _themeMode));
        var bg = ArgbOf(TerminalColorResolver.Resolve(cell.Background, _themeMode));
        return cell.Flags.HasFlag(TerminalCellFlags.Inverse) ? (fg, bg) : (bg, fg);
    }

    // 0 regular, 1 bold, 2 italic, 3 bold-italic.
    private static int FaceIndex(TerminalCellFlags flags)
        => (flags.HasFlag(TerminalCellFlags.Bold) ? 1 : 0) | (flags.HasFlag(TerminalCellFlags.Italic) ? 2 : 0);

    private Typeface FaceTypeface(int face)
    {
        var weight = (face & 1) != 0 ? FontWeight.Bold : FontWeight.Normal;
        var style = (face & 2) != 0 ? FontStyle.Italic : FontStyle.Normal;
        return new Typeface(_typeface.FontFamily, style, weight);
    }

    // ── input ─────────────────────────────────────────────────────────────────────

    protected override void OnGotFocus(Avalonia.Input.GotFocusEventArgs e)
    {
        base.OnGotFocus(e);
        UpdateBlink();
        InvalidateVisual();
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        // Keys released elsewhere never reach OnKeyUp; no synthetic releases are sent.
        _kittyKeysDown.Clear();
        _swallowTextInput = null;
        UpdateBlink();
        InvalidateVisual();
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (_swallowTextInput is not null && _swallowTextInput == e.Text)
        {
            _swallowTextInput = null;
            e.Handled = true;
            return;
        }
        if (_session is null || string.IsNullOrEmpty(e.Text))
            return;
        _session.Write(System.Text.Encoding.UTF8.GetBytes(e.Text));
        _scrollOffsetLines = 0;
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        _swallowTextInput = null;
        if (_session is null)
        {
            base.OnKeyDown(e);
            return;
        }

        if (_session.State is TerminalSessionState.Exited)
        {
            if (e.Key is Key.Enter or Key.Return)
            {
                RestartRequested?.Invoke(this, EventArgs.Empty);
                e.Handled = true;
            }
            return;
        }

        var mods = ToModifiers(e.KeyModifiers);

        // Terminal-scoped chords the control does NOT claim.
        if (mods == TerminalModifiers.Control && e.Key == Key.OemTilde) { base.OnKeyDown(e); return; }
        if (mods.HasFlag(TerminalModifiers.Control) && mods.HasFlag(TerminalModifiers.Shift)
            && e.Key is Key.C or Key.V or Key.F)
        {
            if (e.Key == Key.C) { CopySelection(); e.Handled = true; }
            else if (e.Key == Key.V) { _ = PasteAsync(); e.Handled = true; }
            return;
        }

        if (TrySendKittyKey(e, mods))
            return;

        var modes = ReadModes();

        if (TryMapKey(e.Key, out var tkey))
        {
            var bytes = KeyEncoder.Encode(tkey, mods, modes);
            if (!bytes.IsEmpty)
            {
                _session.Write(bytes);
                _scrollOffsetLines = 0;
                e.Handled = true;
                return;
            }
        }

        // Ctrl+letter / Ctrl+punctuation control codes.
        if (mods.HasFlag(TerminalModifiers.Control) && !mods.HasFlag(TerminalModifiers.Alt))
        {
            var ch = ControlCharFor(e.Key);
            if (ch is not null && KeyEncoder.EncodeControlChar(ch.Value, TerminalModifiers.Control) is { } code)
            {
                _session.Write(new[] { code });
                _scrollOffsetLines = 0;
                e.Handled = true;
                return;
            }
        }

        // Alt+letter / Alt+digit / Alt+punctuation (xterm metaSendsEscape).
        if (mods.HasFlag(TerminalModifiers.Alt) && !mods.HasFlag(TerminalModifiers.Control))
        {
            var ch = MetaCharFor(e.Key, mods.HasFlag(TerminalModifiers.Shift));
            if (ch is not null && KeyEncoder.EncodeMeta(ch.Value, mods) is { } metaBytes)
            {
                _session.Write(metaBytes);
                _scrollOffsetLines = 0;
                e.Handled = true;
                return;
            }
        }

        // Only keys that cannot produce text (exactly one of Ctrl or Alt held) are claimed
        // here; plain keys, Shift-only keys, and Ctrl+Alt (AltGr) must stay unhandled so the
        // platform can deliver character messages to OnTextInput.
        if (e.Key is not (Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin))
        {
            if (KeyEncoder.ClaimsUnencodedKey(mods))
                e.Handled = true;
        }
    }

    /// <summary>
    /// Sends the key through the Kitty encoder when the child has turned the protocol on. A key
    /// that types text stays on <see cref="OnTextInput"/> (so IME, AltGr and dead keys work)
    /// unless the child asked for every key as an escape code.
    /// </summary>
    private bool TrySendKittyKey(KeyEventArgs e, TerminalModifiers mods)
    {
        var protocol = _session!.KeyboardProtocol;
        if (!protocol.KittyActive)
            return false;

        var textKey = IsTextKey(e.KeyModifiers, e.KeySymbol);
        if (textKey && !protocol.ReportsAllKeys)
            return false;

        var key = ToBrowserKey(e.Key, e.KeySymbol);
        if (key.Length == 0)
            return false;

        // Windows reports AltGr as Ctrl+Alt; the text it typed already reflects it, so neither is
        // a modifier of this key.
        var stripped = textKey && mods.HasFlag(TerminalModifiers.Control) && mods.HasFlag(TerminalModifiers.Alt)
            ? TerminalModifiers.Control | TerminalModifiers.Alt
            : TerminalModifiers.None;

        var id = KeyId(e);
        var kind = _kittyKeysDown.ContainsKey(id) ? TerminalKeyEventKind.Repeat : TerminalKeyEventKind.Press;
        if (!_session.TryEncodeKittyKey(new TerminalKeyInput(key, ToBrowserCode(e.PhysicalKey), mods & ~stripped), kind, out var bytes))
            return false;

        if (bytes.Length > 0)
        {
            _session.Write(bytes);
            _scrollOffsetLines = 0;
        }
        _kittyKeysDown[id] = new KittyHeldKey(key, stripped);
        if (textKey)
            _swallowTextInput = e.KeySymbol;
        e.Handled = true;
        return true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (_session is null || !_kittyKeysDown.Remove(KeyId(e), out var held))
            return;
        e.Handled = true;

        if (!_session.KeyboardProtocol.ReportsEventTypes)
            return;
        // The press's key name: KeyUp may carry no KeySymbol, which would name an AltGr key by
        // its base character.
        var input = new TerminalKeyInput(held.Key, ToBrowserCode(e.PhysicalKey), ToModifiers(e.KeyModifiers) & ~held.Stripped);
        if (_session.TryEncodeKittyKey(input, TerminalKeyEventKind.Release, out var bytes) && bytes.Length > 0)
            _session.Write(bytes);
    }

    private static int KeyId(KeyEventArgs e)
        => e.PhysicalKey != PhysicalKey.None ? (int)e.PhysicalKey : -1 - (int)e.Key;

    private readonly record struct KittyHeldKey(string Key, TerminalModifiers Stripped);

    private TerminalModes ReadModes() => _session?.CurrentModes ?? default;

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_session is null)
            return;

        var isAlt = _session.ReadBuffer(v => v.IsAlternateBuffer);
        if (isAlt)
        {
            // Legacy arrows are right under Kitty too: the protocol keeps them unless flag 8 is set.
            var key = e.Delta.Y > 0 ? TerminalKey.Up : TerminalKey.Down;
            for (var i = 0; i < 3; i++)
                _session.Write(KeyEncoder.Encode(key, TerminalModifiers.None, ReadModes()));
        }
        else
        {
            _scrollOffsetLines = Math.Max(0, _scrollOffsetLines + (e.Delta.Y > 0 ? 3 : -3));
            InvalidateVisual();
        }
        e.Handled = true;
    }

    // ── selection & clipboard ─────────────────────────────────────────────────────

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (_session is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        // Ctrl+Click opens the active OSC 8 hyperlink. IExternalLauncher itself
        // enforces absolute http(s) only, so a non-web scheme simply does not open.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) && _session.LastHyperlink is { } url)
        {
            _launcher.OpenUrl(url);
            e.Handled = true;
            return;
        }

        var cell = PointToCell(e.GetPosition(this));
        _selecting = true;
        _selAnchor = cell;
        _selHead = cell;
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        // Phishing mitigation: surface the real OSC 8 target rather than the styled
        // link text. Per-cell hyperlink hit-testing needs buffer-view support it doesn't have
        // yet — the active-hyperlink target is shown while one is in effect (named gap).
        var link = _session?.LastHyperlink;
        ToolTip.SetTip(this, link);

        if (!_selecting)
            return;
        _selHead = PointToCell(e.GetPosition(this));
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_selecting)
        {
            _selecting = false;
            e.Pointer.Capture(null);
        }
    }

    private (int Line, int Col) PointToCell(Point p)
    {
        var col = Math.Clamp((int)(p.X / _cellWidth), 0, Math.Max(0, _cols - 1));
        var screenRow = Math.Clamp((int)(p.Y / _cellHeight), 0, Math.Max(0, _rows - 1));
        var line = screenRow; // absolute mapping is resolved at copy time against the buffer view
        return (line, col);
    }

    private void CopySelection()
    {
        if (_session is null || _selAnchor is null || _selHead is null)
            return;

        var text = _session.ReadBuffer(view =>
        {
            var bottomTop = Math.Clamp(view.ViewportTop, 0, Math.Max(0, view.TotalLines - 1));
            var top = Math.Clamp(bottomTop - (int)Math.Round(_scrollOffsetLines), 0, bottomTop);

            var (a, b) = Ordered(_selAnchor.Value, _selHead.Value);
            var sb = new System.Text.StringBuilder();
            for (var sr = a.Line; sr <= b.Line; sr++)
            {
                var absolute = top + sr;
                var lineText = view.GetLineText(absolute);
                var from = sr == a.Line ? a.Col : 0;
                var to = sr == b.Line ? b.Col : lineText.Length;
                from = Math.Clamp(from, 0, lineText.Length);
                to = Math.Clamp(to, from, lineText.Length);
                sb.Append(lineText.AsSpan(from, to - from).ToString().TrimEnd());
                if (sr != b.Line)
                    sb.Append('\n');
            }
            return sb.ToString();
        });

        if (!string.IsNullOrEmpty(text))
            TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
    }

    private async Task PasteAsync()
    {
        if (_session is null)
            return;
        var clip = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clip is null)
            return;
#pragma warning disable CS0618 // GetTextAsync: the replacement extension needs IAsyncDataTransfer, not available on this Avalonia
        var text = await clip.GetTextAsync();
#pragma warning restore CS0618
        if (string.IsNullOrEmpty(text))
            return;

        var bracketed = _session.ReadBuffer(v => v.IsAlternateBuffer) || BracketedPasteEnabled();
        if (bracketed)
        {
            _session.Write("\x1b[200~"u8);
            _session.Write(System.Text.Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\r")));
            _session.Write("\x1b[201~"u8);
        }
        else
        {
            _session.Write(System.Text.Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\r")));
        }
        _scrollOffsetLines = 0;
    }

    private bool BracketedPasteEnabled() => _session?.BracketedPasteMode ?? false;

    private static ((int Line, int Col) A, (int Line, int Col) B) Ordered((int Line, int Col) x, (int Line, int Col) y)
        => (x.Line < y.Line || (x.Line == y.Line && x.Col <= y.Col)) ? (x, y) : (y, x);

    // ── key mapping ─────────────────────────────────────────────────────────────────────────

    private static TerminalModifiers ToModifiers(KeyModifiers m)
    {
        var r = TerminalModifiers.None;
        if (m.HasFlag(KeyModifiers.Shift)) r |= TerminalModifiers.Shift;
        if (m.HasFlag(KeyModifiers.Alt)) r |= TerminalModifiers.Alt;
        if (m.HasFlag(KeyModifiers.Control)) r |= TerminalModifiers.Control;
        return r;
    }

    private static bool TryMapKey(Key k, out TerminalKey t)
    {
        t = k switch
        {
            Key.Enter => TerminalKey.Enter,
            Key.Tab => TerminalKey.Tab,
            Key.Back => TerminalKey.Backspace,
            Key.Escape => TerminalKey.Escape,
            Key.Delete => TerminalKey.Delete,
            Key.Insert => TerminalKey.Insert,
            Key.Up => TerminalKey.Up,
            Key.Down => TerminalKey.Down,
            Key.Left => TerminalKey.Left,
            Key.Right => TerminalKey.Right,
            Key.Home => TerminalKey.Home,
            Key.End => TerminalKey.End,
            Key.PageUp => TerminalKey.PageUp,
            Key.PageDown => TerminalKey.PageDown,
            Key.F1 => TerminalKey.F1,
            Key.F2 => TerminalKey.F2,
            Key.F3 => TerminalKey.F3,
            Key.F4 => TerminalKey.F4,
            Key.F5 => TerminalKey.F5,
            Key.F6 => TerminalKey.F6,
            Key.F7 => TerminalKey.F7,
            Key.F8 => TerminalKey.F8,
            Key.F9 => TerminalKey.F9,
            Key.F10 => TerminalKey.F10,
            Key.F11 => TerminalKey.F11,
            Key.F12 => TerminalKey.F12,
            _ => (TerminalKey)(-1),
        };
        return (int)t >= 0;
    }

    internal static char? ControlCharFor(Key k)
    {
        if (k is >= Key.A and <= Key.Z)
            return (char)('a' + (k - Key.A));
        return k switch
        {
            Key.Space => ' ',
            Key.OemOpenBrackets => '[',
            Key.OemCloseBrackets => ']',
            Key.OemBackslash or Key.OemPipe => '\\',
            Key.OemMinus => '_',
            _ => null,
        };
    }

    /// <summary>
    /// Maps a key to the character to be prefixed with ESC under Alt (xterm metaSendsEscape).
    /// Letters respect Shift. Unshifted digits and period, comma, minus produce their ASCII character.
    /// Shifted symbols depend on the active keyboard layout and Windows generates them via WM_SYSCHAR
    /// which bypasses TextInput, so they are deliberately omitted.
    /// </summary>
    internal static char? MetaCharFor(Key k, bool shift)
    {
        if (k is >= Key.A and <= Key.Z)
            return shift ? (char)('A' + (k - Key.A)) : (char)('a' + (k - Key.A));

        if (!shift)
        {
            if (k is >= Key.D0 and <= Key.D9)
                return (char)('0' + (k - Key.D0));
            if (k is >= Key.NumPad0 and <= Key.NumPad9)
                return (char)('0' + (k - Key.NumPad0));

            return k switch
            {
                Key.OemPeriod => '.',
                Key.OemComma => ',',
                Key.OemMinus => '-',
                _ => null,
            };
        }

        return null;
    }

    /// <summary>
    /// Decides whether a focused terminal claims a chord as shell input rather than letting it
    /// trigger an application-level accelerator. Ctrl+` always toggles the dock.
    /// </summary>
    internal static bool ClaimsChord(Key key, KeyModifiers modifiers)
    {
        if (modifiers.HasFlag(KeyModifiers.Control) && key == Key.OemTilde)
            return false;

        if (modifiers == KeyModifiers.Control)
        {
            var ch = ControlCharFor(key);
            return ch is not null && KeyEncoder.EncodeControlChar(ch.Value, TerminalModifiers.Control) is not null;
        }

        if ((modifiers & ~KeyModifiers.Shift) == KeyModifiers.Alt)
        {
            return MetaCharFor(key, modifiers.HasFlag(KeyModifiers.Shift)) is not null;
        }

        return false;
    }

    /// <summary>
    /// <see cref="ClaimsChord"/>, plus the chords the Kitty protocol exists to tell apart: while
    /// the child has it on, a modified Enter, Tab, Backspace, Escape or Space goes to the child
    /// (Ctrl+Enter in Neovim), not to an application accelerator.
    /// </summary>
    internal bool ClaimsChordNow(Key key, KeyModifiers modifiers)
    {
        if (ClaimsChord(key, modifiers))
            return true;
        return modifiers != KeyModifiers.None
            && key is Key.Enter or Key.Tab or Key.Back or Key.Escape or Key.Space
            && _session?.KeyboardProtocol.KittyActive == true;
    }

    /// <summary>
    /// A key that types text: a printable <paramref name="keySymbol"/> with neither Ctrl nor Alt,
    /// or with both, which is how Windows reports AltGr.
    /// </summary>
    internal static bool IsTextKey(KeyModifiers modifiers, string? keySymbol)
    {
        if (string.IsNullOrEmpty(keySymbol) || char.IsControl(keySymbol[0]))
            return false;
        return modifiers.HasFlag(KeyModifiers.Control) == modifiers.HasFlag(KeyModifiers.Alt);
    }

    /// <summary>
    /// The browser <c>KeyboardEvent.key</c> name the Kitty encoder expects. Named keys map by
    /// <paramref name="key"/>; others use a printable <paramref name="keySymbol"/>, falling back to
    /// the US character for the key when a modifier turned the symbol into a control code.
    /// Empty when the key has no name.
    /// </summary>
    internal static string ToBrowserKey(Key key, string? keySymbol)
    {
        var named = key switch
        {
            Key.Enter => "Enter",
            Key.Tab => "Tab",
            Key.Back => "Backspace",
            Key.Escape => "Escape",
            Key.Up => "ArrowUp",
            Key.Down => "ArrowDown",
            Key.Left => "ArrowLeft",
            Key.Right => "ArrowRight",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Insert => "Insert",
            Key.Delete => "Delete",
            Key.LeftShift or Key.RightShift => "Shift",
            Key.LeftCtrl or Key.RightCtrl => "Control",
            Key.LeftAlt or Key.RightAlt => "Alt",
            Key.LWin or Key.RWin => "Meta",
            Key.CapsLock => "CapsLock",
            Key.NumLock => "NumLock",
            Key.Scroll => "ScrollLock",
            Key.PrintScreen => "PrintScreen",
            Key.Pause => "Pause",
            Key.Apps => "ContextMenu",
            >= Key.F1 and <= Key.F24 => "F" + (key - Key.F1 + 1),
            _ => null,
        };
        if (named is not null)
            return named;

        if (!string.IsNullOrEmpty(keySymbol) && !char.IsControl(keySymbol[0]))
            return keySymbol;

        if (key is >= Key.A and <= Key.Z)
            return ((char)('a' + (key - Key.A))).ToString();
        if (key is >= Key.D0 and <= Key.D9)
            return ((char)('0' + (key - Key.D0))).ToString();
        if (key is >= Key.NumPad0 and <= Key.NumPad9)
            return ((char)('0' + (key - Key.NumPad0))).ToString();

        return key switch
        {
            Key.Space => " ",
            Key.OemMinus or Key.Subtract => "-",
            Key.OemPlus => "=",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe or Key.OemBackslash => "\\",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemComma => ",",
            Key.OemPeriod or Key.Decimal => ".",
            Key.OemQuestion or Key.Divide => "/",
            Key.OemTilde => "`",
            Key.Multiply => "*",
            Key.Add => "+",
            _ => "",
        };
    }

    /// <summary>
    /// The browser <c>KeyboardEvent.code</c> name for a physical key. Avalonia's names follow the
    /// same spec except letters (<c>A</c> → <c>KeyA</c>) and the numpad (<c>NumPad7</c> →
    /// <c>Numpad7</c>). Empty when the key is unknown, which the encoder allows.
    /// </summary>
    internal static string ToBrowserCode(PhysicalKey key)
    {
        if (key == PhysicalKey.None)
            return "";
        if (key is >= PhysicalKey.A and <= PhysicalKey.Z)
            return "Key" + (char)('A' + (key - PhysicalKey.A));
        var name = key.ToString();
        return name.StartsWith("NumPad", StringComparison.Ordinal) ? "Numpad" + name[6..] : name;
    }

    // ── small helpers ───────────────────────────────────────────────────────────────────────

    private const int UnfocusedCursorPenStyle = 2;

    internal long PenMissesForTesting => _pens.Misses;
    internal long RunMissesForTesting => _runMisses;
    internal int CachedRunCountForTesting => _runs.Count;
    internal IGlyphTypeface? GlyphFaceForTesting(int face) => _glyphFaces[face];

    // Leaves the control without glyph typefaces, as when none resolves; the next remeasure restores them.
    internal void DropGlyphTypefacesForTesting()
    {
        Array.Clear(_glyphFaces);
        _glyphs.Clear();
        ClearRuns();
    }

    internal void ResetRenderCountersForTesting()
    {
        _brushes.ResetCounters();
        _pens.ResetCounters();
        _glyphs.ResetCounters();
        _runMisses = 0;
    }

    private IBrush Brush(uint argb) => _brushes.Get(argb);
    private IPen Pen(uint argb, double thickness, int style) => _pens.Get(new PenKey(argb, thickness, style));

    private static uint ArgbOf(Rgb c) => ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    private static uint Blend(uint fg, uint bg, double t)
    {
        byte L(int shift) => (byte)Math.Round(
            ((fg >> shift) & 0xFF) * (1 - t) + ((bg >> shift) & 0xFF) * t);
        return ((uint)0xFF << 24) | ((uint)L(16) << 16) | ((uint)L(8) << 8) | L(0);
    }
}
