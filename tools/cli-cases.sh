#!/usr/bin/env bash
# CLI behavior cases: overwrite modes, dry-run, idempotency and error paths.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${CASES_OUT:-$ROOT/artifacts/cli-cases}"
CLI=(dotnet "$ROOT/generator-engine/bin/Debug/net8.0/ApiGenerator.Cli.dll")
FW="$ROOT/profiles/frameworks/aspnet-controller-swagger.profile.json"
SCHEMA="$ROOT/examples/users.sql"
failures=0

dotnet build "$ROOT/generator-engine/ApiGenerator.Cli.csproj" -nologo -v q >/dev/null || exit 1
rm -rf "$OUT" && mkdir -p "$OUT"

# expect <name> <exit-code> <output-regex> -- command...
expect() {
  local name="$1" code="$2" pattern="$3"; shift 4
  local output actual
  output="$("$@" 2>&1)"; actual=$?
  if [[ "$actual" != "$code" ]] || ! grep -Eq -- "$pattern" <<<"$output" || grep -q "   at " <<<"$output"; then
    echo "FAIL: $name (exit $actual, expected $code /$pattern/)"; echo "$output" | tail -5; failures=$((failures + 1))
  else
    echo "ok:   $name"
  fi
}

gen() { "${CLI[@]}" generate --schema "$SCHEMA" --framework "$FW" --postman-collection Enable "$@"; }

expect "first generation" 0 "created: [1-9]" -- gen --output "$OUT/a"
expect "rerun is idempotent" 0 "created: 0, updated: [01], unchanged: [0-9]+, conflicts: 0" -- gen --output "$OUT/a"
echo "// local edit" >> "$OUT/a/src/a.Api/Program.cs"
expect "skip keeps local edit" 0 "conflicts: 1$" -- gen --output "$OUT/a"
grep -q "// local edit" "$OUT/a/src/a.Api/Program.cs" || { echo "FAIL: local edit lost in skip mode"; failures=$((failures + 1)); }
expect "fail mode reports conflict" 1 "Conflicting files detected" -- gen --output "$OUT/a" --overwrite-mode Fail
expect "overwrite mode replaces" 0 "conflicts: 0" -- gen --output "$OUT/a" --overwrite-mode Overwrite
grep -q "// local edit" "$OUT/a/src/a.Api/Program.cs" && { echo "FAIL: overwrite mode kept local edit"; failures=$((failures + 1)); }
expect "dry run plans" 0 "Planned solution" -- gen --output "$OUT/dry" --dry-run
[[ -z "$(ls -A "$OUT/dry" 2>/dev/null)" ]] || { echo "FAIL: dry run wrote files"; failures=$((failures + 1)); }
expect "framework by preset id" 0 "Generated solution" -- bash -c "cd '$ROOT' && ${CLI[*]} generate --schema '$SCHEMA' --framework minimal-api-swagger --output '$OUT/byid'"

expect "missing schema" 1 "Schema file was not found" -- "${CLI[@]}" generate --schema "$OUT/nope.sql" --framework "$FW" --output "$OUT/x"
expect "unknown framework" 1 "Framework preset 'nope-preset' was not found" -- "${CLI[@]}" generate --schema "$SCHEMA" --framework nope-preset --output "$OUT/x"
expect "missing profile" 1 "Profile file was not found" -- gen --output "$OUT/x" --profile "$OUT/nope.json"
expect "project with framework" 1 "only supported when no framework" -- gen --output "$OUT/x" --project "$OUT/a"
expect "default framework needs project" 1 "requires --project" -- "${CLI[@]}" generate --schema "$SCHEMA" --output "$OUT/x"
expect "invalid project" 1 "Target project path was not found" -- "${CLI[@]}" generate --schema "$SCHEMA" --output "$OUT/x" --project "$OUT/nope"
expect "llm misconfigured" 1 "URL, model, or token is missing" -- gen --output "$OUT/x" --llm-enabled
printf '' > "$OUT/empty.sql"; printf 'hello world' > "$OUT/garbage.sql"
expect "empty schema" 1 "No CREATE TABLE statements" -- "${CLI[@]}" generate --schema "$OUT/empty.sql" --framework "$FW" --output "$OUT/x"
expect "garbage schema" 1 "No CREATE TABLE statements" -- "${CLI[@]}" generate --schema "$OUT/garbage.sql" --framework "$FW" --output "$OUT/x"

expect "learn" 0 "Profile saved" -- "${CLI[@]}" learn --project "$OUT/a" --output "$OUT/learned"
expect "learned profile reusable" 0 "Generated solution" -- "${CLI[@]}" generate --schema "$SCHEMA" --project "$OUT/a" --profile "$OUT/learned/company-standard.profile.json" --output "$OUT/relearn"
expect "learn invalid project" 1 "not found|could not|No project" -- "${CLI[@]}" learn --project "$OUT/nope" --output "$OUT/l2"
expect "analyze" 0 "\"ProfileName\"" -- "${CLI[@]}" analyze --project "$OUT/a"
expect "analyze invalid project" 1 "not found|could not|No project" -- "${CLI[@]}" analyze --project "$OUT/nope"
expect "document" 0 "Documentation written" -- "${CLI[@]}" document --output "$OUT/a"

if (( failures > 0 )); then echo "CLI CASES FAILED: $failures"; exit 1; fi
echo "CLI CASES OK"
