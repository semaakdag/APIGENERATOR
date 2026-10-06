"use strict";

// UI end-to-end scenarios. Stops at the first failing scenario so it can be fixed and the run restarted.
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { ExtensionUiHarness, repoRoot } = require("./harness");

const MOJIBAKE = /Ã.|Å.|Ä.|Â./;
const scenarios = [];
const scenario = (name, run) => scenarios.push({ name, run });

const text = (ui, selector) => ui.page.locator(selector).innerText();

async function fill(ui, id, value) {
  await ui.page.locator(`#${id}`).fill(value);
  await ui.page.locator(`#${id}`).dispatchEvent("change");
}

async function selectFramework(ui, frameworkId) {
  await openTab(ui, "config");
  const value = await ui.page.locator("#framework option").evaluateAll(
    (options, id) => (options.find((option) => option.value.includes(id)) || {}).value || "",
    frameworkId);
  assert.ok(value, `framework option ${frameworkId} is listed`);
  await ui.page.locator("#framework").selectOption(value);
}

async function openTab(ui, tab) {
  await ui.page.locator(`[data-tab-button="${tab}"]`).click();
}

async function run(ui) {
  await ui.page.locator("#run").click();
  await ui.waitIdle();
}

scenario("every mode renders without script errors or broken text", async (ui) => {
  for (const mode of ["create", "generate", "learn", "endpoint", "document"]) {
    await ui.openMode(mode);
    const body = await text(ui, "body");
    assert.doesNotMatch(body, MOJIBAKE, `${mode} mode shows mojibake text`);
    const tabs = mode === "create" || mode === "generate"
      ? ["config", "database", "runtime", "llm", "results", "output"]
      : ["config", "results", "output"];
    assert.equal(await ui.page.locator("[data-tab-button]").count(), tabs.length, `${mode} offers ${tabs.join(", ")}`);
    for (const tab of tabs) {
      await openTab(ui, tab);
      const panelText = await text(ui, `[data-tab-panel="${tab}"]`);
      assert.doesNotMatch(panelText, MOJIBAKE, `${mode}/${tab} shows mojibake text`);
    }
    await ui.screenshot(`mode-${mode}`);
  }
});

scenario("create mode generates a project from a framework preset", async (ui) => {
  await ui.openMode("create");
  await fill(ui, "schema", "examples/users.sql");
  await fill(ui, "output", "MinimalApi");
  await selectFramework(ui, "minimal-api-swagger");
  await run(ui);
  const result = ui.lastResult();
  assert.equal(result.payload.exitCode, 0, result.payload.stderr);
  assert.ok(result.payload.manifest, "manifest returned");
  assert.ok(fs.existsSync(path.join(ui.workspace, "MinimalApi", "MinimalApi.sln")), "solution written");
  assert.equal(await ui.page.locator('[data-tab-button="results"]').getAttribute("aria-selected"), "true");
  assert.ok(Number(await text(ui, "#metricCreated")) > 0, "created metric shown");
  const listed = await ui.page.locator("#fileList li").count();
  assert.equal(listed, result.payload.manifest.GeneratedFiles.length, "every generated file is listed");
  await ui.page.locator('#fileList [data-open-file$="/MinimalApi.sln"]').click();
  await ui.waitIdle();
  const opened = ui.stub.executedCommands.filter((command) => command.id === "vscode.open").pop();
  assert.ok(opened && opened.args[0].fsPath.endsWith(path.join("MinimalApi", "MinimalApi.sln")), "clicking a file opens it");
  await ui.screenshot("create-minimal-results");
});

