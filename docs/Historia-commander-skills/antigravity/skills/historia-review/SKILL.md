---
name: historia-review
description: Walk the user through a branch or change set one file at a time in Historia, queueing every stop in its CMDR list with a short note on what to look at. Use when the user asks for a guided review, a review tour, or to "walk me through" a diff or branch.
---

# Guided review in Historia

Take the user through a change set as a tour. You queue every stop in Historia's CMDR list, at the
top of its sidebar; the user clicks each one to see its diff while you say what to look at.

## Procedure

1. **Pick the base.** Use the base the user names; otherwise the branch's upstream, otherwise `main`.
2. **List the changes:** `git diff --name-status <base>...HEAD`. Find the fork point with
   `git merge-base <base> HEAD` and compare against it, so commits that landed on the base after the
   branch started do not show up as reversed changes.
3. **Order the files for reading:** interfaces and models first, then implementations, then call
   sites, then tests, then configuration and docs. Skip generated files and pure renames unless the
   user asks.
4. **Present the plan:** the ordered list, one line each saying why the file is where it is.
5. **Queue every stop up front, in reading order.** For each file, write a one-line note (at most 200
   characters) saying what to check, print the command in a fenced `bash` block, then run it:

   ```bash
   historia-cmdr compare <merge-base> HEAD <file> --line <first changed line> --note "<what to check>"
   ```

   The reply has `"queued": true`. With the default oldest-first order, the CMDR list shows the stops
   in reading order.
6. **Walk the stops in chat.** For each stop give a 2–4 line note: what changed, what to check,
   anything risky. Wait for the user before each next stop. They say "next", "back", "skip to N", or
   "done", and click the matching item in Historia.
7. **Collect their comments** as they make them. At the end, summarize them as a list of follow-ups.

## Rules

- Never edit files during the tour unless the user asks.
- Exit code 2 means Historia is unreachable, switched off, or too old to queue items and must be
  restarted: say so once and offer to continue in chat with `git diff` output instead.
- Queue only. Add `--now` (which switches Historia's view at once) only when the user asks to see a
  stop right away.
- The notes are your reading of the diff; tell the user to trust the diff in Historia over your summary.
