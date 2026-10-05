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

echo "SMOKE OK"
