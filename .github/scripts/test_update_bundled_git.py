import hashlib
import json
import os
import shutil
import sys
import tempfile
import unittest
import zipfile
from unittest import mock

import update_bundled_git
from update_bundled_git import (
    evaluate_component,
    main,
    parse_mingit_version,
    rewrite_license_tags,
)

LICENSES = (
    "## Git for Windows (MinGit)\n\n"
    "Pinned tag `v2.56.0.windows.4`, source at /tree/v2.56.0.windows.4\n\n"
    "## Other Library\n\nAlso released as v3.8.1 and v2.56.0.windows.4.\n\n"
    "## Git LFS\n\nPinned release `v3.8.1`.\n"
)


class TestUpdateBundledGit(unittest.TestCase):
    def setUp(self):
        self.test_dir = tempfile.mkdtemp(prefix="test_update_bundled_git_")
        self.fixtures_dir = os.path.join(os.path.dirname(__file__), "fixtures", "bundled-git")
        self.manifest_path = os.path.join(self.test_dir, "bundled-git.json")
        self.licenses_path = os.path.join(self.test_dir, "licenses.md")
        self.pr_body_path = os.path.join(self.test_dir, "pr-body.md")
        manifest = {
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
            json.dump(manifest, f, indent=2)
        with open(self.licenses_path, "w", encoding="utf-8", newline="") as f:
            f.write(LICENSES)

    def tearDown(self):
        shutil.rmtree(self.test_dir, ignore_errors=True)

    def run_main(self, fixture, *extra):
        argv = [
            "update_bundled_git.py",
            "--manifest", self.manifest_path,
            "--licenses-file", self.licenses_path,
            "--releases-json", os.path.join(self.fixtures_dir, fixture),
            "--pr-body-file", self.pr_body_path,
            "--dry-run",
            *extra,
        ]
        with mock.patch.object(sys, "argv", argv):
            return main()

    def manifest(self):
        with open(self.manifest_path, "r", encoding="utf-8") as f:
            return json.load(f)

    def pr_body(self):
        with open(self.pr_body_path, "r", encoding="utf-8") as f:
            return f.read()

    def licenses(self):
        with open(self.licenses_path, "r", encoding="utf-8", newline="") as f:
            return f.read()

    def test_version_ordering_handles_windows_10_greater_than_windows_9(self):
        self.assertGreater(parse_mingit_version("v2.56.0.windows.10"), parse_mingit_version("v2.56.0.windows.9"))

    def test_only_non_security_newer_releases_reports_no_change(self):
        self.assertEqual(self.run_main("releases_no_security.json"), 0)
        self.assertFalse(os.path.exists(self.pr_body_path))
        self.assertEqual(self.manifest()["mingit"]["version"], "v2.56.0.windows.4")
        self.assertEqual(self.licenses(), LICENSES)

    def test_security_release_selects_newest_stable_and_lists_cves(self):
        self.assertEqual(self.run_main("releases_with_security.json"), 0)
        manifest = self.manifest()
        self.assertEqual(manifest["mingit"]["version"], "v2.56.0.windows.6")
        self.assertEqual(manifest["mingit"]["asset"], "MinGit-2.56.0.6-64-bit.zip")
        body = self.pr_body()
        self.assertIn("CVE-2026-99991", body)
        self.assertIn("v2.56.0.windows.6", body)
        self.assertIn("security fixes", body)

    def test_prereleases_and_backport_builds_ignored(self):
        self.assertEqual(self.run_main("releases_prerelease_and_ordering.json"), 0)
        self.assertEqual(self.manifest()["mingit"]["version"], "v2.56.0.windows.10")
        body = self.pr_body()
        self.assertNotIn("v2.56.0.windows.11", body)
        self.assertNotIn("CVE-2026-88888", body)
        self.assertNotIn("CVE-2026-44444", body)
        self.assertIn("CVE-2026-77777", body)

    def test_a_security_prerelease_alone_does_not_trigger_an_update(self):
        release = {
            "tag_name": "v2.56.0.windows.5",
            "prerelease": True,
            "body": "Fixes CVE-2026-1.",
            "assets": [],
        }
        result = evaluate_component(
            "mingit", {"version": "v2.56.0.windows.4"}, [release], force=False, dry_run=True, minimum_git_version=(2, 46)
        )
        self.assertIsNone(result)

    def test_force_updates_without_security_release_and_does_not_claim_one(self):
        self.assertEqual(self.run_main("releases_no_security.json", "--force"), 0)
        manifest = self.manifest()
        self.assertEqual(manifest["mingit"]["version"], "v2.56.0.windows.5")
        self.assertEqual(manifest["gitLfs"]["version"], "v3.8.2")
        self.assertNotIn("security fixes", self.pr_body())

    def test_digest_and_release_body_mismatch_fails(self):
        with self.assertRaises(ValueError) as ctx:
            self.run_main("releases_digest_mismatch.json")
        self.assertIn("does not match", str(ctx.exception).lower())

    def test_licence_tags_follow_the_update_within_their_own_sections_only(self):
        self.run_main("releases_no_security.json", "--force")
        expected = (
            "## Git for Windows (MinGit)\n\n"
            "Pinned tag `v2.56.0.windows.5`, source at /tree/v2.56.0.windows.5\n\n"
            "## Other Library\n\nAlso released as v3.8.1 and v2.56.0.windows.4.\n\n"
            "## Git LFS\n\nPinned release `v3.8.2`.\n"
        )
        self.assertEqual(self.licenses(), expected)

    def test_a_licence_section_without_the_pinned_tag_stops_before_anything_is_written(self):
        with open(self.licenses_path, "w", encoding="utf-8", newline="") as f:
            f.write("## Git for Windows (MinGit)\n\nNo tag.\n\n## Git LFS\n\nNo tag.\n")
        with self.assertRaises(ValueError):
            self.run_main("releases_no_security.json", "--force")
        self.assertEqual(self.manifest()["mingit"]["version"], "v2.56.0.windows.4")

    def test_rewrite_keeps_crlf_line_endings(self):
        text = "## Git LFS\r\n\r\nPinned `v1.0.0`.\r\n"
        updates = [{"component": "gitLfs", "old_version": "v1.0.0", "new_version": "v1.0.1"}]
        self.assertEqual(rewrite_license_tags(text, updates), "## Git LFS\r\n\r\nPinned `v1.0.1`.\r\n")


class TestDownloadPath(unittest.TestCase):
    """The path a real run takes: bytes are downloaded, hashed and compared."""

    ASSET = "git-lfs-windows-amd64-v3.8.2.zip"

    def release(self, digest):
        return {
            "tag_name": "v3.8.2",
            "body": "Security fix for CVE-2026-1.",
            "assets": [{"name": self.ASSET, "browser_download_url": "https://example.invalid/a.zip", "digest": digest}],
        }

    @staticmethod
    def fake_download(url, dest_path):
        with zipfile.ZipFile(dest_path, "w") as z:
            z.writestr("git-lfs-3.8.2/git-lfs.exe", b"fake")
        with open(dest_path, "rb") as f:
            return hashlib.sha256(f.read()).hexdigest()

    def evaluate(self, digest):
        return evaluate_component(
            "gitLfs", {"version": "v3.8.1"}, [self.release(digest)], force=False, dry_run=False, minimum_git_version=(2, 46)
        )

    def test_downloaded_bytes_that_do_not_match_the_digest_fail(self):
        with mock.patch.object(update_bundled_git, "download_and_hash", self.fake_download):
            with self.assertRaises(ValueError) as ctx:
                self.evaluate("sha256:" + "0" * 64)
        self.assertIn("does not match api digest", str(ctx.exception).lower())

    def test_matching_bytes_are_smoke_tested_and_pinned(self):
        captured = {}

        def download(url, dest_path):
            captured["hash"] = self.fake_download(url, dest_path)
            return captured["hash"]

        ran = mock.Mock(return_value=mock.Mock(stdout="git-lfs/3.8.2"))
        with mock.patch.object(update_bundled_git, "download_and_hash", download), \
                mock.patch.object(update_bundled_git.subprocess, "run", ran), \
                mock.patch.object(update_bundled_git.sys, "platform", "win32"):
            result = self.evaluate(None)

        self.assertEqual(result["sha256"], captured["hash"])
        self.assertEqual(ran.call_count, 1)
        self.assertTrue(ran.call_args.args[0][0].endswith("git-lfs.exe"))


if __name__ == "__main__":
    unittest.main()
