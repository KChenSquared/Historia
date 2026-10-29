# Historia — Constitution

The invariants. Every one of these was learned the hard way, from a real defect or a real
design decision. The `(§I-…)`, `(§V2-…)`, and `(§V3-…)` citations point into the initial, v2,
and v3 design documents respectively; those are archived version plans that are rotated out of
this repo, so the tags are kept as provenance markers rather than links. The living design is
the split set under [`Historia-DevDocs/ComprehensiveDesign/`](../Historia-DevDocs/ComprehensiveDesign/).

**A rule here outranks convenience, consistency with other code, and the obvious
implementation.** If a rule blocks you, raise it — do not route around it. If you break one
deliberately, say so in the PR and say why. The rules are written so human and coding-agent
contributors can follow them mechanically, and an agent's change is held to them exactly like a
human's.

---

## Rule 00 — Nothing destructive happens without the user saying yes

> **Every action that destroys the user's work or irreversibly rewrites their history must be
> gated behind an explicit confirmation the user actively accepts — a modal alert with a red
> `warning`, naming the exact effect. No exceptions, no "just this once", no silent
> path.**

This is the number-one rule and it outranks every other rule in this document, **including Rule
0**. If offline-first, performance, progressive disclosure, or a smoother workflow ever appears
to need a confirmation skipped, they lose. Losing the user's uncommitted work or their commits
is the one failure this app must never cause by its own hand.

**00.1 — What counts as destructive.** Anything Git cannot cleanly undo, or that throws away
uncommitted work:
- discard / checkout-over working-tree changes, delete untracked files, `git clean`
- hard reset, any reset that drops commits, branch or tag deletion (including `-D`
  force-delete)
- history rewrites: an interactive rebase that rewrites anything (drops, squashes, rewords,
  reorders — the pick-only, unchanged-order fast path is the one exception), an amend that
  rewrites a commit already on its upstream, `push --force*`
- stash drop / clear, worktree removal, submodule deinit (deinitialises it, removes it from the
  index and deletes its own `.git/modules/<name>` repository)
- deleting a profile, a theme, a saved session; wiping Historia's local data
- overwriting a shared external resource such as `~/.gitconfig`

When it is arguable whether an action belongs here, it does. *(§I-6.9.1, §I-6.13, §I-6.15.1)*

**00.2 — The confirmation is a modal alert, not a toast and not a one-click inline control.**
It routes through `IDialogService.ConfirmDestructiveAsync` (or `ChooseAsync` with
`DialogIcon.Warning`, when there are several remedies) — never the plain `ConfirmAsync`
overload hand-rolled to look the same. The one entry point hard-wires:
- the large red `warning` glyph;
- naming the exact effect (the files, the branch, the commit count), never a bare "Are you
  sure?";
- **Cancel** as the primary (blue / accent) button *and* the keyboard default, so a reflexive
  Enter or click lands on the safe choice;
- **Cancel** as the **rightmost** button — Windows `[Action] [Cancel]` order. Primary styling
  changes Cancel's colour, never its position;
- the confirming button labelled with its verb ("Delete", "Discard", "Force push"), styled plain
  or `danger` — never primary.

**Exemption.** A `ChooseAsync` chooser may keep its ordinary (non-destructive) default and icon
when its destructive option itself opens `ConfirmDestructiveAsync` before acting — the red
treatment then lands on the dialog that actually performs the destructive step, not on the
chooser that merely offers it. The push-rejection chooser (force-push vs. pull vs. cancel) is
the current example.

**00.3 — "Don't ask again this session" is the only permitted suppression.** It is
per-session and per-operation-class, it is never the default, and it never persists to disk.
There is no global "turn off confirmations" setting.