scenario("a failing CLI run shows the error instead of crashing the webview", async (ui) => {
  await ui.openMode("generate");
  await fill(ui, "schema", "examples/missing.sql");
  await fill(ui, "output", "Broken");
  await selectFramework(ui, "minimal-api-swagger");
  await run(ui);
  assert.notEqual(ui.lastResult().payload.exitCode, 0);
  assert.match(await text(ui, "#statusPill"), /Başarısız/i);
  assert.ok(await ui.page.locator("#errorCard").isVisible(), "error card is shown");
  assert.match(await text(ui, "#errorText"), /Schema file was not found/);
  await ui.screenshot("generate-failure");
  await openTab(ui, "output");
  assert.match(await text(ui, "#outputBox"), /Schema file was not found/);

  // Fix the input and retry from the error card.
  await openTab(ui, "results");
  await fill(ui, "schema", "examples/users.sql");
  await ui.page.locator("#retryRun").click();
  await ui.waitIdle();
  assert.equal(ui.lastResult().payload.exitCode, 0, ui.lastResult().payload.stderr);
  assert.ok(!(await ui.page.locator("#errorCard").isVisible()), "error card hidden after a successful retry");
});

scenario("framework choice toggles reference-project, profile and checklist sections", async (ui) => {
  await ui.openMode("generate");
  const visible = (id) => ui.page.locator(`#${id}`).isVisible();
  await selectFramework(ui, "minimal-api-swagger");
  assert.equal(await visible("defaultFrameworkProjectSection"), false, "reference project hidden for presets");
  assert.equal(await visible("profileSection"), false, "profile hidden for presets");
  await openTab(ui, "runtime");
  assert.equal(await visible("defaultFrameworkFeatureNote"), false);
  assert.equal(await visible("frameworkFeatureChecklist"), true);
  assert.equal(await ui.page.locator("#featureUnitTests").isEnabled(), true);
  await ui.screenshot("preset-runtime");

  await openTab(ui, "config");
  await ui.page.locator("#framework").selectOption("");
  assert.equal(await visible("defaultFrameworkProjectSection"), true, "reference project shown for default framework");
  assert.equal(await visible("profileSection"), true);
  await openTab(ui, "runtime");
  assert.equal(await visible("defaultFrameworkFeatureNote"), true);
  assert.equal(await visible("frameworkFeatureChecklist"), false);
  await ui.screenshot("default-framework-runtime");
});

scenario("default framework without a reference project is blocked before the CLI runs", async (ui) => {
  await ui.openMode("generate");
  await fill(ui, "schema", "examples/users.sql");
  await openTab(ui, "config");
  await ui.page.locator("#framework").selectOption("");
  await fill(ui, "project", "");
  const before = ui.posted.length;
  await run(ui);
  assert.equal(ui.posted.slice(before).filter((message) => message.type === "result").length, 0, "CLI must not run");
  assert.match(await text(ui, "#statusPill"), /Engellendi/i);
});

scenario("file pickers fill the schema and reference project fields", async (ui) => {
  await ui.openMode("generate");
  const schemaPath = path.join(ui.workspace, "examples", "users.sql");
  ui.stub.openDialogAnswers.push([schemaPath]);
  await ui.page.locator("#pickSchemaFile").click();
  await ui.waitIdle();
  assert.equal(await ui.page.locator("#schema").inputValue(), schemaPath);

  await openTab(ui, "config");
  await ui.page.locator("#framework").selectOption("");
  ui.stub.openDialogAnswers.push([ui.workspace]);
  await ui.page.locator("#pickProjectFolder").click();
  await ui.waitIdle();
  assert.equal(await ui.page.locator("#project").inputValue(), ui.workspace);
  assert.match(await text(ui, "#frameworkSummaryCopy"), /projesini referans alıp/);

  // A cancelled dialog leaves the field untouched.
  await ui.page.locator("#pickSchemaFile").click();
  await ui.waitIdle();
  assert.equal(await ui.page.locator("#schema").inputValue(), schemaPath);
});

const visible = (ui, selector) => ui.page.locator(selector).isVisible();
const optionValues = (ui, selector) => ui.page.locator(`${selector} option`).evaluateAll((options) => options.map((option) => option.value));

