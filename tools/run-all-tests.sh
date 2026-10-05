#!/usr/bin/env bash
# Runs every test suite; stops at the first failing suite so it can be fixed and the run restarted.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

echo "### 1/4 CLI unit tests"
dotnet test "$ROOT/tests/ApiGenerator.Cli.Tests" -nologo -v q

echo "### 2/4 CLI behavior cases"
"$ROOT/tools/cli-cases.sh"

echo "### 3/4 Generation smoke test (build, unit tests, EF model, runtime CRUD, learner round trip)"
"$ROOT/tools/smoke-test.sh"

echo "### 4/4 VS Code extension build"
(cd "$ROOT/extension" && npm ci --force --no-audit --no-fund >/dev/null && npm run build)

echo "ALL TESTS PASSED"
