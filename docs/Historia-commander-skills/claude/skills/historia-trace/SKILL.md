---
name: historia-trace
description: Find which commit introduced a line of code and queue that change in Historia's CMDR list at that line. Use when the user asks where a line came from, who changed it, or why it is the way it is.
allowed-tools: Bash(historia-cmdr *) Bash(git blame *) Bash(git log *) Bash(git show *)
---

# Trace a line back to its commit

Historia does not switch views: each commit's diff waits in the CMDR list at the top of its sidebar
until the user clicks it.

1. **Find the commit.** For one line: `git blame -L <n>,<n> --porcelain -- <file>`. The first line of
   the output is `<sha> <original line> <final line>`. For a range:
   `git blame -L <start>,<end> --porcelain -- <file>`, and collect the distinct commits.
   If the file was renamed since, use the path shown in the blame output (`filename`).
2. **Build the command** for each commit, at the line as it was in that commit, with a one-line note
   (at most 200 characters) naming the lines it owns:
   `historia-cmdr diff <sha> <file> --line <original line> --note "<line(s) it owns>"`.
3. **Queue each commit.** Print each command in a fenced `bash` block so the user can rerun it, then
   run it. The reply has `"queued": true`.
4. **More than one commit: list them.** For each commit give the short SHA, subject, author, date and
   the lines it owns, and say they are waiting in Historia's CMDR list.
5. **Explain briefly:** the commit subject, author and date, and one or two sentences on why the
   change was made, from its message. Say when the message does not explain it.

## Rules

- A boundary commit (the porcelain output has a `boundary` line, or the history is shallow) is the oldest the repository
  knows about; say so rather than guessing.
- Exit code 2 means Historia is unreachable, switched off, or too old to queue items and must be
  restarted: give the commit and message in chat, and keep the command shown so the user can run it
  once Historia is up.
- Queue only. Add `--now` (which switches Historia's view at once) only when the user asks to see the
  change right away.
- Read-only: blame, log and show only.
