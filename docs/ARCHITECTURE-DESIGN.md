# Architecture Design

## Vision

API Generator Platform is a developer productivity system that generates consistent .NET API solutions from schema, metadata, and learned company standards. The architecture must support both fast one-shot generation and long-term maintainability.

## Design Principles

- Clean separation between orchestration, analysis, and generation.
- Strong contracts between VS Code and CLI layers.
- Deterministic generation outputs.
- Template-driven customization instead of hard-coded code emission.
- Profile-driven standardization for company-specific rules.
- Replaceable modules for parser, analyzer, renderer, and document generator.

## System Context

Primary actors:

- Backend developer generating a new API.
- Tech lead standardizing output with a company profile.
- Platform engineer extending templates and rules.

External dependencies:

- VS Code extension host.
- .NET 8 runtime.
- Roslyn/MSBuild for code analysis.
- Scriban for templates.

## High-Level Architecture

### VS Code Extension Layer

Responsibilities:

- Expose commands.
- Collect input through a wizard webview.
- Persist generator profiles.
- Execute the generator CLI.
- Render progress and generation results.

Submodules:

- `commands`: command entry points and activation wiring.
- `webview`: multi-step wizard, progress view, output summary.
- `services`: CLI process execution, profile storage, workspace access.
- `state`: persisted session selections, recent schemas, last-used profile.

Design improvements:

- Move from a single page form to a step-based wizard.
- Use a typed request contract for CLI invocations.
- Show generation preview before execution.
- Keep command handlers thin; business rules stay in generator engine.

### Generator Engine (.NET 8 CLI)

Responsibilities:

- Parse SQL schema.
- Build generation models.
- Render templates through Scriban.
- Analyze existing projects with Roslyn.
- Generate documentation.

Submodules:

- `Commands`: CLI command routing and argument validation.
- `SqlParser`: schema tokenizer, parser, and semantic mapping.
- `Analyzers`: Roslyn-based pattern extraction and profile synthesis.
- `Generators`: use-case orchestration and output planning.
- `Templates`: Scriban template loading, validation, and rendering.
- `Documentation`: README, API docs, architecture docs, and manifest generation.

## Core Domain Model

- `DatabaseSchema`: parsed SQL source.
- `TableDefinition`: logical entity source.
- `ColumnDefinition`: column metadata and constraints.
- `StandardProfile`: learned conventions, naming rules, and architectural choices.
- `GenerationPlan`: files to create, overwrite policy, template bindings.
- `GenerationManifest`: output summary used by the extension for reporting.

## End-to-End Flows

### Create API / Generate From SQL

1. Extension collects schema path, output path, options, and target profile.
2. Extension sends a typed command to CLI.
3. CLI parses the schema and loads the selected profile.
4. Generator builds a `GenerationPlan`.
5. Templates render all layers and tests.
6. Documentation generator creates markdown deliverables.
7. CLI emits a manifest and logs.
8. Extension opens generated files and summary.

### Learn Company Standard

1. User selects an existing company project or solution.
2. CLI loads solution metadata through Roslyn/MSBuild.
3. Analyzer extracts folders, namespaces, naming, patterns, and DI registrations.
4. Analyzer emits a reusable `StandardProfile`.
5. Extension stores and exposes that profile in future generation sessions.

### Add Endpoint

1. User selects target entity and endpoint recipe.
2. Generator loads the existing project model.
3. Endpoint generator updates controller, service, repository, tests, Swagger annotations, and docs.
4. Output manifest lists modified files.

## Architectural Boundaries

- Extension never generates source code directly.
- Generator engine never depends on VS Code APIs.
- Analyzers emit profiles; generators consume profiles.
- Documentation uses the same schema/profile model as code generation.

## Key Decisions

- UI and generation logic are decoupled through CLI execution.
- Templates are externalized for customization.
- Learned company standards are stored as reusable JSON profiles.
- Generation models stay simple and deterministic.

Additional decisions:

- CLI commands remain idempotent when inputs are unchanged.
- Profile schema is versioned to support future migration.
- Generated output includes a manifest to enable safe incremental updates.
- Custom endpoint generation uses recipes rather than ad-hoc text mutation.
- Regeneration is policy-driven through `skip`, `overwrite`, and `fail` modes.

## Target Output Structure

```text
GeneratedApi/
  src/
    GeneratedApi.Domain/
    GeneratedApi.Application/
    GeneratedApi.Infrastructure/
    GeneratedApi.Api/
  tests/
    GeneratedApi.UnitTests/
  docs/
    README.md
    API-DOCUMENTATION.md
    ARCHITECTURE.md
  generation-manifest.json
```

This target structure is now the baseline generation shape. Future work should focus on richer scaffolding inside these projects rather than changing the top-level layout again.

## Non-Functional Architecture Requirements

- Generation latency target: less than 10 seconds for 50-entity schemas on a typical developer machine.
- Safe overwrite policy with preview/diff awareness.
- Structured logging for CLI and extension.
- Template validation before write operations.
- Analyzer resilience against partially broken solutions.

## Risks and Mitigations

- Roslyn analysis may fail on custom MSBuild setups.
  Mitigation: fall back to heuristic profile extraction.
- SQL dialect differences may break parsing.
  Mitigation: define dialect adapters starting with SQL Server baseline.
- Template drift can create invalid code.
  Mitigation: add golden-file and smoke generation tests.

## Recommended Next Design Improvements

- Split generator engine into multiple class libraries instead of a single CLI project.
- Add project scaffolding templates for full multi-project Clean Architecture output.
- Add endpoint recipe definitions in JSON/YAML to keep custom endpoints extensible.
- Add diff preview support in the extension for conflicted regeneration plans.
