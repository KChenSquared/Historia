#!/usr/bin/env python3
"""Updates packaging/bundled-git.json when MinGit or git-lfs publish security releases."""
import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile
import urllib.request
import zipfile

MINGIT_TAG_RE = re.compile(r"^v(?P<major>\d+)\.(?P<minor>\d+)\.(?P<patch>\d+)\.windows\.(?P<build>\d+)$")
GIT_LFS_TAG_RE = re.compile(r"^v(?P<major>\d+)\.(?P<minor>\d+)\.(?P<patch>\d+)$")
SECURITY_RE = re.compile(r"(?i)\bsecurity\b|CVE-\d{4}-\d+")
CVE_RE = re.compile(r"CVE-\d{4}-\d+")


def parse_mingit_version(tag: str) -> tuple[int, int, int, int] | None:
    m = MINGIT_TAG_RE.match(tag)
    if not m:
        return None
    return (int(m.group("major")), int(m.group("minor")), int(m.group("patch")), int(m.group("build")))


def parse_git_lfs_version(tag: str) -> tuple[int, int, int] | None:
    m = GIT_LFS_TAG_RE.match(tag)
    if not m:
        return None
    return (int(m.group("major")), int(m.group("minor")), int(m.group("patch")))


def extract_body_hash(body: str, asset_name: str) -> str | None:
    pattern = rf"{re.escape(asset_name)}\s*\|\s*([0-9a-fA-F]{{64}})"
    m = re.search(pattern, body or "")
    if m:
        return m.group(1).lower()
    return None


def extract_cves(text: str) -> list[str]:
    return sorted(set(CVE_RE.findall(text or "")))


def fetch_github_releases(repo: str, token: str | None = None) -> list[dict]:
    url = f"https://api.github.com/repos/{repo}/releases?per_page=30"
    req = urllib.request.Request(url)
    req.add_header("Accept", "application/vnd.github+json")
    req.add_header("User-Agent", "Historia-Bundled-Git-Updater")
    if token:
        req.add_header("Authorization", f"Bearer {token}")

    with urllib.request.urlopen(req, timeout=30) as resp:
        return json.loads(resp.read().decode("utf-8"))


def download_and_hash(url: str, dest_path: str, token: str | None = None) -> str:
    req = urllib.request.Request(url)
    req.add_header("User-Agent", "Historia-Bundled-Git-Updater")
    if token:
        req.add_header("Authorization", f"Bearer {token}")

    hasher = hashlib.sha256()
    with urllib.request.urlopen(req, timeout=120) as resp, open(dest_path, "wb") as f:
        while chunk := resp.read(65536):
            hasher.update(chunk)
            f.write(chunk)
    return hasher.hexdigest().lower()


