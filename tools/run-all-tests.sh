#!/usr/bin/env bash
# Runs every test suite; stops at the first failing suite so it can be fixed and the run restarted.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

echo "### 1/5 CLI unit tests"
dotnet test "$ROOT/tests/ApiGenerator.Cli.Tests" -nologo -v q

echo "### 2/5 CLI behavior cases"
"$ROOT/tools/cli-cases.sh"

echo "### 3/5 Generation smoke test (build, unit tests, EF model, runtime CRUD, learner round trip)"
"$ROOT/tools/smoke-test.sh"

echo "### 4/5 VS Code extension build"
(cd "$ROOT/extension" && npm ci --no-audit --no-fund >/dev/null && npm run build)

echo "### 5/5 UI end-to-end (extension webview in Chromium, real CLI)"
(cd "$ROOT/extension" && node ./test/e2e/run-e2e.js)

echo "ALL TESTS PASSED"
