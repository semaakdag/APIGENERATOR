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
    for (const tab of ["config", "database", "runtime", "llm", "results", "output"]) {
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
  assert.match(await text(ui, "#statusPill"), /Başarısız/);
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
  assert.match(await text(ui, "#statusPill"), /Engellendi/);
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
