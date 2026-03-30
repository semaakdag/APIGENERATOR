# API Generator Platform

VS Code extension and .NET 8 CLI for generating standardized API projects from SQL schema, metadata, and learned company conventions.

## Architecture

- `extension`: VS Code extension layer built with TypeScript and a Webview wizard.
- `generator-engine`: .NET 8 CLI responsible for parsing, analysis, code generation, and documentation.
- `profiles`: learned company standard profiles.

## Planning Documents

- `docs/PRODUCT-REQUIREMENTS.md`: functional and non-functional requirements.
- `docs/TASK-LIST.md`: phased implementation backlog and delivery plan.
- `docs/ARCHITECTURE-DESIGN.md`: improved target architecture and component design.
