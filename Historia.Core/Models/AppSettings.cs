namespace Historia.Models;

/// <summary>
/// Application-wide settings. Persisted as <c>settings.json</c> in
/// <c>%APPDATA%/Historia/</c>, loaded once synchronously at startup and written back
/// with debounced atomic writes.
/// </summary>
public sealed record AppSettings
{
    /// <summary>Schema version for the migration pipeline. v2: graph columns;
    /// v3: issue autolinks; v4: hidden Author/Date/SHA, session restore on, recents 30;
    /// v5: multi-line tab strip; v6: persistent graph cache toggle; v7: graph
    /// row hard cap; v8: local repo-state cache toggle; v9: terminal; v10: tab-width mode;
    /// v11: custom-theme id; v12: splash-screen toggle.</summary>
    public int SchemaVersion { get; init; } = 21;

    // ── Profile &amp; Identity ──
    /// <summary>the default profile — the clone-dialog pre-selection and what an opened
    /// repo adopts when it has no per-repo selection and the default is non-global. Null = the
    /// reserved global profile is the default (the automatic case).</summary>
    public Guid? DefaultProfileId { get; init; }

    // ── Network &amp; Privacy ──
    /// <summary>drives <c>IAutoFetchService</c>'s <c>[AutoFetch]</c> loop, which periodically
    /// <c>git fetch</c>es every open repository. It is not gated to a token-bearing profile —
    /// it fetches SSH and <i>Local config</i> repos too — so this toggle is the single opt-out
    /// that lets a user guarantee no unsolicited network traffic. Default on.</summary>
    public bool AutoFetchEnabled { get; init; } = true;

    /// <summary>lets local tools (<c>historia-cmdr</c>, and the coding agents that call it) ask
    /// this Historia over its per-user pipe to open a repository, show a commit or diff, or offer a
    /// commit message to the commit box. It never changes a repository or a setting. Turning it
    /// off makes every control request answer <c>disabled</c>; second launches still hand over
    /// their repository paths. Default on.</summary>
    public bool ExternalControlEnabled { get; init; } = true;

    /// <summary>Hours an item in the sidebar's CMDR list (navigations agents queued) is kept:
    /// when Historia starts, it removes older items. Clamped <see cref="MinCmdrQueueExpiryHours"/>…
    /// <see cref="MaxCmdrQueueExpiryHours"/> where it is read and where it is edited. Default 5.</summary>
    public int CmdrQueueExpiryHours { get; init; } = 5;

    /// <summary>The CMDR list shows the newest item first; off shows the oldest first. Set from
    /// the list's own sort button. Default off.</summary>
    public bool CmdrQueueNewestFirst { get; init; }

    public const int MinCmdrQueueExpiryHours = 1;
    public const int MaxCmdrQueueExpiryHours = 168;

    /// <summary>Math.Clamp to the accepted <see cref="CmdrQueueExpiryHours"/> range.</summary>
    public static int ClampCmdrQueueExpiryHours(int value) => Math.Clamp(value, MinCmdrQueueExpiryHours, MaxCmdrQueueExpiryHours);

    /// <summary>
    /// When on (default), developer session logs and performance timing marks are recorded under
    /// the logs directory. Applies live. Also on while <c>HISTORIA_LOG</c> is set.
    /// </summary>
    public bool LoggingEnabled { get; init; } = true;

    /// <summary>
    /// When on, adds a Developer entry to the app menu with testing and diagnostic tools.
    /// Takes effect after restart. Default false.
    /// </summary>
    public bool DeveloperPanelEnabled { get; init; }

    // ── UI preferences ──
    /// <summary>The Light / Dark / System choice; <c>EffectiveTheme</c> is derived. New installs
    /// follow the OS. <see cref="UseCustomTheme"/> can override it.</summary>
    public ThemeMode AppTheme { get; init; } = ThemeMode.System;

    /// <summary>Independent override; wins over <see cref="AppTheme"/>.</summary>
    public bool HighContrastOverride { get; init; }