scenario("required fields are validated inline and the CLI is not started", async (ui) => {
  await ui.openMode("create");
  await fill(ui, "schema", "");
  await fill(ui, "output", "");
  await selectFramework(ui, "minimal-api-swagger");
  const before = ui.posted.length;
  await run(ui);
  assert.equal(ui.posted.slice(before).filter((message) => message.type === "result").length, 0, "CLI must not run");
  assert.match(await text(ui, '[data-error-for="schema"]'), /SQL dosyası zorunludur/);
  assert.match(await text(ui, '[data-error-for="output"]'), /Çıktı klasörü zorunludur/);
  assert.equal(await ui.page.locator("#schema").getAttribute("aria-invalid"), "true");
  await ui.screenshot("validation-errors");

  await fill(ui, "schema", "examples/users.sql");
  await fill(ui, "output", "ValidatedApi");
  await run(ui);
  assert.equal(await ui.page.locator("[data-error-for]").count(), 0, "errors cleared once the form is valid");
  assert.equal(ui.lastResult().payload.exitCode, 0, ui.lastResult().payload.stderr);
});

scenario("preview lists planned files without writing, then warnings are shown after generation", async (ui) => {
  await ui.openMode("generate");
  await fill(ui, "schema", "examples/edge-cases.sql");
  await fill(ui, "output", "PreviewApi");
  await selectFramework(ui, "enterprise-controller-loghelper-swagger");
  await ui.page.locator("#preview").click();
  await ui.waitIdle();
  const preview = ui.lastResult().payload;
  assert.equal(preview.exitCode, 0, preview.stderr);
  assert.equal(preview.preview, true);
  assert.equal(fs.existsSync(path.join(ui.workspace, "PreviewApi")), false, "preview must not write files");
  assert.match(await text(ui, "#statusPill"), /Önizleme/i);
  assert.match(await text(ui, "#fileList"), /Oluşturulacak/i);
  assert.equal(await ui.page.locator("#fileList [data-open-file]").count(), 0, "planned files are not links");
  assert.ok(await visible(ui, "#warningCard"), "warnings visible in preview");
  await ui.screenshot("preview");

  await run(ui);
  assert.equal(ui.lastResult().payload.exitCode, 0);
  assert.ok(fs.existsSync(path.join(ui.workspace, "PreviewApi", "PreviewApi.sln")));
  assert.match(await text(ui, "#warningList"), /NoPrimaryKey/);
  assert.match(await text(ui, "#warningTitle"), /Uyarılar \(\d+\)/);
  await ui.screenshot("warnings");
});

scenario("recent schema and output values are offered after a successful run", async (ui) => {
  await ui.openMode("create");
  const schemas = await optionValues(ui, "#recentSchemas");
  assert.ok(schemas.includes("examples/users.sql"), `recent schemas: ${schemas}`);
  assert.ok((await optionValues(ui, "#recentOutputs")).includes("PreviewApi"));
  assert.equal(await ui.page.locator("#schema").getAttribute("list"), "recentSchemas");
});

