"""Syncs docs/release-notes/<tag>.md files to the bodies of the matching GitHub releases.

For every notes file that was added or modified (or every notes file on a full resync), the body
of the release with the same tag is replaced by the file text followed by GitHub's generated
changelog. Tags without a release are skipped and deleted files are ignored.
"""

import argparse
import os
import re
import shutil
import subprocess
import sys
import tempfile

NOTES_DIR = "docs/release-notes"
NOTES_PATTERN = re.compile(r"^" + re.escape(NOTES_DIR) + r"/(v[^/]*)\.md$")
ZERO_OID = re.compile(r"^0+$")


def _tag_of(path):
    match = NOTES_PATTERN.match(path.strip())
    return match.group(1) if match else None


def _status_rows(diff_name_status):
    for line in diff_name_status.splitlines():
        parts = line.rstrip("\r").split("\t")
        if len(parts) >= 2 and parts[0]:
            yield parts[0][0], parts[-1]


def _unique(items):
    return list(dict.fromkeys(items))


def changed_tags(diff_name_status):
    """Tags of notes files that were added or modified in `git diff --name-status` output."""
    return _unique(
        tag
        for status, path in _status_rows(diff_name_status)
        if status in ("A", "M") and (tag := _tag_of(path))
    )


def deleted_tags(diff_name_status):
    """Tags of notes files that were deleted in `git diff --name-status` output."""
    return _unique(
        tag
        for status, path in _status_rows(diff_name_status)
        if status == "D" and (tag := _tag_of(path))
    )


def all_tags(paths):
    """Tags of every notes file among `paths`."""
    return _unique(tag for path in paths if (tag := _tag_of(path)))


def compose_body(notes, generated):
    """The release body: the notes text, a blank line, then the generated changelog."""
    notes = notes.replace("\r\n", "\n").strip()
    generated = generated.replace("\r\n", "\n").strip()
    if not generated:
        return notes + "\n"
    return notes + "\n\n" + generated + "\n"


def is_zero_oid(oid):
    return bool(ZERO_OID.match(oid.strip()))


def _run(args, cwd=None):
    return subprocess.run(args, cwd=cwd, capture_output=True, text=True, encoding="utf-8")


def _gh_executable():
    path = shutil.which("gh")
    if path is None:
        raise RuntimeError("gh was not found on PATH")
    return path


def previous_tag(tag, cwd=None):
    """The nearest v* tag reachable from the parent of `tag`, or None when there is none."""
    result = _run(
        ["git", "describe", "--tags", "--abbrev=0", "--match", "v*", f"{tag}^"], cwd=cwd
    )
    if result.returncode != 0:
        return None
    return result.stdout.strip() or None


def tracked_notes_tags(cwd=None):
    """Tags of every notes file present in the checked-out tree."""
    directory = os.path.join(cwd or ".", *NOTES_DIR.split("/"))
    if not os.path.isdir(directory):
        return []
    names = sorted(os.listdir(directory))
    return all_tags(f"{NOTES_DIR}/{name}" for name in names)


def diff_name_status(before, after, cwd=None):
    result = _run(
        ["git", "diff", "--name-status", "--no-renames", before, after, "--", NOTES_DIR + "/"],
        cwd=cwd,
    )
    if result.returncode != 0:
        raise RuntimeError(f"git diff {before} {after} failed: {result.stderr.strip()}")
    return result.stdout


def release_exists(gh, repo, tag):
    """True when a release (draft or published) exists for the tag; raises on other failures."""
    result = _run([gh, "release", "view", tag, "--repo", repo])
    if result.returncode == 0:
        return True
    if "not found" in result.stderr.lower():
        return False
    raise RuntimeError(f"gh release view {tag} failed: {result.stderr.strip()}")


def generated_notes(gh, repo, tag, previous):
    args = [
        gh,
        "api",
        f"repos/{repo}/releases/generate-notes",
        "-f",
        f"tag_name={tag}",
    ]
    if previous:
        args += ["-f", f"previous_tag_name={previous}"]
    args += ["--jq", ".body"]
    result = _run(args)
    if result.returncode != 0:
        raise RuntimeError(f"generating notes for {tag} failed: {result.stderr.strip()}")
    return result.stdout


def edit_release(gh, repo, tag, body):
    handle, path = tempfile.mkstemp(prefix="release-notes-", suffix=".md")
    try:
        with os.fdopen(handle, "w", encoding="utf-8", newline="\n") as f:
            f.write(body)
        result = _run([gh, "release", "edit", tag, "--repo", repo, "--notes-file", path])
    finally:
        os.remove(path)
    if result.returncode != 0:
        raise RuntimeError(f"gh release edit {tag} failed: {result.stderr.strip()}")


def sync_tag(gh, repo, tag, cwd=None):
    """Updates one release. Returns the status line to print."""
    if not release_exists(gh, repo, tag):
        return f"{tag}: skipped: no release"
    notes_path = os.path.join(cwd or ".", *NOTES_DIR.split("/"), f"{tag}.md")
    with open(notes_path, encoding="utf-8") as f:
        notes = f.read()
    generated = generated_notes(gh, repo, tag, previous_tag(tag, cwd))
    edit_release(gh, repo, tag, compose_body(notes, generated))
    return f"{tag}: updated"


def parse_args(argv):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", default=os.environ.get("GITHUB_REPOSITORY", ""))
    parser.add_argument("--before", default="")
    parser.add_argument("--after", default="")
    parser.add_argument("--resync-all", default="false")
    return parser.parse_args(argv)


def main(argv=None):
    args = parse_args(sys.argv[1:] if argv is None else argv)
    if not args.repo:
        print("error: --repo or GITHUB_REPOSITORY is required", file=sys.stderr)
        return 2

    resync_all = args.resync_all.strip().lower() == "true"
    before = args.before.strip()
    after = args.after.strip()
    deleted = []

    if resync_all or (before and is_zero_oid(before)):
        tags = tracked_notes_tags()
    elif before and after:
        status = diff_name_status(before, after)
        tags = changed_tags(status)
        deleted = deleted_tags(status)
    else:
        print("nothing to sync: no commit range and no full resync requested")
        return 0

    for tag in deleted:
        print(f"{tag}: skipped: deleted")

    gh = _gh_executable()
    failed = False
    for tag in tags:
        try:
            print(sync_tag(gh, args.repo, tag))
        except (RuntimeError, OSError) as error:
            failed = True
            print(f"{tag}: failed: {error}", file=sys.stderr)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
