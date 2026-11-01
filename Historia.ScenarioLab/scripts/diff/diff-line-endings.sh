#!/usr/bin/env bash
# @short: Working tree and index with CRLF in index and non-standard line endings
# @long:
#  State:
#  - Local1: no operation in progress.
#    - .gitattributes: crlf/** -text, ws/** text.
#    - crlf/index-crlf.txt: committed with CRLF, with 2 unstaged hunks and 1 staged hunk (MM).
#    - crlf/lone-cr.txt: content with lone \r mid-line, edited.
#    - crlf/mixed.txt: one line's ending changed LF->CRLF and nothing else.
#    - ws/whitespace.cs: whitespace-only edits (trailing spaces, tab->spaces).
#
#  Check:
#  - B15 (hunk and line staging succeed; git show :crlf/index-crlf.txt keeps CRLF).
#  - Lone-CR display and copy.
#  - Manual check 9.
# @template: _base

source "$SCENARIO_LIB"
lab_begin

lab_use_template _base
lab_pin_config "$SCENARIO_LOCAL1"
lab_pin_config "$SCENARIO_LOCAL2"

mkdir -p "$SCENARIO_LOCAL1/crlf" "$SCENARIO_LOCAL1/ws"

cat << 'EOF' > "$SCENARIO_LOCAL1/.gitattributes"
crlf/** -text
ws/** text
EOF

# 1. crlf/index-crlf.txt baseline: 30 lines with pure CRLF endings
for i in $(seq -w 1 30); do
    printf "line %s with CRLF\r\n" "$i"
done > "$SCENARIO_LOCAL1/crlf/index-crlf.txt"

# 2. crlf/lone-cr.txt baseline: contains a lone \r mid-line
printf "first line\r\nmiddle lone\rmidline\r\nlast line\r\n" > "$SCENARIO_LOCAL1/crlf/lone-cr.txt"

# 3. crlf/mixed.txt baseline: 5 lines with pure LF
printf "alpha\nbeta\ngamma\ndelta\nepsilon\n" > "$SCENARIO_LOCAL1/crlf/mixed.txt"

# 4. ws/whitespace.cs baseline: clean tabs
printf "public class WhitespaceService\n{\n\tpublic void DoWork()\n\t{\n\t\treturn;\n\t}\n}\n" > "$SCENARIO_LOCAL1/ws/whitespace.cs"

lab_commit "$SCENARIO_LOCAL1" "chore: add line endings baseline files"

# ── 5. Edit crlf/index-crlf.txt: 1 staged hunk, 2 unstaged hunks ──
# Stage hunk at line 5 (pure CRLF)
for i in $(seq -w 1 30); do
    if [[ "$i" == 05 ]]; then
        printf "line %s STAGED CRLF\r\n" "$i"
    else
        printf "line %s with CRLF\r\n" "$i"
    fi
done > "$SCENARIO_LOCAL1/crlf/index-crlf.txt"
git -C "$SCENARIO_LOCAL1" add crlf/index-crlf.txt

# Unstaged hunks at line 15 and line 25 (pure CRLF)
for i in $(seq -w 1 30); do
    if [[ "$i" == 05 ]]; then
        printf "line %s STAGED CRLF\r\n" "$i"
    elif [[ "$i" == 15 ]]; then
        printf "line %s UNSTAGED 1 CRLF\r\n" "$i"
    elif [[ "$i" == 25 ]]; then
        printf "line %s UNSTAGED 2 CRLF\r\n" "$i"
    else
        printf "line %s with CRLF\r\n" "$i"
    fi
done > "$SCENARIO_LOCAL1/crlf/index-crlf.txt"

# ── 6. Edit crlf/lone-cr.txt ──
printf "first line edited\r\nmiddle lone\rmidline\r\nlast line\r\n" > "$SCENARIO_LOCAL1/crlf/lone-cr.txt"

# ── 7. Edit crlf/mixed.txt: change line 3 from LF to CRLF ──
printf "alpha\nbeta\ngamma\r\ndelta\nepsilon\n" > "$SCENARIO_LOCAL1/crlf/mixed.txt"

# ── 8. Edit ws/whitespace.cs: trailing spaces and tab->spaces ──
printf "public class WhitespaceService\n{\n\tpublic void DoWork()   \n\t{\n        return;\n\t}\n}\n" > "$SCENARIO_LOCAL1/ws/whitespace.cs"

# ── Self-checks ──
lab_assert_op "$SCENARIO_LOCAL1" none
lab_assert "index-crlf.txt is i/crlf in index" [ -n "$(git -C "$SCENARIO_LOCAL1" ls-files --eol crlf/index-crlf.txt | grep 'i/crlf')" ]
CR_BYTE="$(printf '\r')"
lab_assert "git diff crlf/index-crlf.txt contains CR byte" grep -q -U "$CR_BYTE" <(git -C "$SCENARIO_LOCAL1" diff crlf/index-crlf.txt)
lab_assert_status "$SCENARIO_LOCAL1" crlf/index-crlf.txt "MM"
lab_assert "lone CR present in lone-cr.txt" awk 'BEGIN { found = 0 } /\r[^\n]/ || /\r$/ { found = 1 } END { exit (found ? 0 : 1) }' "$SCENARIO_LOCAL1/crlf/lone-cr.txt"

lab_done
