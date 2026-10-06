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
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using $CONTEXT_NAMESPACE;

var options = new DbContextOptionsBuilder<AppDbContext>()
    .UseSqlServer("Server=127.0.0.1,1;Database=ModelCheck;User Id=x;Password=x;TrustServerCertificate=True;Connect Timeout=1")
    .Options;
using (var context = new AppDbContext(options))
{
    Console.WriteLine(context.Database.GenerateCreateScript());
}

// Queries are translated to SQL before a connection is opened, so untranslatable LINQ shows up here
// even without a database; a connection failure means the query itself was fine.
var failures = 0;
var repositories = typeof(AppDbContext).Assembly.GetTypes()
    .Where(type => type.IsClass && !type.IsAbstract && type.GetConstructor([typeof(AppDbContext)]) is not null);
foreach (var repositoryType in repositories)
{
    foreach (var methodName in new[] { "GetByIdAsync", "DeleteAsync" })
    {
        var method = repositoryType.GetMethod(methodName);
        if (method is null)
        {
            continue;
        }

        using var context = new AppDbContext(options);
        var repository = Activator.CreateInstance(repositoryType, context);
        var arguments = method.GetParameters()
            .Select(parameter => parameter.ParameterType == typeof(CancellationToken)
                ? CancellationToken.None
                : parameter.ParameterType.IsValueType ? Activator.CreateInstance(parameter.ParameterType) : (object)"x")
            .ToArray();
        try
        {
            await (Task)method.Invoke(repository, arguments)!;
        }
        catch (Exception exception) when (exception.GetType().Name == "SqlException")
        {
        }
        catch (Exception exception)
        {
            failures++;
            Console.Error.WriteLine(\$"QUERY-CHECK-FAILED {repositoryType.Name}.{methodName}: {exception.GetType().Name}: {exception.Message}");
        }
    }
}

return failures == 0 ? 0 : 1;
CS
dotnet run --project "$HARNESS" -nologo -v q
