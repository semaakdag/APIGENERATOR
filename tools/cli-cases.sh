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
expect "skip keeps local edit" 0 "conflicts: 1, deleted: 0$" -- gen --output "$OUT/a"
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

# add-endpoint: idempotency, validation, dry-run, legacy (non-partial) code and regeneration.
REC="$OUT/recipes"
"${CLI[@]}" generate --schema "$ROOT/tools/cases/recipes.sql" --framework "$FW" --unit-tests Enable --output "$REC" >/dev/null
add() { "${CLI[@]}" add-endpoint --project "$REC" "$@"; }
snapshot() { find "$REC" -type f -not -path '*/bin/*' -not -path '*/obj/*' -exec md5sum {} + | sort; }
before=$(snapshot)
expect "add-endpoint dry run" 0 "Planned endpoint 'GetByCode' on Products: GET /api/Products/by-code/\{code\}" -- add --entity Products --recipe GetByCode --field Code --dry-run
[[ "$before" == "$(snapshot)" ]] || { echo "FAIL: add-endpoint dry run wrote files"; failures=$((failures + 1)); }
expect "add-endpoint adds files" 0 "created: src/recipes.Api/Controllers/ProductsController.GetByCode.cs" -- add --entity Products --recipe GetByCode --field Code
after=$(snapshot)
expect "add-endpoint is idempotent" 0 "already exists on Products" -- add --entity Products --recipe GetByCode --field Code
[[ "$after" == "$(snapshot)" ]] || { echo "FAIL: repeated add-endpoint changed files"; failures=$((failures + 1)); }
expect "add-endpoint json event" 0 '"event":"endpoint-added".*"method":"SearchByName"' -- add --entity Products --recipe Search --field Name --log-format Json
expect "unknown recipe" 2 "Unknown recipe 'Nope'" -- add --entity Products --recipe Nope --field Code
expect "unknown entity" 2 "Could not find the repository interface 'IGhostsRepository'" -- add --entity Ghosts --recipe GetByCode --field Code
expect "unknown field" 2 "'Products' has no property 'Colour'" -- add --entity Products --recipe GetByCode --field Colour
expect "wrong field type" 2 "Recipe 'Search' needs a string property, but 'Products.CreatedAt' is 'DateTime'" -- add --entity Products --recipe Search --field CreatedAt
expect "missing field" 2 "Recipe 'GetByDateRange' needs --field" -- add --entity Products --recipe GetByDateRange
expect "missing project" 2 "requires --project" -- "${CLI[@]}" add-endpoint --entity Products --recipe BulkInsert
expect "bulk insert" 0 "POST /api/Products/bulk" -- add --entity Products --recipe BulkInsert
expect "regenerate keeps recipes" 0 "conflicts: 0, deleted: 0" -- "${CLI[@]}" generate --schema "$ROOT/tools/cases/recipes.sql" --framework "$FW" --unit-tests Enable --output "$REC"
grep -q '| BulkInsert | POST | `/api/Products/bulk`' "$REC/docs/API-DOCUMENTATION.md" \
  || { echo "FAIL: regeneration dropped recipe documentation"; failures=$((failures + 1)); }
expect "recipes build after regeneration" 0 "Build succeeded" -- dotnet build "$REC" -nologo -v q

# Overwrite regenerates from scratch: recipe files and the endpoint store go, files the user added stay.
SCRATCH="$OUT/scratch"
cp -r "$REC" "$SCRATCH" && rm -rf "$SCRATCH"/src/*/bin "$SCRATCH"/src/*/obj "$SCRATCH"/tests/*/bin "$SCRATCH"/tests/*/obj
echo "user notes" > "$SCRATCH/NOTES.md"
expect "overwrite dry run lists deletions" 0 "deleted: [1-9]" -- "${CLI[@]}" generate --schema "$ROOT/tools/cases/recipes.sql" --framework "$FW" --unit-tests Enable --output "$SCRATCH" --overwrite-mode Overwrite --dry-run
[[ -f "$SCRATCH/src/scratch.Api/Controllers/ProductsController.BulkInsert.cs" || -f "$SCRATCH/src/recipes.Api/Controllers/ProductsController.BulkInsert.cs" ]] \
  || { echo "FAIL: overwrite dry run deleted files"; failures=$((failures + 1)); }
