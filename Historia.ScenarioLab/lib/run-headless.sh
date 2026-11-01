#!/usr/bin/env bash
set -euo pipefail

# Headless ScenarioLab test runner and linter.
# Environment variables and execution semantics are mirrored with
# Services/ScriptRunner.cs — keep the two in sync.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCENARIO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

TWICE=0
KEEP=0
LINT_ONLY=0
PATHS=()

while [[ "$#" -gt 0 ]]; do
    case "$1" in
        --twice)
            TWICE=1
            shift
            ;;
        --keep)
            KEEP=1
            shift
            ;;
        --lint-only)
            LINT_ONLY=1
            shift
            ;;
        *)
            PATHS+=("$1")
            shift
            ;;
    esac
done

if [[ "${#PATHS[@]}" -eq 0 ]]; then
    PATHS=("$SCENARIO_ROOT/scripts")
fi

# Expand paths into list of .sh files (absolute paths)
SCRIPT_FILES=()
for p in "${PATHS[@]}"; do
    target="$p"
    if [[ ! -e "$target" ]] && [[ -e "$SCENARIO_ROOT/$p" ]]; then
        target="$SCENARIO_ROOT/$p"
    fi

    if [[ -d "$target" ]]; then
        abs_dir="$(cd "$target" && pwd)"
        while IFS= read -r f; do
            [[ -n "$f" ]] && SCRIPT_FILES+=("$f")
        done < <(find "$abs_dir" -type f -name "*.sh" | sort)
    elif [[ -f "$target" ]]; then
        abs_file="$(cd "$(dirname "$target")" && pwd)/$(basename "$target")"
        SCRIPT_FILES+=("$abs_file")
    else
        echo "[FAIL] Path not found: $p" >&2
        exit 1
    fi
done

if [[ "${#SCRIPT_FILES[@]}" -eq 0 ]]; then
    echo "[WARN] No scenario scripts found."
    exit 0
fi

# Build tree-wide basename index under scripts/ to check uniqueness
declare -A ALL_SCRIPTS_MAP
if [[ -d "$SCENARIO_ROOT/scripts" ]]; then
    while IFS= read -r f; do
        if [[ -n "$f" ]]; then
            base="$(basename "$f" .sh)"
            if [[ -n "${ALL_SCRIPTS_MAP[$base]:-}" ]]; then
                ALL_SCRIPTS_MAP["$base"]="${ALL_SCRIPTS_MAP[$base]}|$f"
            else
                ALL_SCRIPTS_MAP["$base"]="$f"
            fi
        fi
    done < <(find "$SCENARIO_ROOT/scripts" -type f -name "*.sh" | sort)
fi

# ── 1. Linting ──
LINT_ERRORS=0

lint_single_script() {
    local script="$1"
    local base="$(basename "$script" .sh)"
    local rel="$script"
    if [[ "$script" == "$SCENARIO_ROOT/"* ]]; then
        rel="${script#$SCENARIO_ROOT/}"
    fi

    local errs=()

    # Line 1: shebang
    local line1
    line1="$(head -n 1 "$script" | tr -d '\r')"
    if [[ "$line1" != "#!/usr/bin/env bash" ]]; then
        errs+=("Line 1 must be '#!/usr/bin/env bash' (got: '$line1')")
    fi

    # Line 2: # @short: <= 72 characters
    local line2
    line2="$(sed -n '2p' "$script" | tr -d '\r')"
    if [[ "$line2" != "# @short:"* ]]; then
        errs+=("Line 2 must start with '# @short:' (got: '$line2')")
    else
        local text="${line2#\# @short:}"
        # trim leading whitespace
        text="${text#"${text%%[![:space:]]*}"}"
        if [[ "${#text}" -gt 72 ]]; then
            errs+=("# @short: text length (${#text}) exceeds 72 characters: '$text'")
        fi
    fi

    # Invocations
    if ! grep -q 'source "\$SCENARIO_LIB"' "$script"; then
        errs+=("Missing 'source \"\$SCENARIO_LIB\"'")
    fi
    if ! grep -q '^[[:space:]]*lab_begin' "$script"; then
        errs+=("Missing 'lab_begin'")
    fi
    if ! grep -q '^[[:space:]]*lab_done' "$script"; then
        errs+=("Missing 'lab_done'")
    fi

    # Basename validation
    if ! [[ "$base" =~ ^[A-Za-z0-9._-]+$ ]]; then
        errs+=("Basename '$base' contains invalid characters")
    fi
    if [[ "$base" == _* ]]; then
        errs+=("Basename '$base' cannot start with an underscore")
    fi
    if [[ -n "${ALL_SCRIPTS_MAP[$base]:-}" ]] && [[ "${ALL_SCRIPTS_MAP[$base]}" == *"|"* ]]; then
        errs+=("Basename '$base' is not unique under scripts/: ${ALL_SCRIPTS_MAP[$base]}")
    fi

    # File bytes: no CR, BOM, or NUL
    if grep -U -q $'\r' "$script"; then
        errs+=("File contains carriage return (CR) bytes")
    fi
    if [[ "$(head -c 3 "$script")" = "$(printf '\xef\xbb\xbf')" ]]; then
        errs+=("File contains a UTF-8 BOM")
    fi
    if [[ "$(wc -c < "$script")" -ne "$(tr -d '\0' < "$script" | wc -c)" ]]; then
        errs+=("File contains NUL bytes")
    fi

    # No network URLs
    if grep -E -q 'https?://' "$script"; then
        errs+=("File contains network URL (http:// or https://)")
    fi

    # Template usage
    if grep -E -q '^[[:space:]]*lab_save_template' "$script"; then
        errs+=("File calls lab_save_template")
    fi
    while IFS= read -r match; do
        if [[ -n "$match" ]]; then
            if ! [[ "$match" =~ ^[[:space:]]*lab_use_template[[:space:]]+_base([[:space:]]|$) ]]; then
                errs+=("Illegal lab_use_template call (only 'lab_use_template _base' allowed): '$match'")
            fi
        fi
    done < <(grep -E '^[[:space:]]*lab_use_template' "$script" || true)

    if [[ "${#errs[@]}" -gt 0 ]]; then
        echo "[LINT FAIL] $rel" >&2
        for e in "${errs[@]}"; do
            echo "  - $e" >&2
        done
        LINT_ERRORS=$(( LINT_ERRORS + 1 ))
        return 1
    fi
    return 0
}