def evaluate_component(
    component_name: str,
    pinned_info: dict,
    releases: list[dict],
    force: bool,
    dry_run: bool,
    minimum_git_version: tuple[int, int],
    token: str | None = None,
) -> dict | None:
    is_mingit = component_name == "mingit"
    version_parser = parse_mingit_version if is_mingit else parse_git_lfs_version

    pinned_tag = pinned_info["version"]
    pinned_ver = version_parser(pinned_tag)
    if not pinned_ver:
        raise ValueError(f"Could not parse pinned version tag '{pinned_tag}' for {component_name}")

    eligible_releases = []
    for r in releases:
        if r.get("draft") or r.get("prerelease"):
            continue
        tag = r.get("tag_name", "")
        ver = version_parser(tag)
        if ver:
            eligible_releases.append((ver, r))

    if not eligible_releases:
        return None

    eligible_releases.sort(key=lambda pair: pair[0], reverse=True)

    newer = [pair for pair in eligible_releases if pair[0] > pinned_ver]
    if not newer:
        return None

    security_releases = [pair for pair in newer if SECURITY_RE.search(pair[1].get("body", "") or "")]

    if not security_releases and not force:
        return None

    target_ver, target_release = newer[0]

    if is_mingit:
        if (target_ver[0], target_ver[1]) < minimum_git_version:
            raise ValueError(
                f"MinGit target version {target_ver} is below minimum required Git version {minimum_git_version}"
            )

    target_asset = None
    for a in target_release.get("assets", []):
        aname = a.get("name", "")
        if is_mingit:
            if re.match(r"^MinGit-\d+\.\d+\.\d+(?:\.\d+)?-64-bit\.zip$", aname) and "busybox" not in aname:
                target_asset = a
                break
        else:
            if re.match(r"^git-lfs-windows-amd64-(?:v)?\d+\.\d+\.\d+\.zip$", aname):
                target_asset = a
                break

    if not target_asset:
        raise ValueError(
            f"Matching asset not found in target release {target_release.get('tag_name')} for {component_name}"
        )

    asset_name = target_asset["name"]
    download_url = target_asset["browser_download_url"]

    api_digest = target_asset.get("digest")
    expected_digest_hash = None
    if api_digest and api_digest.startswith("sha256:"):
        expected_digest_hash = api_digest[7:].lower()

    body_hash = extract_body_hash(target_release.get("body", ""), asset_name) if is_mingit else None

    if expected_digest_hash and body_hash and expected_digest_hash != body_hash:
        raise ValueError(
            f"Digest hash {expected_digest_hash} does not match release body hash {body_hash} for {asset_name}"
        )

    final_hash = None
    if dry_run:
        final_hash = expected_digest_hash or body_hash or "0" * 64
    else:
        with tempfile.TemporaryDirectory() as tmpdir:
            archive_path = os.path.join(tmpdir, asset_name)
            final_hash = download_and_hash(download_url, archive_path, token=token)

            if expected_digest_hash and final_hash != expected_digest_hash:
                raise ValueError(
                    f"Computed hash {final_hash} does not match API digest {expected_digest_hash} for {asset_name}"
                )
            if body_hash and final_hash != body_hash:
                raise ValueError(
                    f"Computed hash {final_hash} does not match body hash {body_hash} for {asset_name}"
                )

            extract_dir = os.path.join(tmpdir, "extracted")
            os.makedirs(extract_dir, exist_ok=True)
            with zipfile.ZipFile(archive_path, "r") as z:
                z.extractall(extract_dir)

            if sys.platform == "win32":
                if is_mingit:
                    git_exe = os.path.join(extract_dir, "cmd", "git.exe")
                    if not os.path.exists(git_exe):
                        raise RuntimeError(f"cmd\\git.exe not found in extracted {asset_name}")
                    proc = subprocess.run([git_exe, "--version"], capture_output=True, text=True, check=True)
                    if "git version" not in proc.stdout:
                        raise RuntimeError(f"Unexpected git --version output: {proc.stdout}")
                else:
                    lfs_exe = None
                    for root, _, files in os.walk(extract_dir):
                        if "git-lfs.exe" in files:
                            lfs_exe = os.path.join(root, "git-lfs.exe")
                            break
                    if not lfs_exe:
                        raise RuntimeError(f"git-lfs.exe not found in extracted {asset_name}")
                    proc = subprocess.run([lfs_exe, "version"], capture_output=True, text=True, check=True)
                    if "git-lfs" not in proc.stdout:
                        raise RuntimeError(f"Unexpected git lfs version output: {proc.stdout}")

    all_cves = []
    security_sentences = []
    for _, sr in security_releases:
        body = sr.get("body", "") or ""
        all_cves.extend(extract_cves(body))
        for line in body.splitlines():
            if SECURITY_RE.search(line):
                stripped = line.strip(" -*#\t")
                if stripped and stripped not in security_sentences:
                    security_sentences.append(stripped)

    return {
        "component": component_name,
        "old_version": pinned_tag,
        "new_version": target_release["tag_name"],
        "asset": asset_name,
        "url": download_url,
        "sha256": final_hash,
        "html_url": target_release.get("html_url", ""),
        "cves": sorted(set(all_cves)),
        "security_notes": security_sentences[:3],
        "is_security": bool(security_releases),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", default="packaging/bundled-git.json")
    parser.add_argument("--output-manifest", default=None)
    parser.add_argument("--pr-body-file", default="bundled-git-pr-body.md")
    parser.add_argument("--releases-json", default=None, help="Path to fixture JSON holding releases for testing")
    parser.add_argument("--force", action="store_true", help="Force update to newest stable regardless of security fixes")
    parser.add_argument("--dry-run", action="store_true", help="Skip downloads and smoke check commands")
    parser.add_argument("--github-token", default=os.environ.get("GITHUB_TOKEN"))
    parser.add_argument("--ci-token-present", action="store_true", default=bool(os.environ.get("BUNDLED_GIT_PR_TOKEN")))
    args = parser.parse_args()

    output_manifest = args.output_manifest or args.manifest

    with open(args.manifest, "r", encoding="utf-8") as f:
        manifest = json.load(f)

    min_ver_str = manifest.get("minimumGitVersion", "2.46")
    min_parts = [int(p) for p in min_ver_str.split(".")]
    min_ver_tuple = (min_parts[0], min_parts[1])

    if args.releases_json:
        with open(args.releases_json, "r", encoding="utf-8") as f:
            all_releases = json.load(f)
        mingit_releases = all_releases.get("mingit", [])
        git_lfs_releases = all_releases.get("gitLfs", [])
    else:
        mingit_releases = fetch_github_releases("git-for-windows/git", token=args.github_token)
        git_lfs_releases = fetch_github_releases("git-lfs/git-lfs", token=args.github_token)

    updates = []
    for comp in ["mingit", "gitLfs"]:
        pinned = manifest.get(comp)
        if not pinned:
            continue
        releases = mingit_releases if comp == "mingit" else git_lfs_releases
        result = evaluate_component(
            component_name=comp,
            pinned_info=pinned,
            releases=releases,
            force=args.force,
            dry_run=args.dry_run,
            minimum_git_version=min_ver_tuple,
            token=args.github_token,
        )
        if result:
            updates.append(result)

    github_output = os.environ.get("GITHUB_OUTPUT")

    if not updates:
        print("No updates required for bundled Git components.")
        if github_output:
            with open(github_output, "a", encoding="utf-8") as f:
                f.write("changed=false\n")
        return 0

    for u in updates:
        comp = u["component"]
        manifest[comp]["version"] = u["new_version"]
        manifest[comp]["asset"] = u["asset"]
        manifest[comp]["url"] = u["url"]
        manifest[comp]["sha256"] = u["sha256"]

    with open(output_manifest, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2)
        f.write("\n")

    pr_lines = [
        "## Bundled Git Dependency Update",
        "",
        "This automated pull request updates the pinned bundled Git dependencies to address upstream security fixes:",
        "",
    ]

    for u in updates:
        pr_lines.append(f"### {u['component']}")
        pr_lines.append(f"- **Version**: `{u['old_version']}` -> [`{u['new_version']}`]({u['html_url']})")
        pr_lines.append(f"- **Asset**: `{u['asset']}`")
        pr_lines.append(f"- **SHA-256**: `{u['sha256']}`")
        if u["cves"]:
            cve_str = ", ".join(f"`{c}`" for c in u["cves"])
            pr_lines.append(f"- **Addressed CVEs**: {cve_str}")
        if u["security_notes"]:
            pr_lines.append("- **Security notes**:")
            for note in u["security_notes"]:
                pr_lines.append(f"  - {note}")
        pr_lines.append("")

    if not args.ci_token_present:
        pr_lines.append(
            "> [!NOTE]\n"
            "> This PR was created using the default `GITHUB_TOKEN`. GitHub Actions workflows will not trigger automatically on PRs created by this token; please trigger CI checks manually.\n"
        )

    with open(args.pr_body_file, "w", encoding="utf-8") as f:
        f.write("\n".join(pr_lines))

    print(f"Updated {len(updates)} component(s): {', '.join(u['component'] for u in updates)}")

    if github_output:
        with open(github_output, "a", encoding="utf-8") as f:
            f.write("changed=true\n")

    return 0


if __name__ == "__main__":
    sys.exit(main())
