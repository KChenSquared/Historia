import os
import shutil
import stat
import subprocess
import tempfile
import unittest

HOOK_SOURCE = os.path.join(
    os.path.dirname(os.path.abspath(__file__)), "..", "..", ".githooks", "pre-push"
)

NOTES_DIR = "docs/release-notes"


class TestPrePushHook(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp(prefix="test_release_notes_hook_")
        self.work = os.path.join(self.root, "work")
        self.remote = os.path.join(self.root, "remote.git")
        self.hooks = os.path.join(self.root, "hooks")
        os.makedirs(self.work)
        os.makedirs(self.hooks)

        self.env = dict(os.environ)
        self.env["GIT_CONFIG_GLOBAL"] = os.devnull
        self.env["GIT_CONFIG_NOSYSTEM"] = "1"
        self.env["GIT_TERMINAL_PROMPT"] = "0"

        # The hook is copied with LF line endings so a CRLF checkout cannot break the shell.
        with open(HOOK_SOURCE, "rb") as f:
            script = f.read().replace(b"\r\n", b"\n")
        hook_path = os.path.join(self.hooks, "pre-push")
        with open(hook_path, "wb") as f:
            f.write(script)
        os.chmod(hook_path, os.stat(hook_path).st_mode | stat.S_IXUSR | stat.S_IXGRP | stat.S_IXOTH)

        self.git(self.root, "init", "--bare", "--quiet", self.remote)
        self.git(self.work, "init", "--quiet", "--initial-branch=main")
        self.git(self.work, "config", "user.name", "Test")
        self.git(self.work, "config", "user.email", "test@example.com")
        self.git(self.work, "config", "commit.gpgsign", "false")
        self.git(self.work, "config", "tag.gpgsign", "false")
        self.git(self.work, "config", "core.hooksPath", self.hooks)
        self.git(self.work, "remote", "add", "origin", self.remote)

    def tearDown(self):
        def make_writable(func, path, _exc):
            os.chmod(path, stat.S_IWRITE)
            func(path)

        shutil.rmtree(self.root, onerror=make_writable)

    def git(self, cwd, *args, check=True):
        result = subprocess.run(
            ["git", *args],
            cwd=cwd,
            env=self.env,
            capture_output=True,
            text=True,
        )
        if check and result.returncode != 0:
            self.fail(f"git {' '.join(args)} failed: {result.stderr}")
        return result

    def commit(self, name, content, message):
        path = os.path.join(self.work, *name.split("/"))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            f.write(content)
        self.git(self.work, "add", "--", name)
        self.git(self.work, "commit", "--quiet", "-m", message)

    def commit_notes(self, tag):
        self.commit(f"{NOTES_DIR}/{tag}.md", f"# Historia {tag}\n", f"notes for {tag}")

    def commit_plain(self, name="file.txt"):
        self.commit(name, "content\n", f"add {name}")

    def push(self, *refspecs):
        return self.git(self.work, "push", "origin", *refspecs, check=False)

    def remote_has_ref(self, ref):
        return self.git(self.remote, "rev-parse", "--verify", "--quiet", ref, check=False).returncode == 0

    def test_tag_without_notes_is_refused(self):
        self.commit_plain()
        self.git(self.work, "tag", "-a", "v1.0.0", "-m", "v1.0.0")

        result = self.push("refs/tags/v1.0.0")

        self.assertNotEqual(result.returncode, 0)
        self.assertIn("v1.0.0", result.stderr)
        self.assertIn("docs/release-notes/v1.0.0.md", result.stderr)
        self.assertFalse(self.remote_has_ref("refs/tags/v1.0.0"))

    def test_tag_with_notes_passes(self):
        self.commit_notes("v1.0.0")
        self.git(self.work, "tag", "-a", "v1.0.0", "-m", "v1.0.0")

        result = self.push("refs/tags/v1.0.0")

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue(self.remote_has_ref("refs/tags/v1.0.0"))

    def test_annotated_and_lightweight_tags_both_checked(self):
        self.commit_plain()
        self.git(self.work, "tag", "v1.0.0")
        self.git(self.work, "tag", "-a", "v1.0.1", "-m", "v1.0.1")

        light = self.push("refs/tags/v1.0.0")
        annotated = self.push("refs/tags/v1.0.1")

        self.assertNotEqual(light.returncode, 0)
        self.assertNotEqual(annotated.returncode, 0)

        self.commit_notes("v1.0.2")
        self.commit_notes("v1.0.3")
        self.git(self.work, "tag", "v1.0.2", "HEAD~1")
        self.git(self.work, "tag", "-a", "v1.0.3", "-m", "v1.0.3")

        light_ok = self.push("refs/tags/v1.0.2")
        annotated_ok = self.push("refs/tags/v1.0.3")

        self.assertEqual(light_ok.returncode, 0, light_ok.stderr)
        self.assertEqual(annotated_ok.returncode, 0, annotated_ok.stderr)

    def test_working_tree_file_does_not_count(self):
        self.commit_plain()
        self.git(self.work, "tag", "-a", "v1.0.0", "-m", "v1.0.0")
        notes = os.path.join(self.work, *NOTES_DIR.split("/"), "v1.0.0.md")
        os.makedirs(os.path.dirname(notes), exist_ok=True)
        with open(notes, "w", encoding="utf-8", newline="\n") as f:
            f.write("# Historia v1.0.0\n")

        untracked = self.push("refs/tags/v1.0.0")
        self.git(self.work, "add", "--", f"{NOTES_DIR}/v1.0.0.md")
        staged = self.push("refs/tags/v1.0.0")
        self.git(self.work, "commit", "--quiet", "-m", "notes after the tag")
        later_commit = self.push("refs/tags/v1.0.0")

        self.assertNotEqual(untracked.returncode, 0)
        self.assertNotEqual(staged.returncode, 0)
        self.assertNotEqual(later_commit.returncode, 0)
        self.assertFalse(self.remote_has_ref("refs/tags/v1.0.0"))

    def test_tag_deletion_passes(self):
        self.commit_notes("v1.0.0")
        self.git(self.work, "tag", "v1.0.0")
        self.assertEqual(self.push("refs/tags/v1.0.0").returncode, 0)
        self.git(self.work, "rm", "--quiet", "--", f"{NOTES_DIR}/v1.0.0.md")
        self.git(self.work, "commit", "--quiet", "-m", "remove notes")

        result = self.push(":refs/tags/v1.0.0")

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(self.remote_has_ref("refs/tags/v1.0.0"))

    def test_branch_push_unaffected(self):
        self.commit_plain()

        result = self.push("main")

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue(self.remote_has_ref("refs/heads/main"))

    def test_non_v_tag_unaffected(self):
        self.commit_plain()
        self.git(self.work, "tag", "-a", "release-1", "-m", "release-1")

        result = self.push("refs/tags/release-1")

        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue(self.remote_has_ref("refs/tags/release-1"))


if __name__ == "__main__":
    unittest.main()
