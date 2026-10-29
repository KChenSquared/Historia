# Contributing to Historia

Thank you for your interest in contributing to **Historia**! I am building a blazing-fast, open-source Git client for Windows, and I welcome contributions of all kinds: bug fixes, performance optimizations, new features, documentation improvements, and UI refinements.

---

## 1. Core Principles & Philosophy

When contributing to Historia, keep these core tenets in mind:

0. **Confirm Before Destroying Anything (highest priority):** No code path may discard the
   user's uncommitted work or irreversibly rewrite their history without an explicit
   confirmation the user actively accepts. The confirmation is a modal alert raised through
   `IDialogService.ConfirmDestructiveAsync` (or `ChooseAsync` with `DialogIcon.Warning` when
   there are several remedies) — the only entry points. They hard-wire the red `warning`, the
   exact effect named, and **Cancel** as the primary (blue) button and the keyboard default
   while it stays the rightmost button. Hand-rolling that treatment on the plain `ConfirmAsync`
   overload fails `DestructiveConfirmationTests`. See constitution **Rule 00**. This outranks
   every other principle below, including performance.
1. **Performance First:** Historia is built to match the speed of native clients like Git-Fork. Avoid unnecessary allocations in hot paths (commit log parsing, DAG calculation, diff rendering). Always profile and benchmark performance-critical code.
2. **Never Block the UI Thread:** All Git operations, I/O, and heavy computations must execute on background threads and communicate via non-blocking asynchronous patterns.
3. **Simplicity & Polish:** Strive for clean, intuitive UI interactions. Complex Git concepts should be rendered clearly and visually.
4. **Progressive Disclosure:** Design interfaces that reveal complexity only as needed. Common workflows (staging, unstaging, committing, switching branches, reading the graph) must remain streamlined, clutter-free, and instantly approachable. Advanced capabilities (interactive rebase, worktree management, submodules, terminal access, deep diagnostic metadata) should be surfaced gracefully through context menus, collapsible drawers, or dedicated drill-downs rather than dominating the primary screen space. Errors must show clean, actionable human descriptions first, preserving raw Git stderr behind a details disclosure.
5. **Built for Agentic Coding:** Two ways. Historia is where you review what coding agents wrote: `historia-cmdr` lets an agent queue the commit, diff or uncommitted change it just produced for you to review in Historia, or hand over staged files with a proposed commit message, and only you commit (constitution [Rule 27](constitution.md#automation--agents)). And the codebase is built for agent contributors: the constitution is written to be followed mechanically, tests enforce what can regress silently, and multi-file work follows a phased plan whose parallel milestones own disjoint files (see [Working with coding agents](#6-working-with-coding-agents)).

---

## 2. Prerequisites & Environment Setup

### Required Tools
- **Operating System:** Windows 10 (version 1809 / build 17763 or higher) or Windows 11 is the parity target. Linux and macOS can build and run the test suites.
- **.NET SDK:** .NET 10.0 SDK (version 10.0.202 or compatible latest feature, pinned in `global.json`)
- **IDE:**
  - [Visual Studio 2022 / 2025](https://visualstudio.microsoft.com/) with the **.NET Desktop Development** workload, OR
  - [Visual Studio Code](https://code.visualstudio.com/) with the **C# Dev Kit** extension, OR
  - [JetBrains Rider](https://www.jetbrains.com/rider/) with .NET desktop support.

---

## 3. Building and Running Locally

### Clone the Repository
```bash
git clone https://github.com/KChenSquared/GitHistoria.git
cd GitHistoria
```

### Restore Dependencies & Build
```powershell
dotnet build Historia.slnx
```

### Run the Application
```powershell
dotnet run
```
Run from the repository root, this builds only the app and `historia-cmdr` (see `Historia.proj`),
then launches Historia.

### Run Tests
```powershell
# Inner loop: fast pure unit tests (< 15 s)
.\dev\test.ps1 -Tier fast

# Pre-push: UI and integration tiers
.\dev\test.ps1 -Tier git
.\dev\test.ps1 -Tier ui

# Full suite verification (~2-3 min; slower when started right after another run, see design §24.6)
.\dev\test.ps1 -Tier all
```

#### Test Suite Architecture & Parallelism
- **Parallel Collections:** Test collections run in parallel on 16 threads (`xunit.runner.json`). Under xunit's default `conservative` algorithm, that caps how many classes run at once, and a class's own tests run one after another.
- **Slowest First:** `SlowestFirstCollectionOrderer` starts the longest-running classes first, so none of them is admitted late and becomes the run's tail. Re-rank its list from a TRX run (`dotnet test --logger trx`) when one class starts dominating the end of the run.
- **Serial Collection:** Tests that mutate process-global state, configure ambient environment variables, watch process-wide events such as `TaskScheduler.UnobservedTaskException`, or contend on global locks must be annotated with `[Collection("serial")]`. The serial collection runs alone after every parallel one, so keep it small. It includes 16 test classes:
  1. `AppPathsTests`
  2. `AppLogTests`
  3. `TestScratchTests`
  4. `GraphConcurrencyInvariantTests`
  5. `RepositoryStateRefreshCoalescingTests`
  6. `ColdLoadPerfManualTests`
  7. `GitCliPriorityTests`
  8. `GitCliServicePoolTests`
  9. `GitLogStreamDisposeKillTests`
  10. `LogTrimmerTests`
  11. `LoggingSettingBindingTests`
  12. `LoggingSwitchTests`
  13. `PerfLogWriterTests`
  14. `ProcessKillerTests`
  15. `QueuedLineWriterTests`
  16. `RuntimeStatsSamplerSwitchTests`
- **Timeouts:** A `[Fact(Timeout = ...)]` guards against hangs only. Set it to at least 2x the test's duration under a full parallel run, not its duration alone.
- **Scratch Root & Fixture Lifecycle:** Disk fixtures live under a per-test-project scratch root inside the build output directory (`bin\<TestProject>\scratch`, or `HISTORIA_TEST_TMP` override). Each test process creates a per-run `historia-run-<pid>-*` folder deleted on process exit (`TestScratch.NewDirectory()`, `NewPath()`, `Delete()`). Run folders of finished test processes older than 30 minutes are purged on first use (`TestScratch.PruneStale()`); nothing else under the root is touched, and the system temporary directory is never pruned. `GIT_CEILING_DIRECTORIES` isolates test repositories from the enclosing repository, and `TestTempUsageGuardTests` guards against test fixtures created in the system temporary directory.
- **Performance & Timing Assertions:** Per Rule 20, performance tests record timing diagnostics to test output (`ITestOutputHelper`) rather than asserting hard wall-clock ratios (e.g. "must be 5x faster") that can flake under parallel I/O contention.

---

## 4. Codebase Organization

```
Historia/
├── Historia.slnx              # Main solution file
├── AGENTS.md                  # Shared instructions for coding agents
├── CLAUDE.md                  # Points Claude Code at AGENTS.md
├── Historia.App/              # Avalonia UI application project
├── Historia.Cmdr/             # historia-cmdr, the command-line client for agent control
├── Historia.Core/             # Portable domain models, services, and git gateway
├── Historia.ScenarioLab/      # Test scenario generator and runner
├── tests/Historia.Tests/      # Core test suite
├── tests/Historia.App.Tests/  # Headless Avalonia UI test suite
├── assets/                    # Application logos, splash art, and icons
├── dev/                       # Automation scripts and development tools
└── docs/                      # Project documentation and guidelines
    └── Historia-commander-skills/ # Sample skills for Claude Code and GitHub Copilot
```

---

## 5. Coding Standards & Best Practices

### C# Conventions
- Target modern C# (file-scoped namespaces, nullable reference types enabled, target-typed `new()`, pattern matching).
- Use `readonly record struct` or `record class` for immutable data transfer objects.
- Prefer `ValueTask` / `Task` for all async operations and pass `CancellationToken` throughout long-running operations.
- In hot paths (graph calculation, diff parsing), use `Span<T>`, `ReadOnlySpan<T>`, and `ArrayPool<T>` where appropriate to avoid heap allocations.
- **Global usings only.** Every `using` directive lives in the project's `GlobalUsings.cs` as a `global using` — plain namespace imports, `using static`, and alias directives (`using X = Y;`) alike. A `.cs` file contains no `using` directive of its own; `using` *statements* (`using var`, `using (...)`) are unaffected. `<ImplicitUsings>` already covers the BCL basics (`System`, `System.Linq`, `System.IO`, `System.Threading.Tasks`, …), so never re-add those. When a new dependency is introduced, add its `global using` to the relevant project's `GlobalUsings.cs`. When globalizing two namespaces makes a type name ambiguous (`CS0104`), resolve it with a `global using` alias pinned to the intended type (e.g. `global using Version = System.Version;`), or fully qualify at the one-off call site if only a single file wants the other type. This holds even for a namespace only one or two files use: the project prefers one uniform place for imports over making each file's dependencies visible in its own header.

### Avalonia UI Conventions
- Views are AXAML bound to view models in `Historia.Core/ViewModels`; custom-drawn surfaces (graph, terminal, diff and conflict editors) are custom controls. `MvvmViewGuardTests` enforces it.
- Never hard-code colors. `HistoriaPalette` (`Historia.Core/Services/Theming/`) is the single source of truth; bind through the generated `{DynamicResource ...}` keys so light/dark theming keeps working.
- Always use virtualization when rendering large collections such as commit lists or long diffs (see `CommitGraphControl`, which renders only the visible row range).

---

## 6. Working with Coding Agents

Agent-assisted contributions are welcome. This section is how the repository keeps them safe.

### Guardrails
- **The constitution is written to be followed mechanically.** [`docs/constitution.md`](constitution.md) states each invariant as a rule an agent can check its change against, and Rule 00 (never destroy the user's work without a confirmation they accept) outranks all of them.
- **Tests enforce what can regress silently.** For example `DestructiveConfirmationTests` (destructive actions go through `ConfirmDestructiveAsync`), `OfflineAssetTests` (no network URL in any `.cs` or `.axaml`), `PaletteParityTests` (one palette) and `ExternalControlReadOnlyTests` (agent-facing control code reaches no write path). A change that breaks one fails the build, whoever wrote it.
- **An agent that meets a rule it cannot satisfy stops and reports.** It must not route around the rule.

### Instruction files
[`AGENTS.md`](../AGENTS.md) is the single source of instructions for coding agents. `CLAUDE.md` and `.github/copilot-instructions.md` point to it. Change `AGENTS.md`, not the copies.

### Plans for multi-file work
Work that touches several files follows a phased plan:
- Read the current code before planning against it. The design documents describe intent; the working tree is what exists.
- Every change lands with acceptance criteria that state what "done" means in terms someone else can check.
- Phases run in sequence; the milestones inside a phase run in parallel.
- Parallel milestones own disjoint files, so each can run in its own git worktree.
- Each milestone lists what it owns, its steps, its tests, its acceptance criteria and its commit.
- Every gate is automated; manual checks are collected in a file for afterwards.
- A verification phase follows the implementation, and design documents are updated last.

### Reviewing agent output
Review it in Historia. With `historia-cmdr` an agent queues the exact commit, file diff, comparison or uncommitted change it produced for review in Historia's CMDR list, where you open it when ready; see [`docs/Historia-commander-skills/README.md`](Historia-commander-skills/README.md) for the sample skills. An agent can also stage its own files and propose the commit message, so you review the staged files and click Commit yourself. Read the diff, not the agent's summary or its message.

### Policy
- The contributor is responsible for every line, agent-written or not.
- An agent's change passes the same gates and review as any other.
- The pull request description says which parts were agent-written.
- A `Co-Authored-By` trailer for the agent is allowed, not required.

---

## 7. Git & Contribution Workflow

### 1. Branch Naming
Create a feature branch from `main` using the following prefixes:
- `feat/feature-name` — New features
- `fix/bug-description` — Bug fixes
- `perf/optimization-target` — Performance improvements
- `docs/topic-name` — Documentation changes
- `refactor/scope` — Code refactoring without behavioral changes

### 2. Commit Messages
I adhere to [Conventional Commits](https://www.conventionalcommits.org/):
```
<type>(<scope>): <short summary>

[optional body]

[optional footer(s)]
```
*Examples:*
- `feat(graph): implement dynamic lane allocation for merge commits`
- `fix(staging): resolve hunk stage offset calculation bug`
- `perf(diff): cache lexer tokens during side-by-side render`

### 3. Pull Request Guidelines
1. Ensure your changes build cleanly without warnings (`dotnet build Historia.slnx`).
2. Run unit and integration tests (`dev/test.ps1 -Tier fast` during development, `dev/test.ps1 -Tier all` before pushing).
3. Ensure formatting checks pass (`dotnet format whitespace Historia.slnx --verify-no-changes`).
4. For performance-sensitive code, include benchmark results or profiling notes in the PR description.
5. Keep PRs focused on a single logical change for ease of review.
6. If coding agents helped, say which parts in the description.

### 4. CI Checks
Every pull request and push to `main` triggers automated GitHub Actions workflows:
- **`ci.yml` (Test Suite Matrix):** Runs the full test suites (`Historia.Tests` and `Historia.App.Tests`) across `windows-latest`, `ubuntu-latest`, and `macos-latest` with `fail-fast: false`.
- **`lint.yml` (Code and Workflow Formatting):** Runs `dotnet format whitespace` and `dotnet format style` checks on Windows, `actionlint` in Docker on Ubuntu, and `gitleaks` secret analysis on Ubuntu.

To reproduce and verify these checks locally:
```powershell
# Format verification
dotnet format whitespace Historia.slnx --verify-no-changes
dotnet format style Historia.slnx --verify-no-changes --severity warn

# Configure git blame to ignore bulk reformatting commits
git config blame.ignoreRevsFile .git-blame-ignore-revs
```

### 5. Releases
Versions are automatically derived from git tags via MinVer (`MinVerTagPrefix=v`).
- Pushing a tag matching `v*` (such as `v0.1.0`) triggers `.github/workflows/package.yml`, which compiles release builds, packages archives and native installers for Windows (`win-x64`), computes SHA-256 hashes in `SHA256SUMS.txt`, and generates a draft GitHub Release for maintainer review.
- The packaging workflow may also be invoked manually (`workflow_dispatch`) with checkboxes to selectively build specific operating systems (`win-x64`, `osx-arm64`, `osx-x64`, `linux-x64`) or package types (archives or installers).
- When a new .NET 10 servicing release is published, bump the pinned Windows Desktop Runtime version, URL, and SHA-512 hash in `.github/workflows/package.yml`.

---

## 8. Reporting Issues & Requesting Features

- **Bug Reports:** Open an issue with clear reproduction steps, repo size/context, your Windows & .NET version, and any relevant error logs or screenshots.
- **Feature Proposals:** Start a discussion or open an issue outlining the user problem, proposed UX flow, and technical considerations.

---

## 9. License

Historia is released under the [MIT License](../LICENSE).

**Contributor terms.** You keep the copyright to your contributions. By submitting a contribution, you grant Kevin Chen and any successor maintainer of Historia a perpetual, worldwide, non-exclusive, royalty-free, irrevocable license to use, copy, modify, distribute, sublicense and relicense your contribution under any terms, and a matching patent license for any of your patents your contribution needs. You confirm the contribution is your own work, or that you have the right to submit it under these terms, including any permission your employer requires. Your contribution is also available to everyone under the project's MIT License.

Bundled third-party material is tracked in [`docs/third-party-licenses.md`](third-party-licenses.md).
The utility icon set is Material Symbols (Apache-2.0), vendored as SVGs under `assets/material-symbols/`
(Outlined style, 24px optical size, weight 400) and regenerated with `dev/vendor-material-symbols.ps1`.
Add glyphs from the pinned commit with the same style, then re-run `dev/vendor-material-symbols.ps1`; never
hand-draw or mix styles. Keep the licence entry in `docs/third-party-licenses.md` current.
