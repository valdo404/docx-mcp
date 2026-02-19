#!/usr/bin/env bash
# Generate test coverage reports for .NET and Rust services
set -euo pipefail

COVERAGE_DIR="coverage"
rm -rf "$COVERAGE_DIR"

echo "=== .NET coverage ==="
dotnet test tests/DocxMcp.Tests/ \
  --collect:"XPlat Code Coverage" \
  --results-directory "$COVERAGE_DIR/dotnet"

if command -v reportgenerator &> /dev/null; then
  reportgenerator \
    -reports:"$COVERAGE_DIR/dotnet/**/coverage.cobertura.xml" \
    -targetdir:"$COVERAGE_DIR/dotnet/html" \
    -reporttypes:"Html;TextSummary"
  cat "$COVERAGE_DIR/dotnet/html/Summary.txt"
else
  echo "  (install reportgenerator for HTML reports: dotnet tool install -g dotnet-reportgenerator-globaltool)"
  echo "  Raw coverage: $COVERAGE_DIR/dotnet/**/coverage.cobertura.xml"
fi

echo ""
echo "=== Rust coverage ==="
if command -v cargo-tarpaulin &> /dev/null; then
  cargo tarpaulin --workspace \
    --exclude-files "*/build.rs" \
    --out Html Xml \
    --output-dir "$COVERAGE_DIR/rust"
else
  echo "  (install tarpaulin for Rust coverage: cargo install cargo-tarpaulin)"
  echo "  Running tests without coverage..."
  cargo test --workspace
fi

echo ""
echo "Reports:"
echo "  .NET:  $COVERAGE_DIR/dotnet/html/index.html"
echo "  Rust:  $COVERAGE_DIR/rust/tarpaulin-report.html"
