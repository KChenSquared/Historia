# Agent integration

`historia-cmdr` lets a coding agent queue the exact commit, file diff, comparison or uncommitted
change it just produced in Historia, so you review it in a real diff viewer instead of terminal
scrollback, when you are ready. It can also hand you a finished change: the agent stages its own
files with its own `git add`, then queues the staged change with a proposed commit message. When you
open it, Historia shows the staged files and offers the message to the commit box. You review the
files and click **Commit**.

By default nothing in Historia moves when an agent runs a command: the item waits in the **CMDR**
list at the top of the sidebar, with the agent's note, until you click it. `--now` switches the view
at once instead.

It navigates, proposes and queues, nothing else. It never stages, commits, or changes your
repository, index, refs, working tree or settings.

This folder holds sample skills for Claude Code, GitHub Copilot (VS Code) and Antigravity that teach
an agent when and how to call it.

## The command

`historia-cmdr.exe` is installed beside `Historia.exe`. Settings → General → **Add to PATH** puts it
on your user `PATH` (and moves it there when you run Historia from another folder); open a new
terminal afterwards so agents can run `historia-cmdr` from any repository.

| Command | What clicking the item (or `--now`) shows |
|---|---|
| `historia-cmdr ping` | Nothing; checks that Historia is running and control is enabled (never queued) |
| `historia-cmdr selection` | Nothing; reports the focused repository, selected commit and open diff (never queued) |
| `historia-cmdr open` | The repository, opened as a tab if it is not already |
| `historia-cmdr show <rev>` | The commit, selected and centred in the graph |
| `historia-cmdr diff <rev> <file> [--line N]` | One file's diff in a commit |
| `historia-cmdr compare <base> <head> <file> [--line N]` | One file between two revisions |
| `historia-cmdr changes <file>... [--staged] [--line N]` | The uncommitted (or staged) changes, with the first file's diff open |
| `historia-cmdr propose-commit (-m <text>... \| -F <file\|->)` | The staged change, with the message offered to the commit box |

Options for every navigation command (`open`, `show`, `diff`, `compare`, `changes`,
`propose-commit`):

- `--note <text>` — a short message shown on the queued item instead of the default text. One line
  (newlines and runs of spaces become one space), 1–200 characters, no control characters.
- `--now` — show it at once instead of queueing: Historia switches the view and comes to the front.
  `--note` cannot be combined with it.
- `-C <repo>` — the repository (default: the current directory).
- `--no-focus` — with `--now`, do not bring Historia's window to the front. Accepted, and ignored,
  when queueing.
- `--no-launch` — do not start Historia when it is not running. Without it, a queued or `--now`
  command starts Historia; `ping` and `selection` never do.

`changes` takes up to 200 files when queued, as one item; with `--now` it takes exactly one. `--line`
applies to the first file.

Before it queues anything, Historia checks the request without opening the repository: the folder
must be inside a work tree, revisions are resolved to full SHAs, paths must lie inside the
repository, and `propose-commit` needs something staged. A request that fails these checks queues
nothing.

It prints one JSON object. A queued request replies
`{"ok": true, "queued": true, "queueId": "…", "repo": "…", "commit": "…", "path": "…"}`, plus
`stagedPaths` for `propose-commit`; with `--now`, `"ok": true` means Historia is now showing it.
Exit codes:

| Code | Meaning |
|---|---|
| `0` | Done (queued, or shown with `--now`) |
| `1` | The request reached Historia and failed: `repo-not-found`, `rev-not-found`, `path-invalid`, `nothing-staged`, `timeout` or `internal` |
| `2` | Historia is unreachable, external control is switched off, or the running Historia is too old to queue items (it prints "The running Historia is too old to queue items; restart it.") |
| `64` | Usage error |

Scripts and installed skills that relied on the view switching as soon as they ran now queue
instead; add `--now` to keep that behaviour.

## The CMDR list