**00.4 — Guard it with a test.** `DestructiveConfirmationTests` runs two checks, scanning all of
`Historia.App` and `Historia.Core/ViewModels`: that no call site hand-rolls the destructive
treatment on the plain `ConfirmAsync` overload (`destructive: true`, `defaultToCancel:`,
`DialogIcon.Warning` outside the one entry point's own implementation), and that every listed
destructive service call sits in a method that also calls `ConfirmDestructiveAsync`, or on an
explicit, commented allowlist. `DestructiveConfirmDialogTests` guards the dialog builder itself.
A destructive call site with no confirm at all, or one that regresses to a bare confirm, fails
the build. *(Rule 24.)*

---

## Rule 0 — Offline first, always

> **Historia must start and render completely, every time, on a machine with no network — from
> what ships in the package, plus what the Windows platform itself already provides.**

This is the first rule after Rule 00 because it is the one that fails silently: nothing
crashes, the app just quietly reaches out. Everything below it is subordinate to it.

**0.1 — No external assets.** No CDN fonts, icon fonts, stylesheets, scripts, or images. Every
   `url()`, `src`, and `href` in the UI resolves to a file in the package. *(§V2-10)*
**0.2 — No network call the user did not initiate.** No telemetry, no analytics, no crash
   reporting, no avatar lookups. Background polling and hosting-API calls run only while a
   profile with a connected token is the active per-repo selection; a repo with its **SSH Only**
   toggle on makes no non-SSH network call and falls closed rather than back to HTTPS.
   **Auto-fetch** (§12.1) is the one sanctioned exception to "only what the user initiated": while
   `AutoFetchEnabled` is on (default on) it periodically `git fetch`es every open repo over its
   configured transport, regardless of profile/token. Opening a repo with a remote is treated as
   the consent — by hand, or through `historia-cmdr` while external control is enabled (rule 27);
   the setting is the opt-out, and it must stay a single visible toggle that turns
   *all* of it off — never a call that cannot be disabled. *(§I-5.5, §12.1, §13)*
**0.3 — Three tiers, and only the third is negotiable.**
   - **Preinstalled platform — assume it.** Present on a stock Windows 11 machine: **Segoe UI**
     and the **Win32/DWM APIs**.
   - **Declared prerequisites — permitted, but they must be declared.** Not preinstalled, and
     legitimately required: the **.NET Desktop Runtime**. Permitted only when the installer
     installs it, the requirement is documented for anyone running raw binaries, and rule 0.4's
     check reports its absence rather than crashing. Adding a new prerequisite is a deliberate
     decision, not something a `csproj` edit does by accident.
   - **Everything else — bundle it.** Fonts, icons, syntax highlighters, markdown renderers,
     brand assets. When it is arguable, bundle. *(§V2-10.3)*

     **"Bundle" means ship it, not write it.** A NuGet package that ships inside the package and
     makes no network call satisfies this rule completely. Rule 0.1 governs runtime asset
     *fetches*; it says nothing about compile-time dependencies. Hand-rolling a highlighter, a
     renderer, or a differ is a choice to be argued on its own merits — coverage, licence,
     attack surface — and this rule never requires it. Note that the merits sometimes favour
     hand-rolling: `MarkdownRenderer` encodes every source character before adding markup, so
     raw HTML from a cloned repo's README cannot survive, which a default Markdig pipeline does
     not give you. *(§V3-18.1 D13)*
**0.4 — Assume the baseline, but verify it — never silently repair it.** Anything outside the
   package is detected at startup and, when absent, reported with an actionable message naming
   what is missing and how to get it. The app still starts where it can. Never a crash, never a
   silent download. Applies to `git.exe` (at the minimum version the README's Requirements
   section names) and the .NET Desktop Runtime.
   A feature that needs a newer Git than the checked minimum raises the minimum in the same
   change: the version checker (`GitExecutableInfo.MinimumVersion` and its tests), the README's
   Requirements section, and every other file that states the minimum.
   Dubious repository ownership (`UnsafeRepository`) is reported with an actionable message
   giving the `safe.directory` command; Historia never writes `safe.directory` automatically.
   *(§I-6.2.3, §V2-10.3)*
**0.5 — Windows 10 is supported but is not the baseline.** The TFM floor is 1809. A dependency
   guaranteed on Windows 11 may legitimately be missing there, which is exactly what rule 0.4
   exists to handle gracefully.
**0.6 — Guard it with a test, not a convention — and guard the whole codebase.**
   `OfflineAssetTests` fails the build if an `http(s)` URL reappears in any `.cs` or `.axaml`
   file. Historia is majority C#, so a scan that skips the C# layer guards the half least likely
   to break the rule. Consented exceptions are named in an explicit allowlist; a blind spot is
   not an allowlist. *(§V2-10.4, §V3-18.2 Q8)*
**0.7 — Pin every dependency.** No floating version wildcards.
**0.8 — Git works with zero accounts.** Every core feature is fully local; remote API panels
   collapse quietly when unconfigured. No nag screens, no forced login. *(§I-5.3, §I-5.4)*

---

## Git correctness

1. **Git access is the CLI only.** Every process is set up by `GitCliProcessSetup`
   (`IGitCliService`, or `GitLogStream` for the streamed graph walk). LibGit2Sharp is a
   test-only fixture dependency; `NoLibGit2InProductionTests` enforces it. Reading git's own
   files directly is allowed only as a read-only accelerator over a CLI path that stays the
   fallback: documented formats only, never written, no handle held past the call, and
   parity-tested against git. The current ones: `HEAD` and ref timestamps (`RefStateToken`),
   the index header (`FileWatcherService`), `packed-refs` (`PackedRefsReader`) and the
   commit-graph (`CommitGraphIndex`). The one sanctioned write into git's directory is removing
   **stale lock files** that a git process left behind when it died — git has no command for
   this, and its own error message tells the user to delete the file. Removal happens only after
   the destructive confirmation (Rule 00) names the files. *(§I-6.3.1)*
2. **Repository writes run inside the write lock; reads never take it.** Historia's own
   index/HEAD/ref/work-tree writes run inside the repository write lock; reads never take it
   and run with `--no-optional-locks`. The lock is never held across a dialog or a nested
   locked call. *(§I-6.3.1)*
3. **Network is CLI only.** Git clone, fetch, pull, and push always run through the CLI with
   full access to `ssh-agent`, `GIT_SSH_COMMAND`, and GCM for authentication and privacy.
   *(§I-6.4.1)*
4. **CLI arguments go through `ArgumentList`.** Never a concatenated string, never a shell.
   Paths after `--` as literal pathspecs. *(§I-6.5.1)*
5. **Drain stdout and stderr concurrently before `WaitForExit`.** The other order deadlocks
    once output exceeds the pipe buffer. *(§I-6.5.1)*
6. **Raw stderr is never shown.** Classify into `GitErrorKind`; `Unknown` surfaces verbatim
    behind a Details expander. Never invent a silent fallback for an unrecognized failure.
    *(§I-6.5.3)*
7. **Force-push is `--force-with-lease --force-if-includes`.** Never plain `--force`; the
    lease alone is trivially satisfiable after a background fetch. *(§I-6.13)*
8. **Never auto-accept an unknown SSH host key.** *(§I-6.13)*
9. **Destructive actions confirm, naming the exact files.** Discard, delete-untracked, hard
    reset. The data is unrecoverable through Git. This is the Git-specific instance of **Rule
    00** — the general requirement and the dialog contract (red `warning`, Cancel
    primary and rightmost, "Don't ask again this session" the only suppression) live there.
    *(§I-6.9.1)*
10. **Commits always go through the CLI.** Hooks (including `core.hooksPath`) and signing
    always apply, preserving tools such as `husky` and `pre-commit`. *(§I-6.9.2)*
11. **Paths are repo-relative with forward slashes.** Convert only at the filesystem
    boundary. *(§I-6.8.1)*
12. **History is always paged.** No call materializes an unbounded commit list. *(§I-6.4.2)*

---

## State & threading

13. **Mutate on background threads, raise events on the UI thread** — `IUiDispatcher` over
    Avalonia's `Dispatcher.UIThread`. Producers never touch the UI. *(§I-6.7)*
14. **Every subscriber is `IDisposable` and unsubscribes.** Undisposed handlers on a
    long-lived state object are the primary leak vector. *(§I-6.7)*
15. **No `AddScoped`.** Nothing creates a DI scope, so a scoped registration behaves as a
    singleton and silently breaks multi-tab isolation. Per-repo services are constructed and
    disposed by `WorkspaceService`. *(§I-6.3)*
16. **Cross-surface communication goes through `IRepositoryStateService`.** Never through a
    parent callback. *(§I-6.7, §V2-8.3)*

---

## Rendering & performance

17. **Never block the UI thread.** A 2 s budget is a correctness ceiling, not licence to
    block; show a skeleton after ~200 ms. *(§I-3)*
18. **Anything long is virtualized** — graph rows, diff lines. Only visible content plus
    overscan exists. *(§I-6.10.3, §I-7.4)*
19. **The graph render path creates no Historia-owned object per frame — and a test says so.**
    Pool every reusable `Pen`, `Brush`, and `Geometry`, clip geometries included; cache
    **shaped text** (`TextLayout`) with every glyph-affecting input in the key, and never draw
    `FormattedText` in a render path — a cached `FormattedText` re-shapes its text on every draw,
    so its cache never misses while every visible string is shaped every frame. **"None" is
    literal and it is measured** — but measured as *pool and cache misses*, never as bytes: every
    pool and cache carries a miss counter, and a render pass after the first must record zero.
    `GraphTextShapingTests` asserts zero pool and text misses on a second pass, and
    `ShapedTextRenderPathTests` fails on a `DrawText` call in the render-path files. Counting bytes
    cannot work here — the rendering stack allocates its own per-frame buffers however correct
    the pooling is, so a frame-to-frame byte delta stays perfectly flat while a per-frame `new`
    goes on happening, and a gate that passes while the rule is broken is worse than none.
    *(§I-7.4, §V2-6.3, §V3-2.1, §V3-18.1 D14)*
20. **45+ FPS sustained, no frame over 22.2 ms.** The ceiling is the meaningful half — an
    average hiding 100 ms hitches still feels broken. **This one cannot be a build gate** —
    frame timing on a shared runner measures the runner, not Historia. It is a manual benchmark
    against a recorded baseline, re-run whenever the render path changes, with the number
    written into that phase's implementation log. A performance rule with neither a check nor a
    recorded measurement is an aspiration; the rule must say which of the two it is.
    *(§I-3, §V3-17.2)*

---

## Views

21. **Views are AXAML bound to view models.** Every view and form dialog is an `.axaml` file whose
    state and commands live in a view model under `Historia.Core/ViewModels`; the code-behind wires
    the view model and handles what binding cannot (pointer capture, focus, divider drags). Custom
    drawn surfaces (the commit graph, the terminal, the diff and conflict editors) are custom
    controls placed from markup. A view never rebuilds its tree by hand, and `new Window` appears
    only in `AvaloniaDialogService`. `MvvmViewGuardTests` enforces the sibling `.axaml`, the
    `new Window` limit and the absence of `Rebuild…` methods under `Historia.App/Views` and
    `Historia.App/Platform`; `ViewMarkupGuardTests` keeps the markup on palette keys.

---

## Secrets

22. **Secrets live in Windows Credential Manager.** `profiles.json` stores reference keys;
    profiles store SSH key *paths* and GPG key *ids*, never key material. *(§I-12)*
    **Exception — AI API keys and request headers** are stored in `settings.json`. They are
    currently not treated as secrets, because they are easily revoked and the feature targets
    free-tier accounts. *(2026-10-06)*
23. **SSH keys are per-process environment, never process-global.** Two tabs on two profiles
    must be able to push concurrently. *(§I-6.5.2)*

---

## Working on this codebase

24. **A guarantee that can regress silently gets a test — over the whole codebase.** Offline
    assets, palette parity, incremental-layout equivalence, per-frame allocation, graph edge
    endpoints — anything where breakage produces no error. Two corollaries, both learned from
    tests that passed while the thing they guarded was broken: a test that scans only part of
    the tree is a convention wearing a test's clothes (rule 0.6), and where a real check is
    genuinely impossible, the rule says so and records a measurement instead (rule 20).
25. **One logger: `AppLog`. Do not add a second logging system.** `Historia.Core/Diagnostics/AppLog.cs`
    is the single application logger, reached through `IAppLog` or the static `AppLog` methods.
    Every call is a no-op when disabled and never throws. Unhandled exceptions, notifications,
    classified git failures, gateway and CLI calls and repo open/close are already logged; add an
    explicit call only at a seam none of those covers. `PerfLog` stays a **separate** tool,
    scoped to startup/latency timing — it is not a model for adding another logger.
26. **Code and documents describe the present.** Never keep a *Correction* note, a "superseded"
    marker, or any account of what the code or a document used to say: when something changes,
    rewrite the text to describe the new state and put the history in the commit message.
    - **26.1 — No transition comments.** Never leave comments explaining what you changed, why
      you changed it, or what the previous code looked like (e.g., `// Updated to fix X`,
      `// Iteration 3`, `// OLD CODE:`).
    - **26.2 — No inline changelogs.** Source files are not a git log. Do not add modification
      history to file headers, function blocks, or inline comments.
    - **26.3 — State-based documentation only.** A comment describes what the code does at the
      time the comment is written or updated, and why — nothing else. A comment that would not
      make sense to someone reading the file for the first time — because it references a
      previous version of the code — does not belong, **except** a trap or pitfall (26.7).
      When you change code, update its comments to match in the same edit.
    - **26.4 — Explain in chat only.** If you need to summarize changes or explain your
      reasoning, do so entirely in the chat window or commit message. Never put change
      explanations in the source files.
    - **26.5 — Clean up stale history.** If you encounter old transition comments from previous
      AI iterations while editing a file, delete them silently.
    - **26.6 — Never reference documents.** Comments (and user-visible strings) must not cite
      any document: no design-spec sections (`§7.6`, `§V3-B/19.24`), version or build plans,
      review findings or item ids (`F9`, `R6`, `finding #17`), milestones, file paths into
      `Historia-DevDocs`, and no rule numbers from this constitution (`Rule 00`, `rule 19`) —
      say what the code must do instead (e.g. "destructive: confirm first"). Documents are renumbered, rotated and archived; a pointer into them rots
      silently and misleads. If the reason behind the code matters, state it in the comment
      itself. The dependency runs one way: design documents may point at code, code never
      points at documents.
    - **26.7 — Traps and pitfalls are the exception.** When an obvious approach was tried and
      proved wrong, record it as a *Trap* (or pitfall) in the comment or document that owns the
      behaviour. A trap may describe the failed approach — including what the code used to do
      and what went wrong ("re-priming with `skip = have` used to mix two walks after a fetch")
      — because that history *is* the lesson: it stops the next person from reintroducing it.
      Keep it to what failed and why; the current rule goes alongside it. A trap is removed only
      when the behaviour it guards is removed. Don't revert what one warns against without
      re-reading it.

---

## Automation & agents

27. **Agent-facing surfaces navigate and propose; only the user acts.** Anything an external tool
    can send Historia — today, `historia-cmdr` over the per-user control pipe — may open, focus and
    navigate, may queue a navigation for the user to open later, and may offer text to an editable
    draft the user must still act on (today, the commit message). The queue is Historia's own list
    of pending items, not a setting, and the control host may only add to it; marking, reordering
    and removing items are the user's. It never changes the repository, index, refs, working tree
    or settings, never stages or commits, and never replaces something the user typed without
    `ConfirmDestructiveAsync`. It stays local and per-user, and one visible setting turns it off.
    Opening a repository through it is recorded in the session and the Recent list exactly like a
    manual open. A new verb that is neither plain navigation, a queued navigation, nor a proposal
    the user must act on does not ship as an agent verb. This is the external-control instance of
    **Rule 00**, as rule 9 is the Git instance. `ExternalControlReadOnlyTests` scans the control
    host, the shared control navigator and every control file for any write path (the git gateway,
    the write lock, settings writes, dialogs, staging patches, file writes, starting processes) and
    for use of anything outside an allowlist of navigation members plus the queue's one add-only
    `Enqueue` call, so a mutating call fails the build; `StagingPanelProposalTests` checks that a
    proposal never replaces a typed draft silently and never commits.
