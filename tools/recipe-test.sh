#!/usr/bin/env bash
# Applies every endpoint recipe to a generated solution, then builds, unit tests and (optionally) exercises it over HTTP.
# Usage: tools/recipe-test.sh <framework-preset> <output-dir> [extra generate options...]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CLI=(dotnet "$ROOT/generator-engine/bin/Debug/net8.0/ApiGenerator.Cli.dll")
PRESET="$1"
TARGET="$2"
shift 2

rm -rf "$TARGET"
"${CLI[@]}" generate --schema "$ROOT/tools/cases/recipes.sql" --framework "$PRESET" --unit-tests Enable --output "$TARGET" "$@" >/dev/null
while read -r entity recipe field; do
  "${CLI[@]}" add-endpoint --project "$TARGET" --entity "$entity" --recipe "$recipe" ${field:+--field "$field"} >/dev/null
done <<'LIST'
Products GetByCode Code
Products GetActiveList
Products Search Name
Products GetByDateRange CreatedAt
Products BulkInsert
Orders GetByCode Number
Orders GetActiveList IsOpen
Orders GetByDateRange PlacedOn
Orders BulkInsert
Stock GetByDateRange CountedAt
Stock BulkInsert
LIST

dotnet build "$TARGET" -nologo -v q -warnaserror
dotnet test "$TARGET" -nologo -v q --no-build
grep -q '| GetByCode | GET | `/api/Products/by-code/{code}`' "$TARGET/docs/API-DOCUMENTATION.md" \
  || { echo "Recipe endpoints missing from API documentation"; exit 1; }