The top of Historia's sidebar, under the Branch button, has two halves: **Branches** (the branch
list) and **CMDR** (the queued items). Historia always starts on Branches. The circle on the CMDR half
counts the items you have not opened yet; it turns the primary colour while that count is above 0.

Each item shows the repository folder name, the time it was queued, and the agent's note. Without a
note it shows the file name for one file, the commit subject for `show`, the proposed subject line
for `propose-commit`, "N files to review" for several files, or "Open repository" for `open`.
Hovering shows the full text and the target.

- **Click an item** to open it: Historia switches to (or opens) that repository's tab and shows the
  commit, diff or change exactly as `--now` would, and dims the item as seen. The window does not
  come to the front. If the folder, commit or staged change is gone, a notice says so and the item
  stays.
- **The red trash button** removes an item at once, with no confirmation: it is a pointer an agent
  left, not your work, and removing it changes nothing in any repository.
- **The sort button** switches between oldest first (the default) and newest first; the choice is
  remembered.
- **Expiry.** When Historia starts it removes items older than Settings → Advanced → *Clear agent
  queue items older than (hours)* (default 5, 1 to 168). Nothing is removed while it runs. At most
  200 items are kept; the oldest go first.

## Hand a change to the user for committing

1. The agent lists the files **it** changed for this task and stages exactly those:
   `git add -- <path>...`. It never runs `git add -A`, `git add .` or `git commit`.
2. It writes the commit message in the repository's convention.
3. It pipes the message on standard input so no shell mangles quotes or newlines, with a short note:

   ```bash
   historia-cmdr propose-commit -F - --note "Adds the export button" <<'EOF'
   feat(scope): subject line

   Body paragraph.
   EOF
   ```

   In Windows PowerShell 5.1, set the output encoding first. It otherwise pipes text to native
   programs as US-ASCII and turns `é` into `?`:

   ```powershell
   $OutputEncoding = [System.Text.UTF8Encoding]::new($false)
   @'
   feat(scope): subject line

   Body paragraph.
   '@ | historia-cmdr propose-commit -F - --note "Adds the export button"
   ```

4. The proposal waits in the CMDR list. When you click it, Historia opens the staging panel with the
   first staged file's diff open and offers the message to the commit box.

The queued reply lists `stagedPaths` (what was staged when it was queued), so the agent can check
them against what it meant to stage. It has no `draft` field: the message reaches the commit box
only when you click the item, and Historia staged nothing in between. If the staged files changed
since, the click proposes against what is staged then.

When the message is offered (on the click, or at once with `--now`, whose reply carries `draft`):

- `"draft": "filled"` — the commit box was empty (or held the same text) and now holds the message.
  A note under the box says a coding agent proposed it.
- `"draft": "pending"` — you already have a different draft, or the box is busy. Your draft is left
  alone and a bar above the box reads "A coding agent proposed a commit message." with **Use it** and
  **Dismiss**. **Use it** asks before replacing your draft.

`nothing-staged` (exit 1) means the index was empty; nothing is queued.

Historia never stages or commits for the agent. Files you staged before the agent ran are part of
the commit too; the staging panel shows them, and `historia-propose-commit` reports them rather
than unstaging them. Read the diff, not the agent's message.

## Installing the samples

Each agent gets the same set of skills:

| Skill | Use | Samples |
|---|---|---|
| `historia-showwork` | At the end of a task, queue the uncommitted changes as one item | [Claude](claude/skills/historia-showwork/SKILL.md) · [Copilot](copilot/skills/historia-showwork/SKILL.md) · [Antigravity](antigravity/skills/historia-showwork/SKILL.md) |
| `historia-review` | A guided review tour of a branch: every stop queued up front, in reading order | [Claude](claude/skills/historia-review/SKILL.md) · [Copilot](copilot/skills/historia-review/SKILL.md) · [Antigravity](antigravity/skills/historia-review/SKILL.md) |
| `historia-trace` | "Where did this line come from?" Queues each commit's diff at that line | [Claude](claude/skills/historia-trace/SKILL.md) · [Copilot](copilot/skills/historia-trace/SKILL.md) · [Antigravity](antigravity/skills/historia-trace/SKILL.md) |
| `historia-propose-commit` | Stage the agent's files and queue them with a proposed commit message | [Claude](claude/skills/historia-propose-commit/SKILL.md) · [Copilot](copilot/skills/historia-propose-commit/SKILL.md) · [Antigravity](antigravity/skills/historia-propose-commit/SKILL.md) |
| `historia-commit` | After the agent commits, queue each commit, oldest first | [Claude](claude/skills/historia-commit/SKILL.md) · [Copilot](copilot/skills/historia-commit/SKILL.md) · [Antigravity](antigravity/skills/historia-commit/SKILL.md) |

