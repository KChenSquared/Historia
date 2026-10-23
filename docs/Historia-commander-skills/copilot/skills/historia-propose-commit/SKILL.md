---
name: historia-propose-commit
description: Stage only the files this task changed and queue them in Historia's CMDR list with a proposed commit message, so the user reviews the staged files and clicks Commit themselves. Use when the user says "stage it for me", "let me commit it" or "hand it to Historia", or when you finish a task and the user prefers to commit themselves.
---

# Hand the change to the user for committing

You stage; the user commits. Historia never stages or commits for you, and it does not switch views:
the proposal waits in the CMDR list at the top of its sidebar until the user clicks it.

## Procedure

1. **List the files this task changed**, from your own edits, cross-checked with
   `git status --porcelain`. Never include changes the user already had.
2. **Stage exactly those files:** `git add -- <path>...` with explicit paths.
   - Never run `git add -A`, `git add .` or `git commit`.
   - Never run `git reset` or anything else that writes refs or rewrites the index beyond those paths.
   - If files were already staged before the task, say so; do not unstage them.
3. **Write the message in the repository's convention.** Read `git log --oneline -10` first. Subject
   at most 72 characters, body wrapped, no trailers unless the repository uses them.
4. **Write the note:** one line, at most 200 characters, saying what the change is or what to check.
5. **Pipe the message on standard input** so no shell mangles quotes or newlines. Never put a long
   message on the command line with `-m`.

   bash, with a quoted heredoc:

   ```bash
   historia-cmdr propose-commit -F - --note "<what to check>" <<'EOF'
   feat(scope): subject line

   Body paragraph.
   EOF
   ```

   PowerShell: set the encoding first. Windows PowerShell 5.1 pipes to native programs as US-ASCII and
   turns `é` into `?`, and `[Text.Encoding]::UTF8` adds a byte-order mark.

   ```powershell
   $OutputEncoding = [System.Text.UTF8Encoding]::new($false)
   @'
   feat(scope): subject line

   Body paragraph.
   '@ | historia-cmdr propose-commit -F - --note "<what to check>"
   ```

6. **Show the command, then run it.** Print the exact command you run, message included, in a fenced
   block tagged with its shell (`bash` or `powershell`) so the user can rerun it, then run it.
7. **Read the JSON reply.**
   - `"queued": true`: the proposal is in the CMDR list. A queued reply has no `draft` field; the
     message is offered to the commit box only when the user clicks the item, which opens the staging
     panel. If the user already has a different draft then, Historia keeps it and offers the message
     in a bar above the box.
   - Compare `stagedPaths` with what you meant to stage and report any difference.
   - `nothing-staged` (exit 1): say so and stop.
   - Exit 2: Historia is unreachable, external control is switched off, or the running Historia is
     too old to queue items and must be restarted; say which, and point the user at the command above
     to rerun once Historia is up.
8. **Finish with one line:** the files are staged and the proposal is waiting in Historia's CMDR list;
   click it, review the staged files and click Commit.

## Rules

- Queue only. Add `--now` (which opens the staging panel at once) only when the user asks to see it
  right away.
- Never commit on the user's behalf in this flow.
- Tell the user to read the staged diff, not your summary of it.