    /// <summary>The Developer panel's custom-theme override. The theme named by
    /// <see cref="CustomThemeId"/> renders in place of <see cref="AppTheme"/> only while this
    /// is on, that id resolves to a saved theme and High Contrast is off; otherwise
    /// <see cref="AppTheme"/> renders. Turned off at startup without developer mode.</summary>
    public bool UseCustomTheme { get; init; }

    /// <summary>The custom theme selected in the Developer panel (a file stem under
    /// <c>themes/</c>). Used while <see cref="UseCustomTheme"/> is on; kept when it is turned
    /// off, so turning it back on restores the same theme.</summary>
    public string? CustomThemeId { get; init; }

    public DiffViewMode DefaultDiffView { get; init; } = DiffViewMode.Unified;

    /// <summary>
    /// Off (default): every displayed commit date is the commit (committer) date — the date the
    /// graph is sorted by. On: show the author date instead. Display-only; never changes row order.
    /// </summary>
    public bool ShowAuthorDate { get; init; }

    /// <summary>How the graph and the tab strip interpret mouse-wheel / trackpad scroll.
    /// <c>Auto</c> matches the OS "natural / inverted" setting (macOS &amp; Linux; Windows is
    /// always standard); <c>Standard</c> / <c>Inverted</c> force it.</summary>
    public WheelScrollDirection WheelScrollDirection { get; init; } = WheelScrollDirection.Auto;

    /// <summary>When on (default), a brief splash animation covers startup while the shell and
    /// the focused repository's commit graph load out of sight. Off = the shell window
    /// appears immediately and the graph fills in as it loads. Takes effect on the next launch.</summary>
    public bool ShowSplashScreen { get; init; } = true;

    /// <summary>when the repo tab strip overflows, wrap tabs onto extra app-band rows
    /// instead of horizontal-scrolling a single row. <see cref="MaxTabRows"/> caps the count.</summary>
    public bool EnableMultiLineTabs { get; init; }

    /// <summary>2–5. Maximum app-band rows to fill with tabs before the ◀ ▶ paging
    /// arrows appear on the bottom row. Ignored unless <see cref="EnableMultiLineTabs"/>.</summary>
    public int MaxTabRows { get; init; } = 3;

    /// <summary><see cref="TabWidthMode.Dynamic"/> (default) sizes each repo tab to its
    /// label; <see cref="TabWidthMode.Fixed"/> gives every tab <see cref="FixedTabWidth"/> px.</summary>
    public TabWidthMode TabWidthMode { get; init; } = TabWidthMode.Dynamic;

    /// <summary>Per-tab width in pixels when <see cref="TabWidthMode"/> is
    /// <see cref="TabWidthMode.Fixed"/>. Clamped
    /// <see cref="MinFixedTabWidth"/>…<see cref="MaxFixedTabWidth"/>.</summary>
    public int FixedTabWidth { get; init; } = DefaultFixedTabWidth;

    public const int DefaultFixedTabWidth = 175;
    public const int MinFixedTabWidth = 120;
    public const int MaxFixedTabWidth = 400;

    /// <summary>Math.Clamp to the accepted <see cref="FixedTabWidth"/> range.</summary>
    public static int ClampFixedTabWidth(int value) => Math.Clamp(value, MinFixedTabWidth, MaxFixedTabWidth);

    // graph column layout (order, widths, visibility, the width pin) and the commit-walk
    // ceiling live in a per-repository store — see Historia.Services.Settings.IRepoLayoutService.

    /// <summary>When on (default), every notice in the bottom-right corner — errors, warnings,
    /// information and confirmations — closes itself 10 seconds after it appears. Off: every
    /// notice stays until the user closes it. Applies live, including to notices already shown.</summary>
    public bool AutoHideNotices { get; init; } = true;

    /// <summary>Flat path list vs. collapsible folder tree for a commit's changed files.</summary>
    public CommitFilesView CommitFilesView { get; init; } = CommitFilesView.Path;

    // ── Diff &amp; editor ──
    public int DiffContextLines { get; init; } = 3;

    // ── Git &amp; repository ──
    /// <summary>Overrides discovery.</summary>
    public string? GitExecutablePath { get; init; }