scenario("add endpoint: load entities, filter fields, preview, add and detect duplicates", async (ui) => {
  await ui.openMode("create");
  await fill(ui, "schema", "examples/recipes.sql");
  await fill(ui, "output", "RecipeApi");
  await selectFramework(ui, "enterprise-controller-loghelper-swagger");
  await run(ui);
  assert.equal(ui.lastResult().payload.exitCode, 0, ui.lastResult().payload.stderr);

  await ui.openMode("endpoint");
  assert.equal(await visible(ui, "#framework"), false, "generation settings are not part of the endpoint form");
  ui.stub.openDialogAnswers.push([path.join(ui.workspace, "RecipeApi")]);
  await ui.page.locator("#pickProjectFolder").click();
  await ui.waitIdle();
  assert.deepEqual(await optionValues(ui, "#entity"), ["Orders", "Products", "Stock"]);
  assert.match(await text(ui, "#entityCaption"), /3 varlık yüklendi/);

  await ui.page.locator("#entity").selectOption("Products");
  await ui.page.locator("#recipe").selectOption("Search");
  assert.deepEqual(await optionValues(ui, "#field"), ["Code", "Name"], "only string fields for Search");
  await ui.page.locator("#recipe").selectOption("GetByDateRange");
  assert.deepEqual(await optionValues(ui, "#field"), ["CreatedAt"]);
  await ui.page.locator("#recipe").selectOption("GetActiveList");
  assert.equal(await ui.page.locator("#field").inputValue(), "IsActive", "IsActive preselected");
  await ui.page.locator("#entity").selectOption("Stock");
  assert.deepEqual(await optionValues(ui, "#field"), []);
  assert.match(await text(ui, "#fieldCaption"), /uygun alan yok/);
  await run(ui);
  assert.match(await text(ui, '[data-error-for="field"]'), /uygun bir alan seçin/);

  await ui.page.locator("#entity").selectOption("Products");
  await ui.page.locator("#recipe").selectOption("GetByCode");
  await ui.page.locator("#field").selectOption("Code");
  await ui.page.locator("#preview").click();
  await ui.waitIdle();
  assert.equal(ui.lastResult().payload.exitCode, 0, ui.lastResult().payload.stderr);
  assert.match(await text(ui, "#fileList"), /Oluşturulacak/i);
  assert.equal(fs.existsSync(path.join(ui.workspace, "RecipeApi", "src", "RecipeApi.Api", "Controllers", "ProductsController.GetByCode.cs")), false);
  await ui.screenshot("endpoint-preview");

  await run(ui);
  assert.equal(ui.lastResult().payload.exitCode, 0, ui.lastResult().payload.stderr);
  assert.ok(fs.existsSync(path.join(ui.workspace, "RecipeApi", "src", "RecipeApi.Api", "Controllers", "ProductsController.GetByCode.cs")));
  assert.match(await text(ui, "#summaryMeta"), /GET \/api\/Products\/by-code\/\{code\}/);
  await ui.page.locator('#fileList [data-open-file$="ProductsController.GetByCode.cs"]').click();
  await ui.waitIdle();
  assert.ok(ui.stub.executedCommands.some((command) => command.id === "vscode.open" && command.args[0].fsPath.endsWith("ProductsController.GetByCode.cs")));
  await ui.screenshot("endpoint-added");

  await run(ui);
  assert.match(await text(ui, "#statusPill"), /Zaten Var/i);
});

scenario("learn mode saves a profile that becomes selectable", async (ui) => {
  await ui.openMode("learn");
  await fill(ui, "project", path.join(ui.workspace, "RecipeApi"));
  await fill(ui, "output", "profiles");
  await run(ui);
  assert.equal(ui.lastResult().payload.exitCode, 0, ui.lastResult().payload.stderr);
  assert.match(await text(ui, "#summaryMeta"), /Profile saved/);
  assert.match(await text(ui, "#statusPill"), /Tamamlandı/i);
  assert.ok(fs.existsSync(path.join(ui.workspace, "profiles", "company-standard.profile.json")));

  await ui.openMode("generate");
  await openTab(ui, "config");
  await ui.page.locator("#framework").selectOption("");
  const profiles = await optionValues(ui, "#profile");
  assert.ok(profiles.some((value) => value.endsWith(path.join("profiles", "company-standard.profile.json")) && value.startsWith(ui.workspace)), `profiles: ${profiles}`);
});

scenario("document mode writes documentation", async (ui) => {
  await ui.openMode("document");
  await fill(ui, "output", "RecipeApi");
  await run(ui);
  assert.equal(ui.lastResult().payload.exitCode, 0, ui.lastResult().payload.stderr);
  assert.match(await text(ui, "#summaryMeta"), /Documentation written/);
});

