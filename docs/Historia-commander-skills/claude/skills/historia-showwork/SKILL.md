---
name: historia-showwork
description: When you finish a task that changed files and you did not commit, queue the uncommitted changes in Historia's CMDR list with a short note, and show the historia-cmdr command so the user can rerun it. Use at the end of any task that left uncommitted code changes.
allowed-tools: Bash(historia-cmdr *) Bash(git status *) Bash(git diff *)
---

# Queue your uncommitted work in Historia

Run this when the task is done, you changed files, and you did not commit. Historia does not switch
views: the change waits in the CMDR list at the top of its sidebar until the user clicks it.

## Procedure

1. **List the files this task changed**, from your own edits, cross-checked with
   `git status --porcelain`. Leave out changes the user already had. If none of yours remain, stop.
2. **Put them in `git status --porcelain` order.** Add `--staged` only when every one of those files'
   changes is entirely staged (a blank second status column). More than 200 files: take the first
   200 and say the rest were left out.
3. **Write the note:** one line, at most 200 characters, saying what to look at first.
4. **Show the command, then run it.** Print it in a fenced `bash` block so the user can rerun it:

   ```bash
   historia-cmdr changes <file> <file>... --note "<what to look at>"
   ```

   Then run it. The reply has `"queued": true`. Historia queues one item for all the files; clicking
   it opens the changes panel with the first file's diff open.
5. **Finish with the list** of files you changed, one per line, and say the change is waiting in
   Historia's CMDR list.

## Rules

- Exit 1: report the error from the JSON reply.
- Exit 2: Historia is unreachable, external control is switched off, or the running Historia is too
  old to queue items and must be restarted; say which, and point the user at the command above to
  rerun once Historia is up.
- Queue only. Add `--now` (which switches Historia's view at once) only when the user asks to see the
  change right away.
- Navigate only: never stage, unstage, commit or change anything.