expect "overwrite starts from scratch" 0 "deleted: [1-9]" -- "${CLI[@]}" generate --schema "$ROOT/tools/cases/recipes.sql" --framework "$FW" --unit-tests Enable --output "$SCRATCH" --overwrite-mode Overwrite
leftovers=$(find "$SCRATCH" -name '*.BulkInsert*.cs' -o -name '*.GetByCode*.cs' -o -name 'api-generator.endpoints.json' -o -path '*/recipes.*' -not -path '*/bin/*' -not -path '*/obj/*' | head -5)
[[ -z "$leftovers" ]] || { echo "FAIL: overwrite kept files of the previous generation:"; echo "$leftovers"; failures=$((failures + 1)); }
[[ -f "$SCRATCH/NOTES.md" ]] || { echo "FAIL: overwrite deleted a file the generator did not write"; failures=$((failures + 1)); }
grep -q 'BulkInsert' "$SCRATCH/docs/API-DOCUMENTATION.md" && { echo "FAIL: overwrite kept recipe documentation"; failures=$((failures + 1)); }
grep -q '"Status": "deleted"' "$SCRATCH/generation-manifest.json" || { echo "FAIL: manifest does not list deleted files"; failures=$((failures + 1)); }
expect "scratch solution builds" 0 "Build succeeded" -- dotnet build "$SCRATCH" -nologo -v q

LEGACY="$OUT/legacy"
"${CLI[@]}" generate --schema "$ROOT/tools/cases/recipes.sql" --framework "$FW" --output "$LEGACY" >/dev/null
find "$LEGACY/src" -name '*.cs' -exec sed -i 's/ partial / /' {} +
expect "legacy code gets partial" 0 "updated: src/legacy.Api/Controllers/OrdersController.cs" -- "${CLI[@]}" add-endpoint --project "$LEGACY" --entity Orders --recipe GetActiveList --field IsOpen
grep -q "public sealed partial class OrdersController" "$LEGACY/src/legacy.Api/Controllers/OrdersController.cs" \
  || { echo "FAIL: partial modifier not added"; failures=$((failures + 1)); }
expect "legacy code builds" 0 "Build succeeded" -- dotnet build "$LEGACY" -nologo -v q

ENDPOINTS="$OUT/endpoint-style"
echo '{"Framework":{"UseControllers":false,"ApiStyle":"minimal-api"}}' > "$OUT/endpoint-style.profile.json"
"${CLI[@]}" generate --schema "$ROOT/tools/cases/recipes.sql" --framework "$FW" --output "$ENDPOINTS" --profile "$OUT/endpoint-style.profile.json" >/dev/null
for recipe in "GetByCode Code" "BulkInsert" "Search Name"; do
  read -r name field <<<"$recipe"
  "${CLI[@]}" add-endpoint --project "$ENDPOINTS" --entity Products --recipe "$name" ${field:+--field "$field"} >/dev/null
done
expected_order=$'app.MapProductsEndpoints();\napp.MapProductsGetByCodeEndpoint();\napp.MapProductsBulkInsertEndpoint();\napp.MapProductsSearchByNameEndpoint();'
[[ "$(grep -o 'app.MapProducts[A-Za-z]*();' "$ENDPOINTS/src/endpointstyle.Api/Program.cs")" == "$expected_order" ]] \
  || { echo "FAIL: endpoint registrations out of order"; grep -n 'app.Map' "$ENDPOINTS/src/endpointstyle.Api/Program.cs"; failures=$((failures + 1)); }
"${CLI[@]}" generate --schema "$ROOT/tools/cases/recipes.sql" --framework "$FW" --output "$ENDPOINTS" --profile "$OUT/endpoint-style.profile.json" >/dev/null
[[ "$(grep -o 'app.MapProducts[A-Za-z]*();' "$ENDPOINTS/src/endpointstyle.Api/Program.cs")" == "$expected_order" ]] \
  || { echo "FAIL: regeneration dropped endpoint registrations"; failures=$((failures + 1)); }
expect "endpoint style builds" 0 "Build succeeded" -- dotnet build "$ENDPOINTS" -nologo -v q
"${CLI[@]}" generate --schema "$ROOT/tools/cases/recipes.sql" --framework "$FW" --output "$ENDPOINTS" --profile "$OUT/endpoint-style.profile.json" --overwrite-mode Overwrite >/dev/null
[[ "$(grep -o 'app.MapProducts[A-Za-z]*();' "$ENDPOINTS/src/endpointstyle.Api/Program.cs")" == "app.MapProductsEndpoints();" ]] \
  || { echo "FAIL: overwrite kept recipe endpoint registrations"; failures=$((failures + 1)); }
