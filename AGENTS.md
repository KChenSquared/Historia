# AGENTS.md

Instructions for coding agents working in this repository (Historia, a Git client built with C# and
Avalonia). This file is the single source; `CLAUDE.md` and `.github/copilot-instructions.md` point here.

## Read first

- [Historia Constitution](docs/constitution.md) — the invariants. Rule 00 (confirm before destroying
  anything) outranks every other rule, including performance. If a rule blocks you, stop and report;
  do not route around it.
- [Comprehensive Design Documentation](Historia-DevDocs/ComprehensiveDesign/README.md) — the living
  spec. Read the relevant section before changing architecture.
- [Contributing Guide](docs/contributing.md) — setup, coding standards, PR guidelines.

## Build and test

```powershell
dotnet build Historia.App                     # the app; required after App changes
dotnet test tests/Historia.Tests              # Core tests; works while Historia is running
dotnet test tests/Historia.App.Tests          # headless UI tests; close Historia if the build cannot copy its exe
dotnet format whitespace Historia.slnx        # formatting check; required before committing
.\dev\test.ps1 -Tier fast                     # also: git, ui, slow, all
```

## Rules agents trip over most

One line each; the constitution has the full text.

- Destructive actions confirm through `IDialogService.ConfirmDestructiveAsync` (Rule 00).
- Offline first: no network URL in any `.cs` or `.axaml`, no unrequested network call (Rule 0).
- Git access is the CLI only, with arguments in `ArgumentList`, `--` before paths (Rules 1 and 4).
- Never block the UI thread; mutate off it and raise events on it (Rules 13 and 17).
- Global usings only: every `using` directive lives in the project's `GlobalUsings.cs` (see
  [Coding standards](docs/contributing.md#5-coding-standards--best-practices)).
- Comments describe the present: no transition comments and no references to documents or rule
  numbers (Rule 26).
- Views are AXAML bound to a view model in `Historia.Core/ViewModels`; no code-built views, no
  `new Window`, no `Rebuild*` methods (Rule 21).
- Agent-facing surfaces navigate, propose and queue only; they never stage, commit or change
  anything (Rule 27).

## Plans

Work that touches several files follows the phased plan format: sequential phases, parallel
milestones that own disjoint files, automated gates, and design docs updated last. See
[Working with coding agents](docs/contributing.md#6-working-with-coding-agents).

## Commits

Conventional Commits, subject under 72 characters, one commit per milestone. Local sessions never
push; the maintainer pushes.

Cloud sessions (`CLAUDE_CODE_REMOTE=true`) may push, but only to a new branch they create for their
own work. Never push to, merge into, rebase or force-push the default branch (`main`), and never
merge a pull request or enable auto-merge. Opening a pull request from that branch is fine; the
maintainer merges.

## Reviewing your work

After a change, queue it in Historia's CMDR list with a one-line note on what to look at:
`historia-cmdr show HEAD --note "<what to check>"` for a commit, or
`historia-cmdr changes <file>... --note "<what to check>"` for uncommitted changes. Queueing does not
switch the maintainer's view; use `--now` only when asked to show it right away. When the maintainer
wants to commit it themselves, stage only the files you changed with explicit paths and run
`historia-cmdr propose-commit -F - --note "<what to check>"` with the message on standard input
instead of committing. See
[`docs/Historia-commander-skills/`](docs/Historia-commander-skills/README.md).
