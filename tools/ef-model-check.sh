#!/usr/bin/env bash
# Validates the EF Core model of a generated single-project API (no database needed)
# by building the model and printing the CREATE script.
# Usage: tools/ef-model-check.sh <generated-solution-dir>
set -euo pipefail

TARGET="$(cd "$1" && pwd)"
API_PROJECT="$(find "$TARGET/src" -maxdepth 2 -name '*.Api.csproj' | head -1)"
CONTEXT_FILE="$(find "$(dirname "$API_PROJECT")" -name AppDbContext.cs -not -path '*/obj/*' | head -1)"
[[ -n "$CONTEXT_FILE" ]] || { echo "No AppDbContext in $TARGET"; exit 1; }
CONTEXT_NAMESPACE="$(sed -n 's/^namespace \(.*\);/\1/p' "$CONTEXT_FILE")"
HARNESS="$(mktemp -d)"
trap 'rm -rf "$HARNESS"' EXIT


cat > "$HARNESS/EfModelCheck.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="$API_PROJECT" />
  </ItemGroup>
</Project>
XML
cat > "$HARNESS/Program.cs" <<CS
using Microsoft.EntityFrameworkCore;
using $CONTEXT_NAMESPACE;

var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseSqlServer("Server=localhost;Database=ModelCheck;Trusted_Connection=True;TrustServerCertificate=True")
    .Options;
using var context = new AppDbContext(options);
Console.WriteLine(context.Database.GenerateCreateScript());
CS
dotnet run --project "$HARNESS" -nologo -v q
