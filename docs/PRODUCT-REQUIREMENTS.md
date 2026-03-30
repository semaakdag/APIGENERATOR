# Product Requirements

## Product Statement

API Generator Platform is a VS Code based developer tool that generates consistent .NET API solutions from SQL schema, metadata, and learned company standards.

## Objectives

- Reduce API bootstrap time from hours to minutes.
- Standardize project structure and naming across teams.
- Lower onboarding cost for new backend developers.
- Preserve company-specific architectural conventions.

## Primary Users

- Backend developers
- Technical leads
- Platform/tooling engineers

## Functional Requirements

### FR-1 API generation from SQL schema

- System must accept `.sql` schema files as input.
- System must extract tables, columns, keys, nullability, type length, and defaults.
- System must generate Clean Architecture oriented layers.
- System must generate CRUD-oriented baseline endpoints for each entity.

### FR-2 Company standard learning

- System must accept an existing .NET solution or project folder.
- System must analyze folder layout, naming rules, interfaces, inheritance, DI patterns, controller structure, and test style.
- System must output a reusable versioned standard profile in JSON.

### FR-3 Code structure extraction

- System must identify namespaces, referenced projects, base classes, and shared abstractions.
- System must infer whether patterns such as unit of work, repository, base entity, and service abstraction are in use.

### FR-4 Custom endpoint generation

- System must generate predefined endpoint recipes such as `GetByCode`, `GetActiveList`, `Search`, `BulkInsert`, and `GetByDateRange`.
- System must update controller, service, repository, tests, and documentation consistently.

### FR-5 Unit test generation

- System must generate xUnit test files.
- System must use Moq and FluentAssertions conventions.
- System must include success, validation failure, not found, and exception scenarios for each endpoint recipe.

### FR-6 Swagger and OpenAPI setup

- Generated APIs must include Swagger/OpenAPI bootstrap.
- Generated endpoints must support XML comments and response metadata.
- Authentication integration points must be scaffolded.

### FR-7 Documentation generation

- System must generate `README.md`, `API-DOCUMENTATION.md`, and `ARCHITECTURE.md`.
- Documentation must include project structure, setup instructions, and endpoint inventory.

### FR-8 Extension workflow

- Extension must expose commands for create, generate, learn, add endpoint, and document operations.
- Extension must provide a Webview-based wizard.
- Extension must show logs, output summary, and generated file paths.

### FR-9 Template customization

- Templates must be externalized.
- System must support template overrides per profile or workspace.
- Template validation errors must be surfaced clearly.

### FR-10 Safe regeneration

- System must distinguish between create, update, and conflict cases.
- System should support preview and overwrite strategies.

## Non-Functional Requirements

### NFR-1 Extensibility

- New SQL dialects, endpoint recipes, and templates must be added without rewriting the core generator.

### NFR-2 Performance

- The generator should complete a typical 10-entity API in under 5 seconds after warm startup.

### NFR-3 Reliability

- Invalid inputs must fail with actionable error messages.
- Partial generation failures must provide a manifest of successful and failed files.

### NFR-4 Maintainability

- Codebase must be modular and testable.
- Core generation logic must be isolated from the VS Code extension.

### NFR-5 Observability

- CLI should emit structured logs and a generation summary.

### NFR-6 Security

- User-selected paths must be validated.
- No arbitrary script execution from templates or profiles.

## Constraints

- Extension layer uses TypeScript.
- Generator engine uses C# on .NET 8.
- Template engine is Scriban.
- Code analysis uses Roslyn.

## Acceptance Criteria

- A developer can run `Create API` in VS Code and generate a baseline .NET API scaffold from a SQL file.
- A tech lead can run `learn` against an existing company solution and obtain a reusable JSON profile.
- Generated output contains source files, tests, Swagger bootstrap, and documentation.
- The platform supports iterative expansion without architectural rewrites.
