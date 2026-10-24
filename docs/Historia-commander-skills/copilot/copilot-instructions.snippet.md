## Reviewing your changes in Historia

Historia is the user's Git GUI, and `historia-cmdr` queues a change in its CMDR list for the user to
open when ready; it does not switch the user's view. Pass a one-line `--note` (at most 200
characters) saying what to look at. When you finish a task that left uncommitted changes, use the
`historia-showwork` skill (`historia-cmdr changes <file>... --note "…"`); after you commit, use the
`historia-commit` skill (`historia-cmdr show <sha> --note "…"`). When the user wants to commit
themselves, stage only the files you changed and propose the message with the
`historia-propose-commit` skill (`historia-cmdr propose-commit -F - --note "…"`). Use `--now` only
when the user asks to see something right away. `historia-cmdr` only navigates, proposes and queues:
it never stages or commits, and you must not run `git add -A`, `git add .` or `git commit` for this.
Do not queue the same change repeatedly; if it exits with code 2, Historia is not available (or is
too old to queue items and needs a restart), so say so and carry on without it.
