#!/usr/bin/env bash
set -euo pipefail

# Self-test for ScenarioLab bash helpers.
# Asserts helper correctness in an isolated sandbox.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCENARIO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

SANDBOX="$(mktemp -d)"
trap 'rm -rf "$SANDBOX"' EXIT

export SCENARIO_LOCAL1="$SANDBOX/local1"
export SCENARIO_LOCAL2="$SANDBOX/local2"
export SCENARIO_REMOTES="$SANDBOX/remotes"
export SCENARIO_TEMPLATES="$SCENARIO_ROOT/templates"
export SCENARIO_LIB="$SCENARIO_ROOT/lib/scenario.sh"
export SCENARIO_ROOT="$SCENARIO_ROOT"
export SCENARIO_NAME="selftest"
export SCENARIO_HEADLESS=1

export GIT_TERMINAL_PROMPT=0
export GIT_EDITOR=true
export GIT_SEQUENCE_EDITOR=true
export GIT_PAGER=cat
export PAGER=cat
export GIT_CONFIG_NOSYSTEM=1
export GIT_CONFIG_GLOBAL="$SCENARIO_ROOT/lib/lab.gitconfig"

mkdir -p "$SCENARIO_LOCAL1" "$SCENARIO_LOCAL2" "$SCENARIO_REMOTES"

source "$SCENARIO_LIB"

echo "Running helper self-tests..."

# ── 1. lab_pin_config ──
REPO_PIN="$SANDBOX/pin_repo"
git init "$REPO_PIN" >/dev/null
lab_pin_config "$REPO_PIN"
[[ "$(git -C "$REPO_PIN" config --local core.autocrlf)" = "false" ]] || { echo "pin_config default autocrlf failed"; exit 1; }
[[ "$(git -C "$REPO_PIN" config --local merge.conflictStyle)" = "merge" ]] || { echo "pin_config default conflictStyle failed"; exit 1; }

lab_pin_config "$REPO_PIN" core.autocrlf true custom.foo bar
[[ "$(git -C "$REPO_PIN" config --local core.autocrlf)" = "true" ]] || { echo "pin_config override autocrlf failed"; exit 1; }
[[ "$(git -C "$REPO_PIN" config --local custom.foo)" = "bar" ]] || { echo "pin_config custom key failed"; exit 1; }
echo "PASS: lab_pin_config"

# ── 2. lab_expect_conflict & lab_assert_status ──
REPO_CONF="$SANDBOX/conf_repo"
git init "$REPO_CONF" >/dev/null
git -C "$REPO_CONF" config user.name "Test"
git -C "$REPO_CONF" config user.email "test@example.com"
echo "base" > "$REPO_CONF/file.txt"
git -C "$REPO_CONF" add file.txt
git -C "$REPO_CONF" commit -m "base" >/dev/null

git -C "$REPO_CONF" checkout -b branch-a >/dev/null 2>&1
echo "edit a" > "$REPO_CONF/file.txt"
git -C "$REPO_CONF" commit -am "edit a" >/dev/null

git -C "$REPO_CONF" checkout -b branch-b HEAD~1 >/dev/null 2>&1
echo "edit b" > "$REPO_CONF/file.txt"
git -C "$REPO_CONF" commit -am "edit b" >/dev/null

# Merge conflict (exit 1)
git -C "$REPO_CONF" checkout branch-a >/dev/null 2>&1
lab_expect_conflict "$REPO_CONF" "merge conflict branch-b" merge branch-b >/dev/null
lab_assert_status "$REPO_CONF" file.txt UU

# lab_assert_status rejects wrong code
if (lab_assert_status "$REPO_CONF" file.txt AA 2>/dev/null); then
    echo "lab_assert_status should have failed on wrong code AA"
    exit 1
fi
echo "PASS: lab_assert_status"

# Reset repo for checkout -m test
git -C "$REPO_CONF" merge --abort >/dev/null 2>&1 || git -C "$REPO_CONF" reset --hard HEAD >/dev/null 2>&1

# checkout -m conflict (exit 0)
echo "local dirty" > "$REPO_CONF/file.txt"
lab_expect_conflict "$REPO_CONF" "checkout -m conflict" checkout -m branch-b >/dev/null
lab_assert_status "$REPO_CONF" file.txt UU

# Clean merge must fail lab_expect_conflict
REPO_CLEAN="$SANDBOX/clean_repo"
git init "$REPO_CLEAN" >/dev/null
git -C "$REPO_CLEAN" config user.name "Test"
git -C "$REPO_CLEAN" config user.email "test@example.com"
echo "base" > "$REPO_CLEAN/f1.txt"
git -C "$REPO_CLEAN" add f1.txt
git -C "$REPO_CLEAN" commit -m "base" >/dev/null
git -C "$REPO_CLEAN" checkout -b c-branch >/dev/null 2>&1
echo "branch" > "$REPO_CLEAN/f2.txt"
git -C "$REPO_CLEAN" add f2.txt
git -C "$REPO_CLEAN" commit -m "add f2" >/dev/null
git -C "$REPO_CLEAN" checkout branch-a 2>/dev/null || git -C "$REPO_CLEAN" checkout master 2>/dev/null || git -C "$REPO_CLEAN" checkout main 2>/dev/null
if (lab_expect_conflict "$REPO_CLEAN" "clean merge should fail" merge c-branch 2>/dev/null >/dev/null); then
    echo "lab_expect_conflict should have failed on clean merge"
    exit 1
fi
echo "PASS: lab_expect_conflict"

