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
expect "fail mode reports conflict" 3 "Conflicting files detected" -- gen --output "$OUT/a" --overwrite-mode Fail
expect "overwrite mode replaces" 0 "conflicts: 0" -- gen --output "$OUT/a" --overwrite-mode Overwrite
grep -q "// local edit" "$OUT/a/src/a.Api/Program.cs" && { echo "FAIL: overwrite mode kept local edit"; failures=$((failures + 1)); }
expect "dry run plans" 0 "Planned solution" -- gen --output "$OUT/dry" --dry-run
[[ -z "$(ls -A "$OUT/dry" 2>/dev/null)" ]] || { echo "FAIL: dry run wrote files"; failures=$((failures + 1)); }
expect "framework by preset id" 0 "Generated solution" -- bash -c "cd '$ROOT' && ${CLI[*]} generate --schema '$SCHEMA' --framework minimal-api-swagger --output '$OUT/byid'"

expect "missing schema" 2 "Schema file was not found" -- "${CLI[@]}" generate --schema "$OUT/nope.sql" --framework "$FW" --output "$OUT/x"
expect "unknown framework" 2 "Framework preset 'nope-preset' was not found" -- "${CLI[@]}" generate --schema "$SCHEMA" --framework nope-preset --output "$OUT/x"
expect "missing profile" 2 "Profile file was not found" -- gen --output "$OUT/x" --profile "$OUT/nope.json"
expect "project with framework" 2 "only supported when no framework" -- gen --output "$OUT/x" --project "$OUT/a"
expect "default framework needs project" 2 "requires --project" -- "${CLI[@]}" generate --schema "$SCHEMA" --output "$OUT/x"
expect "invalid project" 2 "Target project path was not found" -- "${CLI[@]}" generate --schema "$SCHEMA" --output "$OUT/x" --project "$OUT/nope"
expect "llm misconfigured" 2 "URL, model, or token is missing" -- gen --output "$OUT/x" --llm-enabled
printf '' > "$OUT/empty.sql"; printf 'hello world' > "$OUT/garbage.sql"
expect "empty schema" 2 "No CREATE TABLE statements" -- "${CLI[@]}" generate --schema "$OUT/empty.sql" --framework "$FW" --output "$OUT/x"
expect "garbage schema" 2 "No CREATE TABLE statements" -- "${CLI[@]}" generate --schema "$OUT/garbage.sql" --framework "$FW" --output "$OUT/x"

expect "learn" 0 "Profile saved" -- "${CLI[@]}" learn --project "$OUT/a" --output "$OUT/learned"
expect "learned profile reusable" 0 "Generated solution" -- "${CLI[@]}" generate --schema "$SCHEMA" --project "$OUT/a" --profile "$OUT/learned/company-standard.profile.json" --output "$OUT/relearn"
expect "learn invalid project" 2 "not found|could not|No project" -- "${CLI[@]}" learn --project "$OUT/nope" --output "$OUT/l2"
expect "analyze" 0 "\"ProfileName\"" -- "${CLI[@]}" analyze --project "$OUT/a"
expect "analyze invalid project" 2 "not found|could not|No project" -- "${CLI[@]}" analyze --project "$OUT/nope"
expect "document" 0 "Documentation written" -- "${CLI[@]}" document --output "$OUT/a"

# Structured output, warnings and safety checks.
expect "json log lines" 0 '^\{"level":"info","event":"generation-summary".*"created":' -- gen --output "$OUT/json" --log-format Json
expect "json error line" 2 '^\{"level":"error","event":"command-failed".*"exitCode":2,"category":"invalid-input"' -- "${CLI[@]}" generate --schema "$OUT/nope.sql" --framework "$FW" --output "$OUT/x" --log-format Json
expect "keyless table warning" 0 "warning: Line [0-9]+: Table 'NoPrimaryKey' has no primary key" -- "${CLI[@]}" generate --schema "$ROOT/tools/cases/edge-cases.sql" --framework "$FW" --output "$OUT/edge"
grep -q '"Warnings": \[' "$OUT/edge/generation-manifest.json" && grep -q "NoPrimaryKey" "$OUT/edge/generation-manifest.json" \
  || { echo "FAIL: manifest lacks warnings"; failures=$((failures + 1)); }
printf 'CREATE TABLE T (Id INT PRIMARY KEY, Spot GEOGRAPHY NULL);\n' > "$OUT/unknown-type.sql"
expect "unknown type warning" 0 "warning: Line 1: Type 'geography'" -- "${CLI[@]}" generate --schema "$OUT/unknown-type.sql" --framework "$FW" --output "$OUT/geo"

