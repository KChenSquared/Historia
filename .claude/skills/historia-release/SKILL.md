---
name: historia-release
description: Prepare a Historia release — ask for the version, check the tag, draft concise release notes from the commits since the previous release, get the user's approval, write and commit docs/release-notes/<tag>.md, then create the annotated tag on that commit. Never pushes. Use when the user says "release", "cut a release", "make release notes", "tag version X", or names a version to ship.
allowed-tools: Bash(git *) Bash(historia-cmdr *)
---

# Historia release

Prepares a release locally: a release notes file and the tag that the `Package` workflow builds.
The workflow starts the draft release body from `docs/release-notes/<tag>.md` when the tagged
commit contains it (without one the body is blank), so the notes are committed first and the tag
goes on that commit. This skill never pushes; the user pushes the tag.

## 1. Version

Ask the user for the version unless they already gave it. Normalise it to a tag: `v` plus
`<major>.<minor>.<patch>`, optionally with a pre-release suffix (`v0.10.1`, `v0.11.0-rc.1`). Refuse
anything that does not match `^v\d+\.\d+\.\d+([-+][0-9A-Za-z.+-]+)?$`.

The notes file is `docs/release-notes/<tag>.md`.

## 2. Preflight

```bash
git rev-parse --abbrev-ref HEAD
git status --porcelain
```

- Stop if a merge, rebase, cherry-pick or revert is in progress, or HEAD is detached.
- Stop if the working tree is not clean; ask the user to commit or stash first.
- Remember whether the current branch is `main`.

## 3. Tag check

```bash
git rev-parse --verify --quiet "refs/tags/<tag>^{commit}"
git ls-remote --tags origin "refs/tags/<tag>"
git rev-parse HEAD
git cat-file -e "HEAD:docs/release-notes/<tag>.md"
```

- **Tag absent locally and on `origin`:** continue.
- **Tag on HEAD and HEAD contains the notes file:** the release is already prepared. Say so, show
  the push command from step 8, and stop. Change nothing.
- **Tag exists anywhere else** (on another commit, on HEAD without the notes file, or only on
  `origin`): stop. Tell the user which commit the tag points at, local and remote, and that the
  existing tag must be deleted before this skill can continue:

  ```bash
  git push --delete origin <tag>
  git tag -d <tag>
  ```

  Do not delete or move the tag yourself unless the user tells you to in this conversation.

## 4. Previous release

The previous release is the newest `v*` tag reachable from HEAD that is not the new tag:

```bash
git describe --tags --abbrev=0 --match "v*" --exclude "<tag>" HEAD
```

If there is none, use the root commit and say so. Tell the user the range you are reviewing.

If HEAD already contains `docs/release-notes/<tag>.md` (for example, notes merged from a branch
earlier), show the file and ask whether to keep it as is (skip to step 7) or redraft it.

## 5. Review the commits

```bash
git log --no-merges --format="%h %s" <previous>..HEAD
```

Read every subject. Where a subject is unclear, read the body (`git show --stat <sha>`), and the
diff if needed, before deciding what it is. Then group the commits into user-visible changes.

Rules for the list:

- **Never list documentation changes.** Skip `docs:` commits and any commit that only touches
  Markdown, the design docs or comments.
- Skip changes users cannot see: `test`, `ci`, `refactor`, `chore`, `style`, review-finding
  follow-ups and build plumbing, unless they change what ships (new packages or platforms, a
  bundled tool, a startup or speed improvement users will notice).
- One bullet per major change or major fix. Merge related commits (a feature and its follow-up
  fixes) into one bullet. Lead with what the user can now do or what no longer goes wrong, in
  plain words, not commit-style prefixes or scopes.
- Keep it concise: usually 5 to 12 bullets, one line each.
- Minor fixes are not listed one by one. They are covered by a final bullet that reads exactly
  `Various bug fixes`.
- No commit hashes, pull request numbers or changelog: GitHub appends its generated changelog to
  the draft release.

## 6. Approval

Show the user the complete file you intend to write, in this shape (from
`docs/release-notes/TEMPLATE.md`; keep the supported-packages line):

```markdown
# Historia <tag>

Only the Windows packages are supported. The MacOSX and Linux packages are experimental.

## Changes

- <major change or fix>
- <major change or fix>
- Various bug fixes
```

Ask the user to approve it or say what to change. Revise and show it again until they approve.
Do not create the file before they approve.

## 7. Write and commit

Write `docs/release-notes/<tag>.md` with the approved text (LF line endings, trailing newline).

```bash
git add -- docs/release-notes/<tag>.md
git commit -m "docs(release): add <tag> release notes"
```

Use the commit attribution the session asks for. Stage only the notes file.

**Not on `main`:** do not tag. The tag belongs on the commit that lands on `main`. Give the user
the commands to push the branch and the link to open a pull request, built from the `origin`
remote's owner and repository (an SSH host alias such as `git@github-alias:Owner/Repo.git` still
names `Owner/Repo`):

```bash
git push -u origin <branch>
```

`https://github.com/<owner>/<repo>/compare/main...<branch>?expand=1`

Tell them to run this skill again on `main` after the pull request merges, to create the tag.
Then stop.

## 8. Tag (on `main` only)

Create an annotated tag on the commit that contains the notes:

```bash
git cat-file -e "HEAD:docs/release-notes/<tag>.md"
git tag -a <tag> -m "Historia <tag>"
```

Then queue the commit for review and tell the user how to publish. Do not push anything:

```bash
historia-cmdr show HEAD --note "<tag> release notes; tag <tag> is on this commit"
```

```bash
git push origin main
```

```bash
git push origin refs/tags/<tag>
```

Pushing the tag starts the `Package` workflow, which builds the packages and creates a draft
release from the notes file. Later edits to the notes file on `main` resync the release body.