    /// <summary>On by default. Restore opens only the
    /// focused repository; every other tab is a placeholder until first focused.</summary>
    public bool RestoreSessionTabs { get; init; } = true;
    public bool PruneOnFetch { get; init; } = true;
    public bool PullWithRebase { get; init; }

    /// <summary>When on (the default), a pull is followed by a fetch of every branch and tag so the
    /// remote-tracking refs match the remote. A settings file without the field reads as on.</summary>
    public bool FetchAfterPull { get; init; } = true;

    /// <summary>When on, the "Stash all" buttons (staging panel + shortcuts bar) prompt
    /// for a stash message instead of using the default <c>Stash of &lt;branch&gt;</c>.</summary>
    public bool UseCustomStashNames { get; init; }
    public int MaxRecentRepositories { get; init; } = 30;

    /// <summary>When on, a repository's enumerated state (local &amp; remote branches, tags,
    /// stashes, submodules, worktrees) is cached to the machine cache folder so the sidebar
    /// paints immediately on the next launch. The real enumeration still runs and corrects any
    /// staleness.</summary>
    public bool PersistRepoCache { get; init; } = true;

    /// <summary>
    /// when on, the graph keeps only the fixed-size layout arrays in memory and pages
    /// per-commit metadata (message, author, dates) from a random-access cache file, and
    /// background-caches the whole history to disk. Read once at startup — a change takes
    /// effect on the next launch. Default on.
    /// </summary>
    public bool GraphMetadataFromDisk { get; init; } = true;

    /// <summary>
    /// When on, a large (200,000+ row), exhausted
    /// on-disk graph store whose walk key has moved (a fetch, a new branch) is brought up to date
    /// by splicing in just the added/removed commits (<see cref="Historia.Services.Graph.GraphMetaSplice"/>)
    /// instead of a full re-walk, when every guard passes — falling back to the ordinary reorder
    /// otherwise. Off by default: experimental, and only pays off on histories large enough that a
    /// full re-walk is itself expensive. Read once at startup — a change takes effect on the next
    /// launch of Historia.
    /// </summary>
    public bool EnableGraphSplice { get; init; }

    /// <summary>
    /// When on, the graph keeps at most <see cref="GraphLimits.LightweightMaxResidentRows"/> rows
    /// resident and preloads in steps of <see cref="GraphLimits.LightweightSilentCrawlRows"/>,
    /// overriding <see cref="SilentCrawlRowCap"/> and <see cref="ResidentRowCap"/> without changing
    /// them. Read once at startup.
    /// </summary>
    public bool LightweightMode { get; init; }

    /// <summary>
    /// How many rows the graph's silent background crawler pre-loads into the
    /// resident layout after first paint, before stopping until the viewport scrolls past the
    /// loaded threshold, advancing another this many rows each step. Deeper history
    /// reaches disk in full via the background spill regardless of this cap.
    /// <c>0</c> means uncapped — the crawler runs all the way to the resident-row ceiling
    /// (<see cref="GraphLimits.GetMaxResidentRows"/>). Default 500,000.
    /// </summary>
    public int SilentCrawlRowCap { get; init; } = 500_000;

    public const int MinSilentCrawlRowCap = 0;

    /// <summary>
    /// The same ceiling as <see cref="MaxResidentRowCap"/> (no app-imposed limit), so any crawl
    /// cap up to the resident cap can be set directly, not only through <c>0</c> (uncapped).
    /// </summary>
    public const int MaxSilentCrawlRowCap = MaxResidentRowCap;

    /// <summary>Math.Clamp to the accepted <see cref="SilentCrawlRowCap"/> range. Follows the
    /// house style for a bounded numeric setting.</summary>
    public static int ClampSilentCrawlRowCap(int value) => Math.Clamp(value, MinSilentCrawlRowCap, MaxSilentCrawlRowCap);

