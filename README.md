# API Generator Platform

VS Code extension and .NET 8 CLI for generating standardized API projects from SQL schema, metadata, and learned company conventions.

## Architecture

- `extension`: VS Code extension layer built with TypeScript and a Webview wizard.
- `generator-engine`: .NET 8 CLI responsible for parsing, analysis, code generation, and documentation.
- `profiles`: learned company standard profiles.

## Quick Start

```bash
dotnet build generator-engine
dotnet generator-engine/bin/Debug/net8.0/ApiGenerator.Cli.dll generate --schema examples/users.sql --framework aspnet-controller-swagger --output MyApi
dotnet generator-engine/bin/Debug/net8.0/ApiGenerator.Cli.dll add-endpoint --project MyApi --entity Users --recipe GetByCode --field Email
```

See `docs/CLI-REFERENCE.md` for every command, recipe, exit code and option.

## Tests

`tools/run-all-tests.sh` runs the CLI unit tests, CLI behaviour cases, the generation smoke test (every preset and
profile is generated, built, unit tested, checked against SQL Server query translation and exercised over HTTP) and the
VS Code extension end-to-end tests (the real webview in Chromium driving the real CLI). It needs the .NET 8 SDK,
Node.js 22, Python 3 and Chromium (`CHROMIUM_PATH` or Playwright's browser folder).

## Planning Documents

- `docs/PRODUCT-REQUIREMENTS.md`: functional and non-functional requirements.
- `docs/TASK-LIST.md`: phased implementation backlog and delivery plan.
- `docs/ARCHITECTURE-DESIGN.md`: improved target architecture and component design.
- `docs/BACKLOG.md`: requirement-mapped backlog with UI, backend and QA tasks and the bugs found by the test loop.
- `docs/CLI-REFERENCE.md`: commands, recipes, exit codes, logging and profile rules.
