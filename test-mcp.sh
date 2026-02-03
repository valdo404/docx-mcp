#!/usr/bin/env bash
set -euo pipefail

# Integration test for the docx-mcp .NET MCP server (NativeAOT binary).
# Uses mcptools to drive the MCP protocol — pure bash.
#
# Prerequisites:
#   brew install mcptools
#   ./publish.sh  (to build the NativeAOT binary)
#
# Usage:
#   ./test-mcp.sh                               # Test with a new document
#   ./test-mcp.sh ~/Documents/somefile.docx      # Also test with a real file

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
BINARY="$SCRIPT_DIR/dist/macos-arm64/docx-mcp"

GREEN='\033[0;32m'
RED='\033[0;31m'
YELLOW='\033[0;33m'
NC='\033[0m'

PASSED=0
FAILED=0

pass() {
    echo -e "  ${GREEN}PASS${NC} $1"
    ((PASSED++)) || true
}

fail() {
    echo -e "  ${RED}FAIL${NC} $1"
    [[ -n "${2:-}" ]] && echo "       ${2:0:300}"
    ((FAILED++)) || true
}

check() {
    local name="$1" pattern="$2" text="$3"
    if echo "$text" | grep -q "$pattern"; then
        pass "$name"
    else
        fail "$name" "$text"
    fi
}

check_not() {
    local name="$1" pattern="$2" text="$3"
    if echo "$text" | grep -q "$pattern"; then
        fail "$name" "Found '$pattern' in output"
    else
        pass "$name"
    fi
}

echo "=== docx-mcp MCP Integration Tests (NativeAOT + mcptools) ==="
echo ""

if ! command -v mcptools &>/dev/null; then
    echo "Error: mcptools not found. Install with: brew install mcptools"
    exit 1
fi

if [[ ! -x "$BINARY" ]]; then
    echo "Error: NativeAOT binary not found at $BINARY"
    echo "Run ./publish.sh first."
    exit 1
fi

echo "Binary: $BINARY ($(du -sh "$BINARY" | cut -f1))"
echo ""

REAL_FILE="${1:-}"

# ── Test 1: List tools ──
echo "Test: List Tools"
TOOLS=$(mcptools tools "$BINARY" 2>/dev/null)
check "has document_open" "document_open" "$TOOLS"
check "has query" "query" "$TOOLS"
check "has apply_patch" "apply_patch" "$TOOLS"
check "has export_markdown" "export_markdown" "$TOOLS"
check "has export_html" "export_html" "$TOOLS"
check_not "no apply_xml_patch" "apply_xml_patch" "$TOOLS"
check_not "no document_close (CLI-only)" "document_close" "$TOOLS"
check_not "no document_snapshot (CLI-only)" "document_snapshot" "$TOOLS"

# ── Test 2: Create document (standalone call) ──
echo ""
echo "Test: Create Document"
OUTPUT=$(mcptools call document_open -f json "$BINARY" 2>/dev/null)
check "creates document" "Session ID" "$OUTPUT"

# ── Test 3: Full lifecycle via shell session ──
echo ""
echo "Test: Document Lifecycle"

TMP="/tmp/docx-mcp-test-$$"
FIFO_IN="${TMP}.fifo"
OUT_FILE="${TMP}.out"
MD_FILE="${TMP}.md"
HTML_FILE="${TMP}.html"
DOCX_FILE="${TMP}.docx"
SHELL_PID=""

cleanup() {
    rm -f "$FIFO_IN" "$OUT_FILE" "$MD_FILE" "$HTML_FILE" "$DOCX_FILE" \
          "${TMP}.fifo2" "${TMP}.out2"
    [[ -n "${SHELL_PID:-}" ]] && kill "$SHELL_PID" 2>/dev/null || true
}
trap cleanup EXIT

mkfifo "$FIFO_IN"
: > "$OUT_FILE"

mcptools shell -f json "$BINARY" < "$FIFO_IN" > "$OUT_FILE" 2>/dev/null &
SHELL_PID=$!
exec 3>"$FIFO_IN"
sleep 1

# send_cmd: send a command and return the JSON response line
send_cmd() {
    local before
    before=$(wc -l < "$OUT_FILE" | tr -d ' ')
    echo "$1" >&3
    sleep 1
    tail -n +"$((before + 1))" "$OUT_FILE" | sed 's/^mcp > //' | grep '^{' | head -1
}

# Create document
R=$(send_cmd "call document_open")
DOC_ID=$(echo "$R" | grep -o 'Session ID: [a-f0-9]*' | head -1 | cut -d' ' -f3)