scenario("LLM settings are saved without exposing the token", async (ui) => {
  await ui.openMode("generate");
  await openTab(ui, "llm");
  await ui.page.locator("#llmEnabled").check();
  await fill(ui, "llmUrl", "https://llm.example.com/v1");
  await fill(ui, "llmModel", "test-model");
  await fill(ui, "llmToken", "secret-token");
  await ui.page.locator("#saveLlmSettings").click();
  await ui.waitIdle();
  assert.equal(ui.secrets.get("apiGenerator.llm.token"), "secret-token");
  assert.match(await text(ui, "#llmTokenStatus"), /Kayıtlı token var/);
  assert.ok(!(await ui.page.content()).includes("secret-token"), "token is not rendered back into the page");
  await ui.page.locator("#llmEnabled").uncheck();
  await ui.page.locator("#saveLlmSettings").click();
  await ui.waitIdle();
});

scenario("schema designer loads, appends and deletes tables", async (ui) => {
  await ui.openMode("generate");
  await fill(ui, "schema", "examples/users.sql");
  await openTab(ui, "database");
  await ui.page.locator("#loadSchemaDesigner").click();
  await ui.waitIdle();
  assert.match(await text(ui, "#schemaDesignerStatus"), /3 tablo yüklendi/);
  await fill(ui, "draftTableName", "Invoices");
  await ui.page.locator("#addDraftColumn").click();
  const rows = ui.page.locator("#draftColumns [data-column-index]");
  await rows.nth(1).locator('[data-column-field="name"]').fill("Total");
  await rows.nth(1).locator('[data-column-field="sqlType"]').fill("DECIMAL(18,2)");
  await ui.page.locator("#appendSchemaTable").click();
  await ui.waitIdle();
  assert.match(await text(ui, "#schemaDesignerStatus"), /'Invoices' tablosu şema dosyasına eklendi/);
  assert.match(fs.readFileSync(path.join(ui.workspace, "examples", "users.sql"), "utf8"), /CREATE TABLE \[?Invoices/i);
  await ui.screenshot("schema-designer");
  await ui.page.locator('[data-delete-table="Invoices"]').click();
  await ui.waitIdle();
  assert.match(await text(ui, "#schemaDesignerStatus"), /kaldırıldı/);
  assert.doesNotMatch(fs.readFileSync(path.join(ui.workspace, "examples", "users.sql"), "utf8"), /Invoices/);
});

scenario("a missing CLI is reported instead of leaving the run hanging", async (ui) => {
  const original = process.env.API_GENERATOR_CLI_PATH;
  await ui.openMode("generate");
  await fill(ui, "schema", "examples/users.sql");
  await fill(ui, "output", "NoCli");
  await selectFramework(ui, "minimal-api-swagger");
  ui.setCliPath("/nonexistent/ApiGenerator.Cli.dll");
  try {
    await run(ui);
    assert.match(await text(ui, "#statusPill"), /Başarısız/i);
    assert.match(await text(ui, "#errorText"), /CLI bulunamadı/);
  } finally {
    ui.setCliPath(original);
  }
});

async function main() {
  const only = process.argv[2];
  const ui = new ExtensionUiHarness();
  await ui.start();
  let failed = false;
  try {
    for (const { name, run: body } of scenarios) {
      if (only && !name.includes(only)) {
        continue;
      }
      ui.pageErrors.length = 0;
      const started = Date.now();
      try {
        await body(ui);
        await ui.waitIdle();
        assert.deepEqual(ui.pageErrors, [], "no script errors in the webview");
        console.log(`ok:   ${name} (${Date.now() - started} ms)`);
      } catch (error) {
        failed = true;
        console.log(`FAIL: ${name}\n${error.stack || error}`);
        await ui.screenshot(`FAILED-${name.replace(/[^a-z0-9]+/gi, "-")}`).catch(() => {});
        break;
      }
    }
  } finally {
    await ui.stop();
  }
  console.log(failed ? "UI E2E FAILED" : "UI E2E OK");
  process.exit(failed ? 1 : 0);
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
