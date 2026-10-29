import json
import os
import shutil
import tempfile
import unittest

from update_bundled_git import (
    parse_mingit_version,
    parse_git_lfs_version,
    evaluate_component,
    main,
)


class TestUpdateBundledGit(unittest.TestCase):
    def setUp(self):
        self.test_dir = tempfile.mkdtemp(prefix="test_update_bundled_git_")
        self.fixtures_dir = os.path.join(
            os.path.dirname(__file__), "fixtures", "bundled-git"
        )
        self.manifest_path = os.path.join(self.test_dir, "bundled-git.json")
        self.initial_manifest = {
            "minimumGitVersion": "2.46",
            "mingit": {
                "version": "v2.56.0.windows.4",
                "asset": "MinGit-2.56.0.4-64-bit.zip",
                "url": "https://github.com/git-for-windows/git/releases/download/v2.56.0.windows.4/MinGit-2.56.0.4-64-bit.zip",
                "sha256": "e3bf78351fe777fba47ddee5950082f8a98bb0f02604fdc7ba3ee2cb68ffa877",
            },
            "gitLfs": {
                "version": "v3.8.1",
                "asset": "git-lfs-windows-amd64-v3.8.1.zip",
                "url": "https://github.com/git-lfs/git-lfs/releases/download/v3.8.1/git-lfs-windows-amd64-v3.8.1.zip",
                "sha256": "5bb771c12b3dacc535e912d0270a010c209d4da2e63819c5918e3547c2defed9",
            },
        }
        with open(self.manifest_path, "w", encoding="utf-8") as f:
            json.dump(self.initial_manifest, f, indent=2)

    def tearDown(self):
        shutil.rmtree(self.test_dir, ignore_errors=True)

    def test_version_ordering_handles_windows_10_greater_than_windows_9(self):
        v9 = parse_mingit_version("v2.56.0.windows.9")
        v10 = parse_mingit_version("v2.56.0.windows.10")
        self.assertIsNotNone(v9)
        self.assertIsNotNone(v10)
        self.assertGreater(v10, v9)

    def test_only_non_security_newer_releases_reports_no_change(self):
        fixtures_file = os.path.join(self.fixtures_dir, "releases_no_security.json")
        pr_body_file = os.path.join(self.test_dir, "pr-body.md")
        test_args = [
            "update_bundled_git.py",
            "--manifest", self.manifest_path,
            "--releases-json", fixtures_file,
            "--pr-body-file", pr_body_file,
            "--dry-run",
        ]
        import sys
        old_argv = sys.argv
        try:
            sys.argv = test_args
            exit_code = main()
            self.assertEqual(exit_code, 0)
            self.assertFalse(os.path.exists(pr_body_file))
            with open(self.manifest_path, "r", encoding="utf-8") as f:
                manifest = json.load(f)
            self.assertEqual(manifest["mingit"]["version"], "v2.56.0.windows.4")
        finally:
            sys.argv = old_argv

    def test_security_release_selects_newest_stable_and_lists_cves(self):
        fixtures_file = os.path.join(self.fixtures_dir, "releases_with_security.json")
        pr_body_file = os.path.join(self.test_dir, "pr-body.md")
        test_args = [
            "update_bundled_git.py",
            "--manifest", self.manifest_path,
            "--releases-json", fixtures_file,
            "--pr-body-file", pr_body_file,
            "--dry-run",
        ]
        import sys
        old_argv = sys.argv
        try:
            sys.argv = test_args
            exit_code = main()
            self.assertEqual(exit_code, 0)
            self.assertTrue(os.path.exists(pr_body_file))
            with open(pr_body_file, "r", encoding="utf-8") as f:
                body_content = f.read()
            self.assertIn("CVE-2026-99991", body_content)
            self.assertIn("v2.56.0.windows.6", body_content)

            with open(self.manifest_path, "r", encoding="utf-8") as f:
                manifest = json.load(f)
            self.assertEqual(manifest["mingit"]["version"], "v2.56.0.windows.6")
            self.assertEqual(manifest["mingit"]["asset"], "MinGit-2.56.0.6-64-bit.zip")
        finally:
            sys.argv = old_argv

    def test_prereleases_and_backport_builds_ignored(self):
        fixtures_file = os.path.join(self.fixtures_dir, "releases_prerelease_and_ordering.json")
        pr_body_file = os.path.join(self.test_dir, "pr-body.md")
        test_args = [
            "update_bundled_git.py",
            "--manifest", self.manifest_path,
            "--releases-json", fixtures_file,
            "--pr-body-file", pr_body_file,
            "--dry-run",
        ]
        import sys
        old_argv = sys.argv
        try:
            sys.argv = test_args
            exit_code = main()
            self.assertEqual(exit_code, 0)
            with open(self.manifest_path, "r", encoding="utf-8") as f:
                manifest = json.load(f)
            self.assertEqual(manifest["mingit"]["version"], "v2.56.0.windows.10")
            with open(pr_body_file, "r", encoding="utf-8") as f:
                body_content = f.read()
            self.assertNotIn("v2.56.0.windows.11-rc1", body_content)
            self.assertNotIn("v2.53.0.windows.4", body_content)
            self.assertIn("CVE-2026-77777", body_content)
        finally:
            sys.argv = old_argv

    def test_force_updates_without_security_release(self):
        fixtures_file = os.path.join(self.fixtures_dir, "releases_no_security.json")
        pr_body_file = os.path.join(self.test_dir, "pr-body.md")
        test_args = [
            "update_bundled_git.py",
            "--manifest", self.manifest_path,
            "--releases-json", fixtures_file,
            "--pr-body-file", pr_body_file,
            "--force",
            "--dry-run",
        ]
        import sys
        old_argv = sys.argv
        try:
            sys.argv = test_args
            exit_code = main()
            self.assertEqual(exit_code, 0)
            self.assertTrue(os.path.exists(pr_body_file))
            with open(self.manifest_path, "r", encoding="utf-8") as f:
                manifest = json.load(f)
            self.assertEqual(manifest["mingit"]["version"], "v2.56.0.windows.5")
            self.assertEqual(manifest["gitLfs"]["version"], "v3.8.2")
        finally:
            sys.argv = old_argv

    def test_digest_mismatch_fails(self):
        fixtures_file = os.path.join(self.fixtures_dir, "releases_digest_mismatch.json")
        pr_body_file = os.path.join(self.test_dir, "pr-body.md")
        test_args = [
            "update_bundled_git.py",
            "--manifest", self.manifest_path,
            "--releases-json", fixtures_file,
            "--pr-body-file", pr_body_file,
            "--dry-run",
        ]
        import sys
        old_argv = sys.argv
        try:
            sys.argv = test_args
            with self.assertRaises(ValueError) as ctx:
                main()
            self.assertIn("does not match", str(ctx.exception).lower())
        finally:
            sys.argv = old_argv


if __name__ == "__main__":
    unittest.main()