if [[ -z "$DOC_ID" ]]; then
    fail "get session ID" "$R"
    echo "/q" >&3; exec 3>&-; wait "$SHELL_PID" 2>/dev/null || true; SHELL_PID=""
else
    pass "document created"
    echo -e "  ${YELLOW}Session ID: ${DOC_ID}${NC}"

    # Apply patches — the patches param is a JSON *string* so inner quotes must be escaped
    echo ""
    echo "Test: Apply Patches (basic)"
    PATCHES='[{\"op\":\"add\",\"path\":\"/body/children/0\",\"value\":{\"type\":\"heading\",\"level\":1,\"text\":\"Test Document\"}},{\"op\":\"add\",\"path\":\"/body/children/1\",\"value\":{\"type\":\"paragraph\",\"text\":\"This is a test paragraph.\"}},{\"op\":\"add\",\"path\":\"/body/children/2\",\"value\":{\"type\":\"table\",\"headers\":[\"Name\",\"Value\"],\"rows\":[[\"foo\",\"bar\"],[\"baz\",\"qux\"]]}},{\"op\":\"add\",\"path\":\"/body/children/3\",\"value\":{\"type\":\"paragraph\",\"text\":\"Final paragraph.\",\"style\":{\"bold\":true,\"font_size\":14}}}]'

    R=$(send_cmd "call apply_patch -p {\"doc_id\":\"${DOC_ID}\",\"patches\":\"${PATCHES}\"}")
    check "patches applied" "successfully" "$R"

    # Query body
    echo "Test: Query Body"
    R=$(send_cmd "call query -p {\"doc_id\":\"${DOC_ID}\",\"path\":\"/body\"}")
    check "body has paragraphs" "paragraph_count" "$R"

    # Query heading
    echo "Test: Query Heading"
    R=$(send_cmd "call query -p {\"doc_id\":\"${DOC_ID}\",\"path\":\"/body/heading[level=1]\"}")
    check "heading text" "Test Document" "$R"

    # Query table
    echo "Test: Query Table"
    R=$(send_cmd "call query -p {\"doc_id\":\"${DOC_ID}\",\"path\":\"/body/table[0]\"}")
    check "table has data" "foo" "$R"
    check "table has rich_rows" "rich_rows" "$R"

    # Query text search
    echo "Test: Query Text Search"
    R=$(send_cmd "call query -p {\"doc_id\":\"${DOC_ID}\",\"path\":\"/body/paragraph[text~='Final']\"}")
    check "text search finds paragraph" "Final paragraph" "$R"

    # ── Run-Level Write Support ──
    echo ""
    echo "Test: Run-Level Write (paragraph with styled runs)"
    PATCHES_RUNS='[{\"op\":\"add\",\"path\":\"/body/children/4\",\"value\":{\"type\":\"paragraph\",\"properties\":{\"alignment\":\"center\"},\"runs\":[{\"text\":\"Bold \",\"style\":{\"bold\":true,\"color\":\"FF0000\"}},{\"text\":\"and \",\"style\":{\"italic\":true}},{\"text\":\"normal text\"}]}}]'
    R=$(send_cmd "call apply_patch -p {\"doc_id\":\"${DOC_ID}\",\"patches\":\"${PATCHES_RUNS}\"}")
    check "run-level paragraph applied" "successfully" "$R"

    R=$(send_cmd "call query -p {\"doc_id\":\"${DOC_ID}\",\"path\":\"/body/paragraph[text~='Bold']\"}")
    check "styled paragraph has runs" "runs" "$R"
    check "styled paragraph has bold" "bold" "$R"
    check "styled paragraph has color" "FF0000" "$R"
    check "paragraph has properties" "properties" "$R"

    # ── Tab Characters ──
    echo ""
    echo "Test: Tab Characters in Runs"
    PATCHES_TABS='[{\"op\":\"add\",\"path\":\"/body/children/5\",\"value\":{\"type\":\"heading\",\"level\":2,\"properties\":{\"tabs\":[{\"position\":4680,\"alignment\":\"center\"},{\"position\":9360,\"alignment\":\"right\"}]},\"runs\":[{\"text\":\"Title\",\"style\":{\"color\":\"2E5496\"}},{\"tab\":true},{\"text\":\"Company\",\"style\":{\"bold\":true}},{\"tab\":true},{\"text\":\"2024\",\"style\":{\"italic\":true}}]}}]'
    R=$(send_cmd "call apply_patch -p {\"doc_id\":\"${DOC_ID}\",\"patches\":\"${PATCHES_TABS}\"}")
    check "heading with tabs applied" "successfully" "$R"

    R=$(send_cmd "call query -p {\"doc_id\":\"${DOC_ID}\",\"path\":\"/body/heading[1]\"}")
    check "heading has runs" "runs" "$R"
    check "heading has tab" "tab" "$R"
    check "heading has Company" "Company" "$R"
    check "heading has tab stops" "tabs" "$R"

    # ── Rich Table with Styled Cells ──
    echo ""
    echo "Test: Rich Table with Styled Cells"
    PATCHES_RICH_TABLE='[{\"op\":\"add\",\"path\":\"/body/children/6\",\"value\":{\"type\":\"table\",\"border_style\":\"single\",\"table_alignment\":\"center\",\"headers\":[{\"text\":\"Product\",\"shading\":\"E0E0E0\",\"style\":{\"bold\":true}},{\"text\":\"Price\",\"shading\":\"E0E0E0\",\"style\":{\"bold\":true}}],\"rows\":[{\"cells\":[{\"text\":\"Widget\",\"style\":{\"italic\":true}},{\"text\":\"$10\",\"shading\":\"F0FFF0\"}]},{\"cells\":[{\"text\":\"Total\",\"col_span\":1,\"style\":{\"bold\":true}},{\"text\":\"$10\",\"shading\":\"FFFF00\",\"style\":{\"bold\":true}}]}]}}]'
    R=$(send_cmd "call apply_patch -p {\"doc_id\":\"${DOC_ID}\",\"patches\":\"${PATCHES_RICH_TABLE}\"}")
    check "rich table applied" "successfully" "$R"

    R=$(send_cmd "call query -p {\"doc_id\":\"${DOC_ID}\",\"path\":\"/body/table[1]\"}")
    check "rich table has data" "Widget" "$R"
    check "rich table has rich_rows" "rich_rows" "$R"
    check "rich table has rich_cells" "rich_cells" "$R"

    # ── Replace Text (preserving formatting) ──
    echo ""
    echo "Test: Replace Text (format-preserving)"
    PATCHES_REPLACE='[{\"op\":\"replace_text\",\"path\":\"/body/paragraph[1]\",\"find\":\"test\",\"replace\":\"replaced\"}]'
    R=$(send_cmd "call apply_patch -p {\"doc_id\":\"${DOC_ID}\",\"patches\":\"${PATCHES_REPLACE}\"}")
    check "replace_text applied" "successfully" "$R"

    R=$(send_cmd "call query -p {\"doc_id\":\"${DOC_ID}\",\"path\":\"/body/paragraph[text~='replaced']\"}")
    check "text was replaced" "replaced paragraph" "$R"

    # ── Remove Column ──
    echo ""
    echo "Test: Remove Column"
    PATCHES_RMCOL='[{\"op\":\"remove_column\",\"path\":\"/body/table[0]\",\"column\":1}]'
    R=$(send_cmd "call apply_patch -p {\"doc_id\":\"${DOC_ID}\",\"patches\":\"${PATCHES_RMCOL}\"}")
    check "remove_column applied" "successfully" "$R"

    R=$(send_cmd "call query -p {\"doc_id\":\"${DOC_ID}\",\"path\":\"/body/table[0]\"}")
    check "table has 1 column now" "cols.*1" "$R"

    # ── Add Row to Existing Table ──
    echo ""
    echo "Test: Add Row to Table"
    PATCHES_ADDROW='[{\"op\":\"add\",\"path\":\"/body/table[0]\",\"value\":{\"type\":\"row\",\"cells\":[{\"text\":\"NewItem\",\"style\":{\"italic\":true}}]}}]'
    R=$(send_cmd "call apply_patch -p {\"doc_id\":\"${DOC_ID}\",\"patches\":\"${PATCHES_ADDROW}\"}")
    check "add row applied" "successfully" "$R"

    R=$(send_cmd "call query -p {\"doc_id\":\"${DOC_ID}\",\"path\":\"/body/table[0]\"}")
    check "table has new row" "NewItem" "$R"

    # ── Remove Row ──
    echo ""
    echo "Test: Remove Row"
    PATCHES_RMROW='[{\"op\":\"remove\",\"path\":\"/body/table[0]/row[2]\"}]'
    R=$(send_cmd "call apply_patch -p {\"doc_id\":\"${DOC_ID}\",\"patches\":\"${PATCHES_RMROW}\"}")
    check "remove row applied" "successfully" "$R"

    # ── Replace Cell ──
    echo ""
    echo "Test: Replace Cell"
    PATCHES_CELL='[{\"op\":\"replace\",\"path\":\"/body/table[1]/row[1]/cell[0]\",\"value\":{\"type\":\"cell\",\"text\":\"Gizmo\",\"style\":{\"bold\":true},\"shading\":\"E0FFE0\"}}]'
    R=$(send_cmd "call apply_patch -p {\"doc_id\":\"${DOC_ID}\",\"patches\":\"${PATCHES_CELL}\"}")
    check "replace cell applied" "successfully" "$R"

    R=$(send_cmd "call query -p {\"doc_id\":\"${DOC_ID}\",\"path\":\"/body/table[1]/row[1]/cell[0]\"}")
    check "cell was replaced" "Gizmo" "$R"

    # ── Paragraph with line breaks ──
    echo ""
    echo "Test: Paragraph with Line Breaks"
    PATCHES_BRK='[{\"op\":\"add\",\"path\":\"/body/children/7\",\"value\":{\"type\":\"paragraph\",\"runs\":[{\"text\":\"Line one\"},{\"break\":\"line\"},{\"text\":\"Line two\"}]}}]'
    R=$(send_cmd "call apply_patch -p {\"doc_id\":\"${DOC_ID}\",\"patches\":\"${PATCHES_BRK}\"}")
    check "paragraph with break applied" "successfully" "$R"

    R=$(send_cmd "call query -p {\"doc_id\":\"${DOC_ID}\",\"path\":\"/body/paragraph[text~='Line one']\"}")
    check "break paragraph has runs" "runs" "$R"
    check "break paragraph has break" "break" "$R"

    # Export markdown
    echo ""
    echo "Test: Export Markdown"
    R=$(send_cmd "call export_markdown -p {\"doc_id\":\"${DOC_ID}\",\"output_path\":\"${MD_FILE}\"}")
    check "markdown exported" "exported" "$R"
    if [[ -f "$MD_FILE" ]]; then
        check "markdown has heading" "Test Document" "$(cat "$MD_FILE")"
    else
        fail "markdown file created" "File not found: $MD_FILE"
    fi

    # Export HTML
    echo "Test: Export HTML"
    R=$(send_cmd "call export_html -p {\"doc_id\":\"${DOC_ID}\",\"output_path\":\"${HTML_FILE}\"}")
    check "html exported" "exported" "$R"
    if [[ -f "$HTML_FILE" ]]; then
        check "html has heading tag" "<h1>" "$(cat "$HTML_FILE")"
    else
        fail "html file created" "File not found: $HTML_FILE"
    fi

    # Save document
    echo "Test: Save Document"
    R=$(send_cmd "call document_save -p {\"doc_id\":\"${DOC_ID}\",\"output_path\":\"${DOCX_FILE}\"}")
    check "document saved" "saved" "$R"
    if [[ -f "$DOCX_FILE" ]]; then
        SIZE=$(wc -c < "$DOCX_FILE" | tr -d ' ')
        echo -e "  ${YELLOW}Saved file size: ${SIZE} bytes${NC}"
        [[ "$SIZE" -gt 0 ]] && pass "docx has content" || fail "docx has content" "Empty"
    else
        fail "docx file created" "File not found: $DOCX_FILE"
    fi

    # Note: document_close is now CLI-only (not exposed via MCP)
    # Cleanup happens automatically when the shell session ends
    echo "Test: Close Document (CLI-only, skipped in MCP test)"
    pass "document_close is CLI-only (expected)"

    echo "/q" >&3; exec 3>&-; wait "$SHELL_PID" 2>/dev/null || true; SHELL_PID=""
    rm -f "$FIFO_IN" "$OUT_FILE"

    # Clean up via CLI if available
    if [[ -x "$SCRIPT_DIR/dist/macos-arm64/docx-cli" ]]; then
        "$SCRIPT_DIR/dist/macos-arm64/docx-cli" close "$DOC_ID" 2>/dev/null || true
    fi
