# Task List

## Delivery Strategy

Work is organized into three release tracks:

- `MVP`: prove end-to-end generation from SQL.
- `Foundation`: harden architecture, profiles, and template system.
- `Enterprise`: add safe regeneration, richer analysis, and advanced endpoint recipes.

## Phase 1: Product Foundation

- [ ] Finalize product scope and requirement IDs.
- [x] Define CLI request/response contracts.
- [ ] Define profile schema versioning policy.
- [x] Define generation manifest schema.
- [x] Define overwrite/conflict behavior.

Deliverable:

- Approved requirements baseline and architecture contract.

## Phase 2: Target Solution Structure

- [x] Move generated output to `src/`, `tests/`, and `docs/` structure.
- [x] Add solution and project scaffolding templates.
- [ ] Define package/reference strategy for generated APIs.
- [ ] Add workspace-relative output resolution.

Deliverable:

- Compile-ready multi-project Clean Architecture scaffold.

## Phase 3: VS Code Extension

- [ ] Replace single-page webview with step-based wizard.
- [ ] Add typed form model and validation.
- [ ] Add profile selection and recent history.
- [ ] Add generation preview panel.
- [ ] Add log streaming and manifest summary view.
- [ ] Add error-state UX and retry flow.

Deliverable:

- Production-quality extension UX.

## Phase 4: CLI Core

- [x] Introduce `GenerationPlan` model.
- [x] Introduce `GenerationManifest` output.
- [x] Centralize path management and file writes.
- [ ] Add structured logging.
- [ ] Add command result exit codes and error categories.

Deliverable:

- Stable orchestration core for all commands.

## Phase 5: SQL Parser

- [ ] Expand parser to support constraints declared separately.
- [ ] Add foreign key reference extraction.
- [ ] Add SQL Server dialect normalization rules.
- [ ] Add test coverage for multi-table schemas.
- [ ] Add parser diagnostics with line-level errors.

Deliverable:

- Reliable schema-to-model parser.

## Phase 6: Template Engine

- [ ] Add template catalog and metadata.
- [ ] Add template validation before generation.
- [ ] Add workspace template override support.
- [ ] Add project scaffolding templates for csproj, DI, and Swagger wiring.
- [ ] Add golden-file tests for template output.

Deliverable:

- Customizable and testable generation layer.

## Phase 7: Company Standard Learning

- [ ] Extract namespace conventions.
- [ ] Extract service/repository/controller naming patterns.
- [ ] Extract base classes and interface contracts.
- [ ] Extract DI registration style.
- [ ] Extract unit test arrangement style.
- [ ] Add fallback heuristics when Roslyn solution load fails.

Deliverable:

- Useful and repeatable standard profile generation.

## Phase 8: Custom Endpoint Recipes

- [ ] Introduce endpoint recipe model.
- [ ] Implement `GetByCode` recipe.
- [ ] Implement `Search` recipe.
- [ ] Implement `BulkInsert` recipe.
- [ ] Implement endpoint-driven test generation.
- [ ] Update docs and Swagger metadata per recipe.

Deliverable:

- Incremental endpoint generation without manual edits.

## Phase 9: Documentation

- [ ] Add richer README sections.
- [ ] Generate endpoint tables with request/response schemas.
- [ ] Generate architecture dependency map.
- [ ] Generate generation summary and profile metadata sections.

Deliverable:

- Useful generated project documentation.

## Phase 10: Quality Gate

- [ ] Add unit tests for parser, renderer, and analyzer.
- [ ] Add integration tests for `generate`, `learn`, and `document`.
- [ ] Add smoke test for sample schema generation.
- [ ] Add CI pipeline for build and test.

Deliverable:

- Releasable engineering baseline.

## Priority Order

1. Multi-project generated solution structure
2. Typed CLI contracts and generation manifest
3. Step-based extension wizard
4. Parser hardening
5. Full Roslyn learning rules
6. Endpoint recipe system

## Current Gaps Identified

- Generated output is compile-ready, but still uses placeholder in-memory repositories rather than production persistence.
- Extension UI is functional but too thin for enterprise workflows.
- Safe incremental regeneration now supports `skip`, `overwrite`, and `fail`, but no diff preview UI exists yet.
- Analyzer currently produces only a baseline profile.
- Template coverage is incomplete for full CRUD and infrastructure wiring.
