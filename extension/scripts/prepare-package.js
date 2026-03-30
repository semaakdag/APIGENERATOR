const fs = require("node:fs");
const path = require("node:path");
const cp = require("node:child_process");

const extensionRoot = path.resolve(__dirname, "..");
const repoRoot = path.resolve(extensionRoot, "..");
const bundledRoot = path.join(extensionRoot, "bundled");
const cliOutput = path.join(bundledRoot, "cli");
const profilesSource = path.join(repoRoot, "profiles");
const profilesOutput = path.join(bundledRoot, "profiles");
const projectPath = path.join(repoRoot, "generator-engine", "ApiGenerator.Cli.csproj");

function run(command, args, cwd) {
  const result = cp.spawnSync(command, args, {
    cwd,
    stdio: "inherit",
    shell: false
  });

  if (result.status !== 0) {
    throw new Error(`${command} ${args.join(" ")} failed with exit code ${result.status ?? 1}.`);
  }
}

function recreateDirectory(directory) {
  try {
    fs.rmSync(directory, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
  } catch {
    if (fs.existsSync(directory)) {
      for (const entry of fs.readdirSync(directory)) {
        fs.rmSync(path.join(directory, entry), { recursive: true, force: true, maxRetries: 5, retryDelay: 200 });
      }
    }
  }

  fs.mkdirSync(directory, { recursive: true });
}

function copyDirectory(source, destination) {
  fs.mkdirSync(destination, { recursive: true });

  for (const entry of fs.readdirSync(source, { withFileTypes: true })) {
    const sourcePath = path.join(source, entry.name);
    const destinationPath = path.join(destination, entry.name);

    if (entry.isDirectory()) {
      copyDirectory(sourcePath, destinationPath);
      continue;
    }

    fs.copyFileSync(sourcePath, destinationPath);
  }
}

recreateDirectory(bundledRoot);
fs.mkdirSync(cliOutput, { recursive: true });

run(
  "dotnet",
  [
    "publish",
    projectPath,
    "-c",
    "Release",
    "-r",
    "win-x64",
    "--self-contained",
    "true",
    "-o",
    cliOutput,
    "/p:PublishSingleFile=false",
    "/p:PublishTrimmed=false",
    "/p:DebugType=None",
    "/p:DebugSymbols=false"
  ],
  repoRoot
);

copyDirectory(profilesSource, profilesOutput);

console.log(`Bundled CLI ready at ${cliOutput}`);
console.log(`Bundled profiles ready at ${profilesOutput}`);