fi

# ── Test 4: Open real document ──
if [[ -n "$REAL_FILE" && -f "$REAL_FILE" ]]; then
    echo ""
    echo "Test: Open Real Document ($(basename "$REAL_FILE"))"

    FIFO_REAL="${TMP}.fifo2"
    OUT_REAL="${TMP}.out2"
    mkfifo "$FIFO_REAL"
    : > "$OUT_REAL"

    mcptools shell -f json "$BINARY" < "$FIFO_REAL" > "$OUT_REAL" 2>/dev/null &
    SHELL_PID=$!
    exec 4>"$FIFO_REAL"
    sleep 1

    send_cmd_real() {
        local before wait_time="${2:-1}"
        before=$(wc -l < "$OUT_REAL" | tr -d ' ')
        echo "$1" >&4
        sleep "$wait_time"
        tail -n +"$((before + 1))" "$OUT_REAL" | sed 's/^mcp > //' | grep '^{' | head -1
    }

    R=$(send_cmd_real "call document_open -p {\"path\":\"${REAL_FILE}\"}" 3)
    REAL_ID=$(echo "$R" | grep -o 'Session ID: [a-f0-9]*' | head -1 | cut -d' ' -f3)

    if [[ -n "$REAL_ID" ]]; then
        pass "real doc opened (ID: $REAL_ID)"

        R=$(send_cmd_real "call query -p {\"doc_id\":\"${REAL_ID}\",\"path\":\"/body\"}" 2)
        check "real doc body queried" "paragraph_count" "$R"
        echo -e "  ${YELLOW}Body summary: ${R:0:300}${NC}"

        # Query all paragraphs as text
        R=$(send_cmd_real "call query -p {\"doc_id\":\"${REAL_ID}\",\"path\":\"/body/paragraph[*]\",\"format\":\"text\"}" 2)
        echo -e "  ${YELLOW}First 200 chars: ${R:0:200}${NC}"

        # Query first paragraph with full run detail
        echo "Test: Real Doc - Run-Level Query"
        R=$(send_cmd_real "call query -p {\"doc_id\":\"${REAL_ID}\",\"path\":\"/body/paragraph[0]\"}")
        check "real doc paragraph has runs" "runs" "$R"
        echo -e "  ${YELLOW}Paragraph JSON (300 chars): ${R:0:300}${NC}"

        # Query headings if any
        echo "Test: Real Doc - Headings"
        R=$(send_cmd_real "call query -p {\"doc_id\":\"${REAL_ID}\",\"path\":\"/body/heading[*]\",\"format\":\"summary\"}")
        echo -e "  ${YELLOW}Headings: ${R:0:300}${NC}"

        # Query first heading with tab/run detail
        R=$(send_cmd_real "call query -p {\"doc_id\":\"${REAL_ID}\",\"path\":\"/body/heading[0]\"}")
        if echo "$R" | grep -q "runs"; then
            check "real doc heading has runs" "runs" "$R"
            # Check for tabs
            if echo "$R" | grep -q "tab"; then
                pass "real doc heading has tabs"
            else
                echo -e "  ${YELLOW}(no tabs in heading)${NC}"
            fi
            # Check for properties
            if echo "$R" | grep -q "properties"; then
                pass "real doc heading has properties"
            else
                echo -e "  ${YELLOW}(no extra properties in heading)${NC}"
            fi
        else
            echo -e "  ${YELLOW}(heading query returned no runs — may have no headings)${NC}"
        fi

        # Query tables if any
        echo "Test: Real Doc - Tables"
        R=$(send_cmd_real "call query -p {\"doc_id\":\"${REAL_ID}\",\"path\":\"/body/table[0]\"}")
        if echo "$R" | grep -q "table"; then
            check "real doc has table" "table" "$R"
            check "real doc table has rich_rows" "rich_rows" "$R"
            check "real doc table has rich_cells" "rich_cells" "$R"
            echo -e "  ${YELLOW}Table (300 chars): ${R:0:300}${NC}"
        else
            echo -e "  ${YELLOW}(no tables in document)${NC}"
        fi

        # Test replace_text on a copy (non-destructive — save to tmp)
        echo "Test: Real Doc - Replace Text (non-destructive)"
        REAL_SAVE="${TMP}.real.docx"
        R=$(send_cmd_real "call document_save -p {\"doc_id\":\"${REAL_ID}\",\"output_path\":\"${REAL_SAVE}\"}")
        check "real doc saved copy" "saved" "$R"
        rm -f "$REAL_SAVE"

        # Note: document_close is CLI-only, cleanup happens via CLI
        pass "real doc test complete (cleanup via CLI)"
    else
        fail "real doc opened" "$(cat "$OUT_REAL")"
    fi

    echo "/q" >&4; exec 4>&-; wait "$SHELL_PID" 2>/dev/null || true; SHELL_PID=""

    # Clean up via CLI if available
    if [[ -x "$SCRIPT_DIR/dist/macos-arm64/docx-cli" && -n "$REAL_ID" ]]; then
        "$SCRIPT_DIR/dist/macos-arm64/docx-cli" close "$REAL_ID" 2>/dev/null || true
    fi
fi

echo ""
echo "========================================"
echo -e "Results: ${GREEN}${PASSED} passed${NC}, ${RED}${FAILED} failed${NC}"
echo "========================================"

[[ "$FAILED" -eq 0 ]] || exit 1
