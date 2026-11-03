import contextlib
import io
import json
import os
import shutil
import stat
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

import release_notes
from release_notes import all_tags, changed_tags, compose_body, deleted_tags

STUB = '''import json
import os
import sys

stub_dir = os.environ["GH_STUB_DIR"]
with open(os.path.join(stub_dir, "config.json"), encoding="utf-8") as f:
    config = json.load(f)

args = sys.argv[1:]
call = {"args": args}
exit_code = 0

if args[:2] == ["release", "view"]:
    if args[2] not in config["releases"]:
        sys.stderr.write("release not found\\n")
        exit_code = 1
elif args[:1] == ["api"]:
    sys.stdout.write(config["generated"])
elif args[:2] == ["release", "edit"]:
    notes_file = args[args.index("--notes-file") + 1]
    with open(notes_file, encoding="utf-8", newline="") as f:
        call["body"] = f.read()
    if config.get("fail_edit"):
        sys.stderr.write("edit refused\\n")
        exit_code = 1

with open(os.path.join(stub_dir, "calls.jsonl"), "a", encoding="utf-8") as f:
    f.write(json.dumps(call) + "\\n")
sys.exit(exit_code)
'''

DIFF = "\n".join(
    [
        "A\tdocs/release-notes/v0.3.0.md",
        "M\tdocs/release-notes/v0.2.0.md",
        "D\tdocs/release-notes/v0.1.0.md",
        "M\tdocs/release-notes/README.md",
        "A\tdocs/release-notes/TEMPLATE.md",
        "M\tdocs/release-notes/sub/v9.9.9.md",
        "M\tdocs/other/v1.0.0.md",
    ]
)


class TestPureFunctions(unittest.TestCase):
    def test_added_and_modified_kept(self):
        self.assertEqual(changed_tags(DIFF), ["v0.3.0", "v0.2.0"])

    def test_deleted_ignored(self):
        self.assertNotIn("v0.1.0", changed_tags(DIFF))
        self.assertEqual(deleted_tags(DIFF), ["v0.1.0"])

    def test_readme_and_template_ignored(self):
        paths = [
            "docs/release-notes/README.md",
            "docs/release-notes/TEMPLATE.md",
            "docs/release-notes/v1.0.0.md",
            "docs/release-notes/v1.0.1-rc.1.md",
        ]
        self.assertEqual(all_tags(paths), ["v1.0.0", "v1.0.1-rc.1"])
        self.assertEqual(changed_tags(DIFF), ["v0.3.0", "v0.2.0"])

    def test_zero_before_means_all(self):
        self.assertTrue(release_notes.is_zero_oid("0" * 40))
        self.assertTrue(release_notes.is_zero_oid("0" * 64))
        self.assertFalse(release_notes.is_zero_oid("0" * 39 + "1"))

        with tempfile.TemporaryDirectory() as root:
            notes = os.path.join(root, "docs", "release-notes")
            os.makedirs(notes)
            for name in ("v0.1.0.md", "v0.2.0.md", "README.md", "TEMPLATE.md"):
                with open(os.path.join(notes, name), "w", encoding="utf-8") as f:
                    f.write("x\n")
            self.assertEqual(release_notes.tracked_notes_tags(root), ["v0.1.0", "v0.2.0"])

    def test_compose_body_appends_generated_after_blank_line(self):
        body = compose_body("# Historia v1\r\n\r\nText.\r\n\r\n", "\n## What's Changed\n* a\n")

        self.assertEqual(body, "# Historia v1\n\nText.\n\n## What's Changed\n* a\n")

    def test_compose_body_without_generated_text(self):
        self.assertEqual(compose_body("# Historia v1\n\n", "  \n"), "# Historia v1\n")


