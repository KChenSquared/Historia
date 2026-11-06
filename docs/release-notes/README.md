# Release notes

A release of Historia can have one notes file in this folder. The file name is the tag:
`docs/release-notes/<tag>.md`, where the tag is `v` plus the MinVer version (for example
`docs/release-notes/v0.11.0.md` for tag `v0.11.0`).

## How the files are used

- **The file is optional.** The `Package` workflow creates the draft GitHub release, named after
  the tag, whether or not the tagged commit contains a notes file.
- **The draft release starts from the file.** When the tagged commit contains the file, the draft
  release body starts with its text. Without one, the body is blank for the maintainer to fill in.
- **GitHub's generated changelog is appended automatically** below the file's text. Do not paste a
  changelog or a list of pull requests into the file.
- **Edits on `main` resync the release.** When a change to `docs/release-notes/v*.md` reaches
  `main`, the `Release notes sync` workflow rewrites the body of the matching release, draft or
  published, and keeps the generated changelog below it. A tag with no release is skipped, and
  deleting a file changes nothing. Edits made to a release body in the GitHub UI are overwritten by
  the next sync of that file; the file is the source.

## Writing a notes file

1. Copy [`TEMPLATE.md`](TEMPLATE.md) to `docs/release-notes/<tag>.md`.
2. Replace `vX.Y.Z` in the heading with the tag, fill in the sections and delete the empty ones.
3. Keep the line about supported packages.
4. Merge the file to `main` before tagging so the draft release starts from it, or after the
   release exists so the sync rewrites its body.

This README and `TEMPLATE.md` do not match `v*.md`, so no check or workflow treats them as notes.
