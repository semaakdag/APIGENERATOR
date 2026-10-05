#!/usr/bin/env bash
# Smoke test: generate an API from examples/users.sql with every framework preset
# and verify that the generated solution builds (and its tests pass when generated).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="${SMOKE_OUT:-$ROOT/artifacts/smoke}"
CLI="$ROOT/generator-engine/bin/Debug/net8.0/ApiGenerator.Cli.dll"

dotnet build "$ROOT/generator-engine/ApiGenerator.Cli.csproj" -nologo -v q
rm -rf "$OUT"

for preset in "$ROOT"/profiles/frameworks/*.profile.json; do
  name="$(basename "$preset" .profile.json)"
  for tests in Enable Disable; do
    target="$OUT/$name-tests-$tests"
    echo "=== $name (unit tests: $tests) ==="
    dotnet "$CLI" generate --schema "$ROOT/examples/users.sql" --output "$target" \
      --framework "$preset" --unit-tests "$tests" --postman-collection Enable
    dotnet build "$target" -nologo -v q -warnaserror:NU1901,NU1902,NU1903,NU1904
    if [[ "$tests" == Enable ]] && compgen -G "$target/**/*Tests*.csproj" >/dev/null 2>&1 || find "$target" -name '*Tests.csproj' | grep -q .; then
      [[ "$tests" == Enable ]] && dotnet test "$target" -nologo -v q --no-build
    fi
  done
done

# SQL edge cases (schemas, quoted names, composite/missing keys, identity, many types) per preset.
for preset in "$ROOT"/profiles/frameworks/*.profile.json; do
  name="edge-$(basename "$preset" .profile.json)"
  target="$OUT/$name"
  echo "=== $name ==="
  dotnet "$CLI" generate --schema "$ROOT/tools/cases/edge-cases.sql" --output "$target" \
    --framework "$preset" --unit-tests Enable
  dotnet build "$target" -nologo -v q -warnaserror:NU1901,NU1902,NU1903,NU1904
  dotnet test "$target" -nologo -v q --no-build
  if find "$target/src" -name AppDbContext.cs -not -path '*/obj/*' | grep -q .; then
    "$ROOT/tools/ef-model-check.sh" "$target" > "$target/ef-model.sql"
    grep -q 'CREATE TABLE \[sales\]\.\[order_line\]' "$target/ef-model.sql"
    grep -q '\[order_line_id\] int NOT NULL,' "$target/ef-model.sql"
    grep -q 'PRIMARY KEY (\[TenantId\], \[ItemId\])' "$target/ef-model.sql"
    grep -q '\[OrderId\] bigint NOT NULL IDENTITY' "$target/ef-model.sql"
    if grep -q 'Fake' "$target/ef-model.sql"; then echo 'Commented-out table was parsed'; exit 1; fi
  fi
done

# Default Framework mode: learn from a generated reference project, optionally overlaid with each company profile.
reference="$OUT/enterprise-controller-loghelper-swagger-tests-Enable"
for profile in "" "$ROOT"/profiles/*.profile.json "$ROOT"/profiles/feature-check-smoke/*.profile.json; do
  name="default-framework-$(echo "${profile#"$ROOT/profiles/"}" | sed 's/\.profile\.json$//' | tr '/.' '--')"; name="${name%-}"
  target="$OUT/$name"
  echo "=== $name ==="
  dotnet "$CLI" generate --schema "$ROOT/examples/users.sql" --output "$target" \
    --project "$reference" ${profile:+--profile "$profile"}
  dotnet build "$target" -nologo -v q -warnaserror:NU1901,NU1902,NU1903,NU1904
done

# Learner round trip: learn from each generated edge-case project and regenerate the edge schema.
for preset in "$ROOT"/profiles/frameworks/*.profile.json; do
  name="roundtrip-$(basename "$preset" .profile.json)"
  target="$OUT/$name"
  echo "=== $name ==="
  dotnet "$CLI" generate --schema "$ROOT/tools/cases/edge-cases.sql" --output "$target" \
    --project "$OUT/edge-$(basename "$preset" .profile.json)"
  dotnet build "$target" -nologo -v q -warnaserror:NU1901,NU1902,NU1903,NU1904
done

echo "SMOKE OK"