mkdir -p "$OUT/evil"
cat > "$OUT/evil/escape.profile.json" <<'JSON'
{ "SchemaVersion": "2.1", "SharedFiles": [ { "RelativePath": "../../escaped.txt", "Template": "pwned" } ] }
JSON
expect "profile cannot write outside output" 2 "resolves outside the output folder" -- gen --output "$OUT/evil/out" --profile "$OUT/evil/escape.profile.json"
[[ ! -e "$OUT/escaped.txt" && ! -e "$OUT/evil/out" ]] || { echo "FAIL: escaping profile wrote files"; failures=$((failures + 1)); }
echo '{ "SchemaVersion": "3.0" }' > "$OUT/evil/future.profile.json"
expect "newer profile version rejected" 2 "supports 2.x" -- gen --output "$OUT/x" --profile "$OUT/evil/future.profile.json"
echo '{ "SchemaVersion": "2.1", ' > "$OUT/evil/broken.profile.json"
expect "broken profile json" 2 "is not a valid JSON object" -- gen --output "$OUT/x" --profile "$OUT/evil/broken.profile.json"
echo '{ "TemplateOverrides": { "controller": "{{ include \"/etc/passwd\" }}" } }' > "$OUT/evil/include.profile.json"
expect "template include cannot read files" 2 "Template render error in profile override 'controller' \\(1:4\\)" -- gen --output "$OUT/x" --profile "$OUT/evil/include.profile.json"
grep -rqs "root:" "$OUT/x" && { echo "FAIL: template include read a system file"; failures=$((failures + 1)); }
expect "missing profile version warns" 0 "warning: Profile 'include2.profile.json' has no SchemaVersion" -- bash -c "echo '{}' > '$OUT/evil/include2.profile.json'; ${CLI[*]} generate --schema '$SCHEMA' --framework '$FW' --output '$OUT/nover' --profile '$OUT/evil/include2.profile.json'"

echo '{ "SchemaVersion": "2.1", "TemplateOverrides": { "controller": "ok\n{{ for x in }} broken" } }' > "$OUT/evil/parse.profile.json"
expect "broken override fails validation" 2 "Template validation failed; no files were written:.*profile override 'controller' \\(2:" -- bash -c "${CLI[*]} generate --schema '$SCHEMA' --framework '$FW' --output '$OUT/badtpl' --profile '$OUT/evil/parse.profile.json' 2>&1 | tr '\n' ' '; exit \${PIPESTATUS[0]}"
[[ ! -e "$OUT/badtpl" ]] || { echo "FAIL: invalid template still wrote files"; failures=$((failures + 1)); }

mkdir -p "$OUT/ws/.api-generator/templates"
cp "$ROOT/generator-engine/Templates/Entity.sbncs" "$OUT/ws/.api-generator/templates/Entity.sbncs"
sed -i '1i // workspace entity template' "$OUT/ws/.api-generator/templates/Entity.sbncs"
echo 'x' > "$OUT/ws/.api-generator/templates/Unknown.sbncs"
expect "workspace template override" 0 "warning: Template override 'Unknown.sbncs' does not match" -- bash -c "cd '$OUT/ws' && ${CLI[*]} generate --schema '$SCHEMA' --framework '$FW' --output out"
grep -q "// workspace entity template" "$OUT/ws/out/src/out.Domain/Entities/Users.cs" \
  || { echo "FAIL: workspace template override not applied"; failures=$((failures + 1)); }
expect "explicit templates folder" 0 "Generated solution" -- gen --output "$OUT/tplopt" --templates "$OUT/ws/.api-generator/templates"
grep -q "// workspace entity template" "$OUT/tplopt/src/tplopt.Domain/Entities/Users.cs" \
  || { echo "FAIL: --templates override not applied"; failures=$((failures + 1)); }
expect "missing templates folder" 2 "Template override folder .* was not found" -- gen --output "$OUT/x" --templates "$OUT/no-such-folder"
printf 'broken {{ if }}' > "$OUT/ws/.api-generator/templates/Dto.sbncs"
expect "broken workspace template" 2 "workspace template 'Dto.sbncs' \\(1:" -- bash -c "cd '$OUT/ws' && ${CLI[*]} generate --schema '$SCHEMA' --framework '$FW' --output out2"

# Performance (NFR-2): 10 tables < 5 s and 50 tables < 10 s, measured after a warm-up run.
python3 - "$OUT" <<'PY'
import sys
for count in (10, 50):
    with open(f"{sys.argv[1]}/perf-{count}.sql", "w") as schema:
        for index in range(count):
            schema.write(f"CREATE TABLE Table{index} (Id INT IDENTITY(1,1) PRIMARY KEY, Code NVARCHAR(20) NOT NULL, "
                         f"Amount DECIMAL(18,2) NULL, CreatedAt DATETIME2 NOT NULL, IsActive BIT NOT NULL);\n")
PY
"${CLI[@]}" generate --schema "$OUT/perf-10.sql" --framework "$FW" --output "$OUT/perf-warmup" >/dev/null
for spec in "10 5" "50 10"; do
  read -r tables limit <<<"$spec"
  started=$(date +%s.%N)
  "${CLI[@]}" generate --schema "$OUT/perf-$tables.sql" --framework "$FW" --output "$OUT/perf-$tables" >/dev/null
  elapsed=$(echo "$(date +%s.%N) - $started" | bc)
  if (( $(echo "$elapsed < $limit" | bc) )); then
    echo "ok:   $tables tables generated in ${elapsed}s (limit ${limit}s)"
  else
    echo "FAIL: $tables tables took ${elapsed}s (limit ${limit}s)"; failures=$((failures + 1))
  fi
done

if (( failures > 0 )); then echo "CLI CASES FAILED: $failures"; exit 1; fi
echo "CLI CASES OK"
