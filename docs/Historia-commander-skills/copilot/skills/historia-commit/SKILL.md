---
name: historia-commit
description: After you make one or more commits, queue each in Historia's CMDR list with a short note and show the historia-cmdr command for each so the user can rerun it. Use right after you commit, whenever the user asked you to commit.
---

# Queue your commits in Historia

Run this after you commit. Commit only when the user asked you to. Historia does not switch views:
each commit waits in the CMDR list at the top of its sidebar until the user clicks it.

## Procedure

1. **Wait until you are done committing.** When one request makes several commits in a row, such as
   one per milestone, run this once after the last of them, not after each.
2. **List the commits you made**, oldest first: `git log --reverse --format='%h %s' <before>..HEAD`,
   where `<before>` is the commit `HEAD` pointed at before you started. If you did not note it,
   list the SHAs your `git commit` calls printed instead.
3. **Queue each commit, oldest first.** For each, write a one-line note (at most 200 characters)
   saying why the commit exists or what to check, print the command in a fenced `bash` block so the
   user can rerun it, then run it:

   ```bash
   historia-cmdr show <sha> --note "<why>"
   ```

   The reply has `"queued": true`. Clicking the item selects the commit and centres it in the graph.
4. **Finish with the list:** each commit's short SHA and subject, and say they are waiting in
   Historia's CMDR list.

## Rules

- Exit 1: report the error from the JSON reply.
- Exit 2: Historia is unreachable, external control is switched off, or the running Historia is too
  old to queue items and must be restarted; say which, and point the user at the commands above to
  rerun once Historia is up.
- Queue only. Add `--now` (which switches Historia's view at once) only when the user asks to see a
  commit right away.
- Navigate only: `historia-cmdr` never stages, commits or changes anything, and this skill never
  pushes.