    /// <summary>
    /// The resident-row hardcap applied when
    /// <see cref="GraphMetadataFromDisk"/> is on — the absolute ceiling on how many rows the
    /// graph layout will ever keep resident for one repository, regardless of scrolling or the
    /// background crawler/spill. Raising it lets a very large repository's full history become
    /// reachable at the cost of more resident memory; lowering it tightens the memory ceiling at
    /// the cost of capping history earlier. Read once at startup — a change takes effect on the
    /// next launch. Default 2,500,000 (<see cref="GraphLimits.MaxResidentRowsDisk"/>). There is
    /// no app-imposed upper limit; Settings asks for a second confirmation above
    /// <see cref="ResidentRowCapWarnThreshold"/>.
    /// </summary>
    public int ResidentRowCap { get; init; } = GraphLimits.MaxResidentRowsDisk;

    public const int MinResidentRowCap = GraphLimits.MaxResidentRowsMemory;

    /// <summary>No app-imposed ceiling — only the <see cref="int"/> range bounds it.</summary>
    public const int MaxResidentRowCap = int.MaxValue;

    /// <summary>Settings shows an extra heavy-memory warning for a value above this.</summary>
    public const int ResidentRowCapWarnThreshold = 5_000_000;

    /// <summary>Math.Clamp to the accepted <see cref="ResidentRowCap"/> range.</summary>
    public static int ClampResidentRowCap(int value) => Math.Clamp(value, MinResidentRowCap, MaxResidentRowCap);

    /// <summary>Issue-key → URL rules applied to commit messages. Account-free.</summary>
    public IReadOnlyList<IssueAutolinkRule> IssueAutolinks { get; init; } = [];

    /// <summary>The AI integrations, in the order they were added.</summary>
    public IReadOnlyList<AiServiceProfile> AiServices { get; init; } = [];

    /// <summary>The one active AI integration, or null when AI commit messages are off. An id
    /// that matches no profile reads as null.</summary>
    public string? ActiveAiServiceId { get; init; }

    /// <summary>Word-wrap diff lines instead of horizontal scroll.</summary>
    public bool DiffWordWrap { get; init; }

    // ── External tools ──
    public string? ExternalMergeTool { get; init; }

    // ── Terminal ──
    /// <summary>Explicit shell override. Null → resolve per platform. A set-but-missing
    /// path is a <c>ShellNotFound</c> error, never a silent fallback.</summary>
    public string? TerminalShellPath { get; init; }

    /// <summary>Shell arguments, passed as a list. Default empty — no <c>-l</c> login flag.</summary>
    public IReadOnlyList<string> TerminalShellArgs { get; init; } = [];

    /// <summary>Null → the platform monospace stack.</summary>
    public string? TerminalFontFamily { get; init; }

    public int TerminalFontSize { get; init; } = 13;

    /// <summary>Ring-buffer scrollback per session. Clamped <see cref="MinTerminalScrollback"/>…
    /// <see cref="MaxTerminalScrollback"/>. Multiplies by open sessions — see the settings note.</summary>
    public int TerminalScrollbackLines { get; init; } = 5_000;

    public double TerminalDockHeight { get; init; } = 260;
    public TerminalCursorStyle TerminalCursorStyle { get; init; } = TerminalCursorStyle.Block;
    public bool TerminalCursorBlink { get; init; } = true;

    /// <summary>Lets programs turn on the Kitty keyboard protocol. Read when a session starts.</summary>
    public bool TerminalKittyKeyboard { get; init; } = true;

    /// <summary>Lets programs write the system clipboard through OSC 52. Checked on every write.</summary>
    public bool TerminalAllowClipboardWrite { get; init; } = true;

    public const int MinTerminalScrollback = 500;
    public const int MaxTerminalScrollback = 100_000;

    /// <summary>Math.Clamp to the accepted <see cref="TerminalScrollbackLines"/> range. Follows
    /// the house style for a bounded numeric setting.</summary>
    public static int ClampTerminalScrollback(int value) => Math.Clamp(value, MinTerminalScrollback, MaxTerminalScrollback);

    /// <summary>These settings with every option only the Developer panel can change turned
    /// off — applied at startup when developer mode is off, so no developer-only option stays
    /// in force where it cannot be seen or changed. <see cref="CustomThemeId"/> is kept.</summary>
    public AppSettings WithoutDevOnlyOptions() => this with { UseCustomTheme = false };
}
