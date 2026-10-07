# CLI Reference

The generator engine (`generator-engine`, .NET 8) is what the VS Code extension runs. Every command can also be used
directly:

```bash
dotnet generator-engine/bin/Debug/net8.0/ApiGenerator.Cli.dll <command> [options]
```

## Commands

| Command | Purpose |
| --- | --- |
| `generate` | Generate a solution from a SQL schema with a framework preset (`--framework`) or from a reference project (`--project`, Default Framework mode). |
| `add-endpoint` | Add a recipe endpoint to a generated solution. |
| `entities` | List the entities of a generated solution and their properties (used by the extension's endpoint form). |
| `learn` | Learn a company standard profile from an existing project and save it as JSON. |
| `analyze` | Print the profile `learn` would produce, without saving it. |
| `document` | Write documentation for an existing output folder. |

### generate

| Option | Description |
| --- | --- |
| `--schema <file.sql>` | SQL Server `CREATE TABLE` script (required). |
| `--output <folder>` | Output folder; its name becomes the solution name. |
| `--framework <id or file>` | Framework preset id (`minimal-api-swagger`, `aspnet-controller-swagger`, `enterprise-controller-loghelper-swagger`) or a `.profile.json` path. |
| `--project <folder, .csproj or .sln>` | Reference project for Default Framework mode (used when `--framework` is omitted). |
| `--profile <file>` | Extra profile overlay. |
| `--windows-auth`, `--unit-tests`, `--postman-collection` | `Enable` / `Disable` feature toggles for presets. |
| `--connection-string <value>` | Connection string written to appsettings. |
| `--overwrite-mode Skip\|Overwrite\|Fail` | What to do with files that already exist and differ (default `Skip`). `Overwrite` regenerates from scratch, see below. |
| `--dry-run` | Plan only; nothing is written. |
| `--templates <folder>` | Files here replace built-in templates with the same name. Defaults to `.api-generator/templates` in the current folder when it exists. |
| `--llm-*` | Optional LLM refinement pass. |

#### Overwrite regenerates from scratch

With `--overwrite-mode Overwrite` the output is rebuilt from the schema and the reference/preset alone:

- files listed in the previous `generation-manifest.json` or created by `add-endpoint` that are not part of the new
  plan are deleted (status `deleted`, or `would-delete` with `--dry-run`), together with folders left empty;
- endpoints added with `add-endpoint` are not carried over and `api-generator.endpoints.json` is removed;
- files the generator never wrote (notes, your own classes) are left in place.

`Skip` and `Fail` keep earlier output and recipes. In Default Framework mode the output folder may not be the reference
project or a folder inside it (exit code `2`); the reference is only read.

#### Following the reference project

Default Framework mode learns from the reference: project names and folders (including where repository interfaces
and tests live), file names such as `CustomerCreateRequest`, singular entity names (`Customer` for table `Customers`)
with plural feature folders (`Services/Customers`), and whether DTO classes exist. Project references follow the
reference too: when repository interfaces live in the data-access project, the business project references it.

### add-endpoint

```bash
add-endpoint --project <generated solution> --entity <Entity> --recipe <Recipe> [--field <Property>] [--dry-run]
```

| Recipe | Field | HTTP |
| --- | --- | --- |
| `GetByCode` | any scalar property (not `bool` or `byte[]`) | `GET /api/<Entity>/by-<field>/{value}` – 404 when nothing matches |
| `GetActiveList` | `bool` property (default `IsActive`) | `GET /api/<Entity>/active` |
| `Search` | `string` property | `GET /api/<Entity>/search-by-<field>?term=` – 400 without a term |
| `GetByDateRange` | `DateTime`, `DateOnly` or `DateTimeOffset` property | `GET /api/<Entity>/by-<field>-range?from=&to=` – 400 when `from > to` |
| `BulkInsert` | – | `POST /api/<Entity>/bulk` – 201, 400 for an empty list, 409 (nothing inserted) when a key already exists |

Each recipe adds partial files next to the existing types (repository interface and implementation, service interface
and implementation, controller or endpoint module), unit tests for success, validation, not-found and exception cases,
and a row in `docs/API-DOCUMENTATION.md`. Existing method bodies are never edited; types written without `partial` get
the modifier. Recipes are recorded in `api-generator.endpoints.json`, so running `generate` again keeps their
documentation and endpoint registrations. Running the same recipe twice changes nothing.

## Exit codes and output

| Exit code | Meaning |
| --- | --- |
| `0` | Success |
| `1` | Unexpected error |
| `2` | Invalid input: missing or unreadable files, unknown preset, invalid profile or template, schema without tables, unsafe paths |
| `3` | Conflicting files with `--overwrite-mode Fail` |

`--log-format Json` prints one JSON object per line instead of plain text, for example:

```json
{"level":"info","event":"generation-summary","message":"Files: 47, created: 47, ...","totalFiles":47,"created":47}
{"level":"error","event":"command-failed","message":"Schema file was not found.","exitCode":2,"category":"invalid-input"}
```

## Warnings

Warnings are printed and stored in `generation-manifest.json` (`Warnings`):

- SQL parser diagnostics with the original line number (unknown column types mapped to `string`, definitions that could
  not be parsed).
- Tables without a primary key (the first column is used as the key).
- Profiles without `SchemaVersion` or with an older one.
- Template override files that do not match a built-in template.

## Profiles

Profiles carry a `SchemaVersion` of the form `major.minor`. This version of the generator reads major version `2`:
profiles with a newer major version are rejected, older or missing versions are accepted with a warning. Profiles are
JSON overlays applied in this order: built-in defaults, learned reference project, framework preset, `--profile`.
Objects are merged by key; arrays replace inherited arrays unless they are empty.

## Safety

- Every planned file must resolve inside the output folder; profiles that try to write elsewhere are rejected before
  anything is written.
- Templates and profile overrides are validated before generation; errors name the template and line.
- Templates cannot include other files.
