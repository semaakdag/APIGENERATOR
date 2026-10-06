# API Generator

API Generator is a packaged VS Code extension for generating standardized .NET API projects from SQL schemas, framework packs, and reusable company profiles.

## What is bundled

- A Windows self-contained CLI under `bundled/cli`
- Built-in framework packs and standard profiles under `bundled/profiles`
- The webview UI and sidebar commands under `dist`

## Local packaging

Run these commands from the `extension` folder:

```powershell
npm install
npm run package:vsix
```

The package step will:

1. Build the extension TypeScript output.
2. Publish the .NET CLI into `bundled/cli`.
3. Copy profile assets into `bundled/profiles`.
4. Produce `api-generator-platform.vsix`.

## Install the extension

In VS Code:

1. Open `Extensions`
2. Choose `...`
3. Select `Install from VSIX...`
4. Pick `api-generator-platform.vsix`

## Runtime note

The packaged build bundles the CLI published for Windows (`ApiGenerator.Cli.exe`). On Linux and macOS the extension runs
the same framework-dependent `ApiGenerator.Cli.dll` through `dotnet`, so the .NET 8 runtime must be installed. Set
`API_GENERATOR_CLI_PATH` to use a locally built CLI (`.exe` or `.dll`) during development.

## End-to-end tests

`npm run test:e2e` loads the compiled extension against a VS Code API stub, renders its webview in Chromium and drives
every mode through the real CLI. Screenshots are written to `artifacts/ui-e2e`.