# ── 3. lab_count_markers ──
MARKER_FILE="$SANDBOX/markers.txt"
printf '<<<<<<< HEAD\n||||||| base\n=======\n>>>>>>> theirs\n<<<<<<<< 8chars\n' > "$MARKER_FILE"
[[ "$(lab_count_markers "$MARKER_FILE" "<")" -eq 1 ]] || { echo "count 7 < failed"; exit 1; }
[[ "$(lab_count_markers "$MARKER_FILE" "|")" -eq 1 ]] || { echo "count 7 | failed"; exit 1; }
[[ "$(lab_count_markers "$MARKER_FILE" "=")" -eq 1 ]] || { echo "count 7 = failed"; exit 1; }
[[ "$(lab_count_markers "$MARKER_FILE" ">")" -eq 1 ]] || { echo "count 7 > failed"; exit 1; }
[[ "$(lab_count_markers "$MARKER_FILE" "<" 8)" -eq 1 ]] || { echo "count 8 < failed"; exit 1; }

# CRLF lines
CRLF_FILE="$SANDBOX/crlf.txt"
printf '<<<<<<< HEAD\r\n=======\r\n>>>>>>> theirs\r\n' > "$CRLF_FILE"
[[ "$(lab_count_markers "$CRLF_FILE" "<")" -eq 1 ]] || { echo "crlf count < failed"; exit 1; }
[[ "$(lab_count_markers "$CRLF_FILE" "=")" -eq 1 ]] || { echo "crlf count = failed"; exit 1; }
[[ "$(lab_count_markers "$CRLF_FILE" ">")" -eq 1 ]] || { echo "crlf count > failed"; exit 1; }

# Custom size 10
SIZE10_FILE="$SANDBOX/size10.txt"
printf '<<<<<<<<<< branch\n==========\n>>>>>>>>>> main\n' > "$SIZE10_FILE"
[[ "$(lab_count_markers "$SIZE10_FILE" "<" 10)" -eq 1 ]] || { echo "size 10 count < failed"; exit 1; }
[[ "$(lab_count_markers "$SIZE10_FILE" "<" 7)" == 0 ]] || { echo "size 7 in size 10 should be 0"; exit 1; }

# UTF-16LE file via iconv
UTF16_FILE="$SANDBOX/utf16.txt"
printf '<<<<<<< HEAD\r\n=======\r\n>>>>>>> theirs\r\n' | iconv -f UTF-8 -t UTF-16LE > "$UTF16_FILE"
[[ "$(lab_count_markers "$UTF16_FILE" "<" 7 UTF-16LE)" -eq 1 ]] || { echo "utf16 count < failed"; exit 1; }
echo "PASS: lab_count_markers"

# ── 4. lab_assert_op ──
OP_REPO="$SANDBOX/op_repo"
git init "$OP_REPO" >/dev/null
git -C "$OP_REPO" config user.name "Test"
git -C "$OP_REPO" config user.email "test@example.com"
echo "1" > "$OP_REPO/f.txt"
git -C "$OP_REPO" add f.txt
git -C "$OP_REPO" commit -m "c1" >/dev/null

# none
lab_assert_op "$OP_REPO" none

# merge
echo "merge_head_sha" > "$OP_REPO/.git/MERGE_HEAD"
lab_assert_op "$OP_REPO" merge
rm -f "$OP_REPO/.git/MERGE_HEAD"

# revert
echo "revert_head_sha" > "$OP_REPO/.git/REVERT_HEAD"
lab_assert_op "$OP_REPO" revert
rm -f "$OP_REPO/.git/REVERT_HEAD"

# cherry-pick
echo "cherry_head_sha" > "$OP_REPO/.git/CHERRY_PICK_HEAD"
lab_assert_op "$OP_REPO" cherry-pick
rm -f "$OP_REPO/.git/CHERRY_PICK_HEAD"

# rebase
mkdir -p "$OP_REPO/.git/rebase-merge"
lab_assert_op "$OP_REPO" rebase
rm -rf "$OP_REPO/.git/rebase-merge"

# bisect
echo "bisect_log" > "$OP_REPO/.git/BISECT_LOG"
lab_assert_op "$OP_REPO" bisect
rm -f "$OP_REPO/.git/BISECT_LOG"

# Rejection of wrong op
if (lab_assert_op "$OP_REPO" merge 2>/dev/null); then
    echo "lab_assert_op should have rejected merge when op is none"
    exit 1
fi
echo "PASS: lab_assert_op"

# ── 5. lab_gen_lines ──
GEN1="$SANDBOX/gen1.txt"
GEN2="$SANDBOX/gen2.txt"
lab_gen_lines "$GEN1" 100 "gen"
lab_gen_lines "$GEN2" 100 "gen"
HASH1="$(sha256sum "$GEN1" | awk '{print $1}')"
HASH2="$(sha256sum "$GEN2" | awk '{print $1}')"
[[ "$HASH1" = "$HASH2" ]] || { echo "gen_lines not deterministic"; exit 1; }
echo "PASS: lab_gen_lines"

# ── 6. lab_save_template ──
STATUS_BEFORE="$(git -C "$SCENARIO_ROOT" status --porcelain templates/)"
TMPL_OUT="$(lab_save_template _base 2>&1)"
STATUS_AFTER="$(git -C "$SCENARIO_ROOT" status --porcelain templates/)"
[[ "$STATUS_BEFORE" = "$STATUS_AFTER" ]] || { echo "templates modified during headless run"; exit 1; }
[[ "$TMPL_OUT" == *"[INFO] headless run: template not saved"* ]] || { echo "headless save_template missing log"; exit 1; }
echo "PASS: lab_save_template"

echo "All helper self-tests PASSED."
exit 0