Every skill queues with a `--note` and never switches your view; each shows its `historia-cmdr`
commands in a code block so you can rerun them, and uses `--now` only when you ask to see something
right away.

- **Claude Code** reads skills from `~/.claude/skills/<name>/` (all projects) or
  `<repo>/.claude/skills/<name>/` (one repository). Copy the folders from
  [`claude/skills/`](claude/skills).
- **GitHub Copilot** in VS Code (agent mode) reads skills from `<repo>/.github/skills/<name>/` or
  `~/.copilot/skills/<name>/`. Copy the folders from [`copilot/skills/`](copilot/skills), and paste
  [`copilot-instructions.snippet.md`](copilot/copilot-instructions.snippet.md) into
  `<repo>/.github/copilot-instructions.md`.
- **Antigravity** reads skills from `<repo>/.agents/skills/<name>/` (one workspace) or
  `~/.gemini/config/skills/<name>/` (all workspaces). Copy the folders from
  [`antigravity/skills/`](antigravity/skills).

Agent file formats change; if a sample stops loading, check the current
[Claude Code skills](https://code.claude.com/docs/en/skills) and
[VS Code agent skills](https://code.visualstudio.com/docs/copilot/customization/agent-skills) and
[Antigravity skills](https://antigravity.google/docs/skills) documentation.

## Skipping the approval prompts

Agents ask before running shell commands. `historia-cmdr` only navigates, proposes and queues, so it
is reasonable to pre-approve it.

- **Claude Code:** add the permission rule `Bash(historia-cmdr *)` to `.claude/settings.json` (or your
  user settings).
- **GitHub Copilot:** add it to the `chat.tools.terminal.autoApprove` setting:
  `"chat.tools.terminal.autoApprove": { "historia-cmdr": true }`.

Do not pre-approve `git add` or `git commit` on the agent's behalf unless you mean to.

## Security

- **Local and per-user.** Requests travel over a named pipe that only your own account can open. No
  TCP port, no URI scheme, no network call.
- **Navigate, propose and queue only.** A commit message is offered to an editable draft you must
  still act on; it never replaces a draft you typed without asking, and nothing commits until you
  click **Commit**. A guard test fails the build if the control code gains any call that changes the
  repository, index, refs or settings.
- **Queueing is add-only.** A queued request is checked with read-only git commands and added to
  Historia's own CMDR list (not your settings). It opens no tab, fetches nothing, runs no write and
  does not bring the window forward; the control code can add items but cannot mark, reorder or
  remove them. Only your click navigates.
- **Opening a repository.** When you click an item (or an agent passes `--now`), Historia opens a
  repository it was not already showing, exactly as if you had opened it by hand: it joins the saved session and the Recent list, and, with auto-fetch on, it
  is fetched. Opening a repository runs git against it, which a hostile repository's configuration
  can abuse; an agent that was tricked by untrusted content could aim Historia at one. Turn the
  feature off if that worries you.
- **Input is validated.** Revisions and paths are checked before they reach a git argument list.
- **Turn it off.** Settings → Privacy & Network → *Allow command-line control (historia-cmdr)*. When it
  is off, commands fail with `"error":"disabled"` (exit 2) and nothing is queued; items already in the
  CMDR list stay, and clicking one is your own navigation.
- `HISTORIA_CONTROL_PIPE` overrides the pipe name; it exists for tests.