for s in "${SCRIPT_FILES[@]}"; do
    lint_single_script "$s" || true
done

if [[ "$LINT_ONLY" -eq 1 ]]; then
    if [[ "$LINT_ERRORS" -gt 0 ]]; then
        echo "Lint failed with $LINT_ERRORS error(s)." >&2
        exit 1
    fi
    echo "Lint clean (${#SCRIPT_FILES[@]} script(s))."
    exit 0
fi

if [[ "$LINT_ERRORS" -gt 0 ]]; then
    echo "Aborting run due to $LINT_ERRORS lint error(s)." >&2
    exit 1
fi

# ── 2. Execution & Fingerprinting ──

PASS_COUNT=0
SKIP_COUNT=0
FAIL_COUNT=0

generate_fingerprint() {
    local sandbox="$1"
    local fp_file="$2"

    local repos=()
    if [[ -d "$sandbox/local1" ]]; then repos+=("$sandbox/local1"); fi
    if [[ -d "$sandbox/local2" ]]; then repos+=("$sandbox/local2"); fi
    if [[ -d "$sandbox/remotes" ]]; then
        for r in "$sandbox/remotes"/*; do
            if [[ -d "$r" ]]; then repos+=("$r"); fi
        done
    fi

    for r in "${repos[@]}"; do
        local rel_name="${r#$sandbox/}"
        echo "=== REPO: $rel_name ===" >> "$fp_file"
        git -C "$r" for-each-ref --format='%(refname) %(objectname)' 2>/dev/null | sort >> "$fp_file" || true
        (git -C "$r" rev-parse HEAD 2>/dev/null || echo "HEAD: empty") >> "$fp_file"
        if [[ -d "$r/.git" ]]; then
            git -C "$r" ls-files -s 2>/dev/null | sort >> "$fp_file" || true
            git -C "$r" status --porcelain=v1 2>/dev/null >> "$fp_file" || true
        fi

        local gdir
        gdir="$(git -C "$r" rev-parse --git-dir 2>/dev/null || true)"
        if [[ -n "$gdir" ]]; then
            if [[ "$gdir" != /* && "$gdir" != [A-Za-z]:* ]]; then
                gdir="$r/$gdir"
            fi
        fi
        if [[ -n "$gdir" ]] && [[ -d "$gdir" ]]; then
            for sf in MERGE_HEAD CHERRY_PICK_HEAD REVERT_HEAD REBASE_HEAD rebase-merge/msgnum BISECT_LOG; do
                if [[ -f "$gdir/$sf" ]]; then
                    echo "[$sf]" >> "$fp_file"
                    cat "$gdir/$sf" >> "$fp_file"
                fi
            done
        fi

        if [[ -d "$r/.git" ]]; then
            (
                cd "$r"
                find . -name '.git' -prune -o -type f -print | sort | while read -r f; do
                    rel="${f#./}"
                    sha="$(sha256sum "$f" | awk '{print $1}')"
                    echo "$sha  $rel"
                done
            ) >> "$fp_file"
        fi
    done
}

run_in_sandbox() {
    local script="$1"
    local sandbox="$2"

    mkdir -p "$sandbox/local1" "$sandbox/local2" "$sandbox/remotes" "$sandbox/run"

    local s_name
    s_name="$(basename "$script" .sh)"

    export SCENARIO_LOCAL1="$sandbox/local1"
    export SCENARIO_LOCAL2="$sandbox/local2"
    export SCENARIO_REMOTES="$sandbox/remotes"
    export SCENARIO_TEMPLATES="$SCENARIO_ROOT/templates"
    export SCENARIO_LIB="$SCENARIO_ROOT/lib/scenario.sh"
    export SCENARIO_ROOT="$SCENARIO_ROOT"
    export SCENARIO_NAME="$s_name"
    export SCENARIO_HEADLESS=1

    export GIT_TERMINAL_PROMPT=0
    export GIT_EDITOR=true
    export GIT_SEQUENCE_EDITOR=true
    export GIT_PAGER=cat
    export PAGER=cat

    local abs_script
    abs_script="$(cd "$(dirname "$script")" && pwd)/$(basename "$script")"

    set +e
    (
        cd "$sandbox/run"
        bash --noprofile --norc "$abs_script" </dev/null > "$sandbox/output.log" 2>&1
    )
    local code=$?
    set -e
    return "$code"
}

cleanup_sandbox() {
    local sb="$1"
    if [[ "$KEEP" -eq 1 ]]; then
        echo "[INFO] Kept sandbox: $sb"
    else
        rm -rf "$sb"
    fi
}

for script in "${SCRIPT_FILES[@]}"; do
    s_name="$(basename "$script" .sh)"
    rel_path="$script"
    if [[ "$script" == "$SCENARIO_ROOT/"* ]]; then
        rel_path="${script#$SCENARIO_ROOT/}"
    fi

    if [[ "$TWICE" -eq 1 ]]; then
        sb1="$(mktemp -d)"
        sb2="$(mktemp -d)"

        set +e
        run_in_sandbox "$script" "$sb1"
        code1=$?
        set -e

        if [[ "$code1" -eq 3 ]]; then
            skip_msg="$(tail -n 5 "$sb1/output.log" | grep '\[SKIP\]' | head -n 1 || echo "[SKIP]")"
            echo "SKIP: $rel_path ($skip_msg)"
            SKIP_COUNT=$(( SKIP_COUNT + 1 ))
            cleanup_sandbox "$sb1"
            cleanup_sandbox "$sb2"
            continue
        fi

        if [[ "$code1" -ne 0 ]]; then
            echo "FAIL: $rel_path (run 1 exited with code $code1)" >&2
            tail -n 40 "$sb1/output.log" >&2
            FAIL_COUNT=$(( FAIL_COUNT + 1 ))
            cleanup_sandbox "$sb1"
            cleanup_sandbox "$sb2"
            continue
        fi

        set +e
        run_in_sandbox "$script" "$sb2"
        code2=$?
        set -e

        if [[ "$code2" -ne 0 ]]; then
            echo "FAIL: $rel_path (run 2 exited with code $code2)" >&2
            tail -n 40 "$sb2/output.log" >&2
            FAIL_COUNT=$(( FAIL_COUNT + 1 ))
            cleanup_sandbox "$sb1"
            cleanup_sandbox "$sb2"
            continue
        fi

        fp1="$sb1/fp.txt"
        fp2="$sb2/fp.txt"
        generate_fingerprint "$sb1" "$fp1"
        generate_fingerprint "$sb2" "$fp2"

        if cmp -s "$fp1" "$fp2"; then
            echo "PASS: $rel_path"
            PASS_COUNT=$(( PASS_COUNT + 1 ))
        else
            echo "FAIL: $rel_path (not deterministic across two runs)" >&2
            diff -u "$fp1" "$fp2" | head -n 40 >&2 || true
            FAIL_COUNT=$(( FAIL_COUNT + 1 ))
        fi

        cleanup_sandbox "$sb1"
        cleanup_sandbox "$sb2"
    else
        sb="$(mktemp -d)"
        set +e
        run_in_sandbox "$script" "$sb"
        code=$?
        set -e

        if [[ "$code" -eq 0 ]]; then
            echo "PASS: $rel_path"
            PASS_COUNT=$(( PASS_COUNT + 1 ))
        elif [[ "$code" -eq 3 ]]; then
            skip_msg="$(tail -n 5 "$sb/output.log" | grep '\[SKIP\]' | head -n 1 || echo "[SKIP]")"
            echo "SKIP: $rel_path ($skip_msg)"
            SKIP_COUNT=$(( SKIP_COUNT + 1 ))
        else
            echo "FAIL: $rel_path (exited with code $code)" >&2
            tail -n 40 "$sb/output.log" >&2
            FAIL_COUNT=$(( FAIL_COUNT + 1 ))
        fi

        cleanup_sandbox "$sb"
    fi
done

echo ""
echo "PASS $PASS_COUNT · SKIP $SKIP_COUNT · FAIL $FAIL_COUNT"

if [[ "$FAIL_COUNT" -gt 0 ]]; then
    exit 1
fi
exit 0
