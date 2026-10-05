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
- [x] Add test coverage for multi-table schemas.
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

- [ ] Add unit tests for parser, renderer, and analyzer. (parser and profile merge covered in `tests/ApiGenerator.Cli.Tests`; renderer and analyzer still open)
- [x] Add integration tests for `generate`, `learn`, and `document`. (`tools/cli-cases.sh`)
- [x] Add smoke test for sample schema generation. (`tools/smoke-test.sh`)
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

## QA Backlog (test loop)

Run everything with `tools/run-all-tests.sh` (requires .NET 8 SDK, Node 22 and Python 3). It stops at the first failing
suite; after a fix the whole run is restarted from the beginning.

| Suite | What it covers |
| --- | --- |
| `tests/ApiGenerator.Cli.Tests` | SQL parser (comments, schemas, quoted names, composite keys, identity, store types, FKs) and profile overlay merge |
| `tools/cli-cases.sh` | overwrite modes, dry-run, idempotent rerun, invalid input and error paths, `learn` / `analyze` / `document` |
| `tools/smoke-test.sh` | every framework preset x unit tests on/off, every company profile in Default Framework mode, SQL edge cases (`tools/cases/edge-cases.sql`), EF model check (`tools/ef-model-check.sh`), runtime CRUD over HTTP (`tools/runtime-test.py`), learner round trip, NuGet vulnerability gate |
| extension | TypeScript build |

### Fixed

- [x] BUG-1 CLI did not compile: ambiguous `string.Split` collection expressions.
- [x] BUG-2 `minimal-api-swagger` output did not compile: `AppDbContext` using loop and repository `Set<>` type.
- [x] SEC-1 Scriban 5.10.0 had critical/high advisories; upgraded to 7.5.0 with byte-identical template output.
- [x] SEC-2 Generated APIs pinned vulnerable EF Core / Negotiate 8.0.0; bumped to 8.0.31.
- [x] BUG-3 Empty `SharedFiles` in a profile overlay wiped learned shared files that learned `apiProgram` needs.
- [x] BUG-4 `company-standard.profile.json` carried stale service/repository overrides hardcoding Users fields.
- [x] BUG-5 Unknown `--framework` preset was silently ignored.
- [x] BUG-6 Schemas without `CREATE TABLE` generated an empty solution.
- [x] BUG-7 `learn` / `analyze` printed stack traces for invalid paths.
- [x] BUG-8 docx / Postman output was non-deterministic, causing false conflicts on regeneration.
- [x] BUG-9 Parser picked up `CREATE TABLE` inside `/* */` comments and dropped the table schema.
- [x] BUG-10 EF `AppDbContext` had no table / key / column mapping (non-conventional and composite keys failed at runtime).
- [x] BUG-11 Non-IDENTITY integer keys were treated as identity by EF and column store types were lost.
- [x] BUG-12 Learner hardcoded the sample entity's key name into the learned controller template.
- [x] BUG-13 Learner treated a single-project API with a test project as a layered solution.
- [x] Profile fixtures re-encoded as UTF-8; `feature-check-smoke` profile stale entity overrides removed.

### Open

- [ ] Composite-key tables: CRUD endpoints, services and repositories address rows by the first key column only
  (EF mapping is correct). Needs a route/API design decision (e.g. `/{tenantId}/{itemId}`) before implementing.
- [ ] Tables without a primary key fall back to the first column as key; consider read-only generation instead.
- [ ] Add CI pipeline running `tools/run-all-tests.sh`.