class RepoTestCase(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp(prefix="test_release_notes_")
        self.work = os.path.join(self.root, "work")
        os.makedirs(self.work)
        self.environment = {
            "GIT_CONFIG_GLOBAL": os.devnull,
            "GIT_CONFIG_NOSYSTEM": "1",
        }
        patcher = mock.patch.dict(os.environ, self.environment)
        patcher.start()
        self.addCleanup(patcher.stop)
        self.original_cwd = os.getcwd()
        self.addCleanup(os.chdir, self.original_cwd)

        self.git("init", "--quiet", "--initial-branch=main")
        self.git("config", "user.name", "Test")
        self.git("config", "user.email", "test@example.com")
        self.git("config", "commit.gpgsign", "false")
        self.git("config", "tag.gpgsign", "false")

    def tearDown(self):
        def make_writable(func, path, _exc):
            os.chmod(path, stat.S_IWRITE)
            func(path)

        os.chdir(self.original_cwd)
        shutil.rmtree(self.root, onerror=make_writable)

    def git(self, *args):
        result = subprocess.run(
            ["git", *args], cwd=self.work, capture_output=True, text=True, encoding="utf-8"
        )
        if result.returncode != 0:
            self.fail(f"git {' '.join(args)} failed: {result.stderr}")
        return result.stdout.strip()

    def write_notes(self, tag, text):
        path = os.path.join(self.work, "docs", "release-notes", f"{tag}.md")
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(text)

    def release_commit(self, tag):
        self.write_notes(tag, f"# Historia {tag}\n")
        self.git("add", "-A")
        self.git("commit", "--quiet", "-m", f"release {tag}")
        self.git("tag", "-a", tag, "-m", tag)


class TestPreviousTag(RepoTestCase):
    def test_previous_tag_lookup(self):
        self.release_commit("v0.1.0")
        self.release_commit("v0.2.0")

        self.assertEqual(release_notes.previous_tag("v0.2.0", self.work), "v0.1.0")
        self.assertIsNone(release_notes.previous_tag("v0.1.0", self.work))


class TestMainWithGhStub(RepoTestCase):
    def setUp(self):
        super().setUp()
        self.stub_dir = os.path.join(self.root, "stub")
        bin_dir = os.path.join(self.stub_dir, "bin")
        os.makedirs(bin_dir)
        with open(os.path.join(self.stub_dir, "gh_stub.py"), "w", encoding="utf-8") as f:
            f.write(STUB)
        stub_script = os.path.join(self.stub_dir, "gh_stub.py")
        if os.name == "nt":
            wrapper = os.path.join(bin_dir, "gh.bat")
            with open(wrapper, "w", encoding="ascii", newline="\r\n") as f:
                f.write(f'@"{sys.executable}" "{stub_script}" %*\r\n')
        else:
            wrapper = os.path.join(bin_dir, "gh")
            with open(wrapper, "w", encoding="utf-8", newline="\n") as f:
                f.write(f'#!/bin/sh\nexec "{sys.executable}" "{stub_script}" "$@"\n')
            os.chmod(wrapper, os.stat(wrapper).st_mode | stat.S_IXUSR)
        patcher = mock.patch.dict(
            os.environ,
            {
                "PATH": bin_dir + os.pathsep + os.environ["PATH"],
                "GH_STUB_DIR": self.stub_dir,
            },
        )
        patcher.start()
        self.addCleanup(patcher.stop)
        self.configure(releases=[], generated="")

    def configure(self, releases, generated, fail_edit=False):
        with open(os.path.join(self.stub_dir, "config.json"), "w", encoding="utf-8") as f:
            json.dump(
                {"releases": releases, "generated": generated, "fail_edit": fail_edit}, f
            )

    def calls(self):
        path = os.path.join(self.stub_dir, "calls.jsonl")
        if not os.path.exists(path):
            return []
        with open(path, encoding="utf-8") as f:
            return [json.loads(line) for line in f if line.strip()]

    def run_main(self, *args):
        os.chdir(self.work)
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = release_notes.main(["--repo", "owner/repo", *args])
        return code, out.getvalue(), err.getvalue()

    def test_missing_release_is_skipped(self):
        self.release_commit("v0.1.0")
        self.configure(releases=[], generated="## What's Changed\n")

        code, out, _ = self.run_main("--resync-all", "true")

        self.assertEqual(code, 0)
        self.assertIn("v0.1.0: skipped: no release", out)
        self.assertFalse(any(call["args"][:2] == ["release", "edit"] for call in self.calls()))

    def test_existing_release_gets_notes_then_generated_changelog(self):
        self.release_commit("v0.1.0")
        self.release_commit("v0.2.0")
        self.configure(releases=["v0.2.0"], generated="## What's Changed\n* change\n")
        before = self.git("rev-parse", "v0.1.0")
        after = self.git("rev-parse", "HEAD")

        code, out, _ = self.run_main("--before", before, "--after", after)

        self.assertEqual(code, 0)
        self.assertIn("v0.2.0: updated", out)
        calls = self.calls()
        api = next(call for call in calls if call["args"][0] == "api")
        self.assertIn("tag_name=v0.2.0", api["args"])
        self.assertIn("previous_tag_name=v0.1.0", api["args"])
        edit = next(call for call in calls if call["args"][:2] == ["release", "edit"])
        self.assertEqual(
            edit["body"], "# Historia v0.2.0\n\n## What's Changed\n* change\n"
        )

    def test_deleted_notes_file_changes_nothing(self):
        self.release_commit("v0.1.0")
        self.release_commit("v0.2.0")
        before = self.git("rev-parse", "HEAD")
        self.git("rm", "--quiet", "--", "docs/release-notes/v0.2.0.md")
        self.git("commit", "--quiet", "-m", "remove notes")
        after = self.git("rev-parse", "HEAD")
        self.configure(releases=["v0.1.0", "v0.2.0"], generated="")

        code, out, _ = self.run_main("--before", before, "--after", after)

        self.assertEqual(code, 0)
        self.assertIn("v0.2.0: skipped: deleted", out)
        self.assertEqual(self.calls(), [])

    def test_failed_edit_exits_non_zero(self):
        self.release_commit("v0.1.0")
        self.configure(releases=["v0.1.0"], generated="", fail_edit=True)

        code, _, err = self.run_main("--resync-all", "true")

        self.assertEqual(code, 1)
        self.assertIn("v0.1.0: failed", err)

    def test_without_range_or_resync_does_nothing(self):
        self.release_commit("v0.1.0")

        code, out, _ = self.run_main()

        self.assertEqual(code, 0)
        self.assertIn("nothing to sync", out)
        self.assertEqual(self.calls(), [])


if __name__ == "__main__":
    unittest.main()