expect "endpoint style builds after overwrite" 0 "Build succeeded" -- dotnet build "$ENDPOINTS" -nologo -v q

# Default Framework mode follows the reference project's folders and names (tools/cases/reference-acme).
REF="$ROOT/tools/cases/reference-acme"
FROMREF="$OUT/FromRef"
expect "reference generation" 0 "conflicts: 0" -- "${CLI[@]}" generate --schema "$ROOT/tools/cases/reference-orders.sql" --project "$REF" --unit-tests Enable --output "$FROMREF"
for path in \
  src/FromRef.WebApi/Controllers/V1/OrderController.cs \
  src/FromRef.Business/Services/Orders/IOrderService.cs \
  src/FromRef.Business/Services/Orders/OrderService.cs \
  src/FromRef.Business/Models/Requests/OrderCreateRequest.cs \
  src/FromRef.Business/Models/Requests/OrderUpdateRequest.cs \
  src/FromRef.Business/Models/Responses/OrderResponse.cs \
  src/FromRef.Core/Entities/Order.cs \
  src/FromRef.DataAccess/Repositories/OrderRepository.cs \
  src/FromRef.DataAccess/Repositories/Interfaces/IOrderRepository.cs \
  test/FromRef.Tests/Services/OrderServiceTests.cs; do
  [[ -f "$FROMREF/$path" ]] || { echo "FAIL: reference structure not followed, missing $path"; failures=$((failures + 1)); }
done
[[ -z "$(find "$FROMREF" -name '*Dto.cs' -o -path '*Abstractions*' | head -1)" ]] || { echo "FAIL: files the reference does not have were generated"; failures=$((failures + 1)); }
expect "reference solution builds" 0 "Build succeeded" -- dotnet build "$FROMREF" -nologo -v q -warnaserror
expect "reference solution tests pass" 0 "Passed!" -- dotnet test "$FROMREF" -nologo -v q
echo "CREATE TABLE Invoices (Id INT NOT NULL PRIMARY KEY, Number NVARCHAR(20) NOT NULL);" > "$OUT/invoices.sql"
expect "reference overwrite from scratch" 0 "deleted: [1-9]" -- "${CLI[@]}" generate --schema "$OUT/invoices.sql" --project "$REF" --unit-tests Enable --output "$FROMREF" --overwrite-mode Overwrite
[[ -z "$(find "$FROMREF" -name 'Order*' -not -path '*/bin/*' -not -path '*/obj/*' | head -1)" ]] || { echo "FAIL: overwrite kept Order files"; failures=$((failures + 1)); }
[[ -z "$(find "$FROMREF/src/FromRef.Business/Services" -mindepth 1 -type d -name 'Order*' | head -1)" ]] || { echo "FAIL: overwrite kept empty Order folders"; failures=$((failures + 1)); }
expect "reference overwrite builds" 0 "Build succeeded" -- dotnet build "$FROMREF" -nologo -v q -warnaserror
CQRS="$OUT/FromCqrs"
expect "cqrs reference generation" 0 "conflicts: 0" -- "${CLI[@]}" generate --schema "$ROOT/tools/cases/reference-orders.sql" --project "$ROOT/tools/cases/reference-cqrs" --output "$CQRS"
leaked=$(grep -rlE 'UserBranch|KullaniciIslemleri|ConfigurationsDto|AddApp' "$CQRS" --include='*.cs' | head -3)
[[ -z "$leaked" ]] || { echo "FAIL: reference feature code copied:"; echo "$leaked"; failures=$((failures + 1)); }
expect "cqrs reference solution builds" 0 "Build succeeded" -- dotnet build "$CQRS" -nologo -v q -warnaserror
expect "output inside reference rejected" 2 "inside the reference project" -- "${CLI[@]}" generate --schema "$OUT/invoices.sql" --project "$REF" --output "$REF/src"

expect "entities lists properties" 0 "Products: Id \\(int\\), Code \\(string\\)" -- "${CLI[@]}" entities --project "$REC"
expect "entities json" 0 '"event":"entities".*"name":"Orders"' -- "${CLI[@]}" entities --project "$REC" --log-format Json
expect "entities missing project" 2 "requires --project" -- "${CLI[@]}" entities
expect "entities unknown folder" 2 "was not found" -- "${CLI[@]}" entities --project "$OUT/no-such-solution"

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
