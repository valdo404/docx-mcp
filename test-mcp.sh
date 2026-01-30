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
    echo "Test: Apply Patches"
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

    # Query text search
    echo "Test: Query Text Search"
    R=$(send_cmd "call query -p {\"doc_id\":\"${DOC_ID}\",\"path\":\"/body/paragraph[text~='Final']\"}")
    check "text search finds paragraph" "Final paragraph" "$R"

    # Export markdown
    echo "Test: Export Markdown"
    R=$(send_cmd "call export_markdown -p {\"doc_id\":\"${DOC_ID}\",\"output_path\":\"${MD_FILE}\"}")
    check "markdown exported" "exported" "$R"
    if [[ -f "$MD_FILE" ]]; then
        check "markdown has heading" "Test Document" "$(cat "$MD_FILE")"
        check "markdown has table data" "foo" "$(cat "$MD_FILE")"
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

    # Close document
    echo "Test: Close Document"
    R=$(send_cmd "call document_close -p {\"doc_id\":\"${DOC_ID}\"}")
    check "document closed" "closed" "$R"

    echo "/q" >&3; exec 3>&-; wait "$SHELL_PID" 2>/dev/null || true; SHELL_PID=""
    rm -f "$FIFO_IN" "$OUT_FILE"
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
        local before
        before=$(wc -l < "$OUT_REAL" | tr -d ' ')
        echo "$1" >&4
        sleep 1
        tail -n +"$((before + 1))" "$OUT_REAL" | sed 's/^mcp > //' | grep '^{' | head -1
    }

    R=$(send_cmd_real "call document_open -p {\"path\":\"${REAL_FILE}\"}")
    REAL_ID=$(echo "$R" | grep -o 'Session ID: [a-f0-9]*' | head -1 | cut -d' ' -f3)

    if [[ -n "$REAL_ID" ]]; then
        pass "real doc opened (ID: $REAL_ID)"

        R=$(send_cmd_real "call query -p {\"doc_id\":\"${REAL_ID}\",\"path\":\"/body\"}")
        check "real doc body queried" "paragraph_count" "$R"

        R=$(send_cmd_real "call query -p {\"doc_id\":\"${REAL_ID}\",\"path\":\"/body/paragraph[*]\",\"format\":\"text\"}")
        echo -e "  ${YELLOW}First 200 chars: ${R:0:200}${NC}"

        R=$(send_cmd_real "call document_close -p {\"doc_id\":\"${REAL_ID}\"}")
        check "real doc closed" "closed" "$R"
    else
        fail "real doc opened" "$(cat "$OUT_REAL")"
    fi

    echo "/q" >&4; exec 4>&-; wait "$SHELL_PID" 2>/dev/null || true; SHELL_PID=""
fi

echo ""
echo "========================================"
echo -e "Results: ${GREEN}${PASSED} passed${NC}, ${RED}${FAILED} failed${NC}"
echo "========================================"

[[ "$FAILED" -eq 0 ]] || exit 1
