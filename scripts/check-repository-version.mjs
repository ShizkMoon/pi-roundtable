import { access, readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const version = (await readText("VERSION")).trim();
const numericVersion = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;
const failures = [];

const versionMatch = numericVersion.exec(version);
if (!versionMatch) {
  failures.push("VERSION must contain one three-part numeric product version without leading zeroes.");
} else {
  assertWindowsInstallerRange("VERSION", versionMatch.slice(1).map(Number));
}

if (process.env.PI_ROUNDTABLE_VERSION_TRANSACTION !== "1" && await exists(".version-update-journal.json")) {
  failures.push("An interrupted version update journal is present; run version:set to recover it before building.");
}

const rootPackage = await readJson("package.json");
const rootLock = await readJson("package-lock.json");
const protocolPackage = await readJson("packages/protocol-ts/package.json");
const runtimePackage = await readJson("packages/runtime-host/package.json");
const syncPackage = await readJson("packages/sync-server/package.json");
const packagedProtocol = await readJson("packaging/windows-runtime/protocol/package.json");
const packagedRuntimeLock = await readJson("packaging/windows-runtime/package-lock.json");

expect("package.json#/version", rootPackage.version, version);
expect("package-lock.json#/version", rootLock.version, version);
expect("package-lock.json#/packages//version", rootLock.packages?.[""]?.version, version);
expect("packages/protocol-ts/package.json#/version", protocolPackage.version, version);
expect("packages/runtime-host/package.json#/version", runtimePackage.version, version);
expect("packages/runtime-host/package.json#/dependencies/@pi-roundtable~1protocol", runtimePackage.dependencies?.["@pi-roundtable/protocol"], version);
expect("packages/sync-server/package.json#/version", syncPackage.version, version);
expect("packages/sync-server/package.json#/dependencies/@pi-roundtable~1protocol", syncPackage.dependencies?.["@pi-roundtable/protocol"], version);
expect("package-lock.json#/packages/packages~1protocol-ts/version", rootLock.packages?.["packages/protocol-ts"]?.version, version);
expect("package-lock.json#/packages/packages~1runtime-host/version", rootLock.packages?.["packages/runtime-host"]?.version, version);
expect("package-lock.json#/packages/packages~1runtime-host/dependencies/@pi-roundtable~1protocol", rootLock.packages?.["packages/runtime-host"]?.dependencies?.["@pi-roundtable/protocol"], version);
expect("package-lock.json#/packages/packages~1sync-server/version", rootLock.packages?.["packages/sync-server"]?.version, version);
expect("package-lock.json#/packages/packages~1sync-server/dependencies/@pi-roundtable~1protocol", rootLock.packages?.["packages/sync-server"]?.dependencies?.["@pi-roundtable/protocol"], version);
expect("packaging/windows-runtime/protocol/package.json#/version", packagedProtocol.version, version);
expect("packaging/windows-runtime/package-lock.json#/packages/protocol/version", packagedRuntimeLock.packages?.protocol?.version, version);

const cmake = await readText("CMakeLists.txt");
expectContains("CMakeLists.txt", cmake, 'file(STRINGS "${CMAKE_CURRENT_SOURCE_DIR}/VERSION" PI_ROUNDTABLE_VERSION LIMIT_COUNT 1)');
expectContains("CMakeLists.txt", cmake, "VERSION ${PI_ROUNDTABLE_VERSION}");

const msbuild = await readText("Directory.Build.props");
expectContains("Directory.Build.props", msbuild, "$(MSBuildThisFileDirectory)VERSION");
expectContains("Directory.Build.props", msbuild, "System.IO.File]::ReadAllText");

const wix = await readText("packaging/windows-x64/PiRoundtable.Installer.wixproj");
expectContains("PiRoundtable.Installer.wixproj", wix, '<ProductVersion Condition="\'$(ProductVersion)\' == \'\'">$(Version)</ProductVersion>');

const windowsProject = await readText("apps/windows/PiRoundtable.Windows/PiRoundtable.Windows.csproj");
if (/<Version(?:\s[^>]*)?>/.test(windowsProject)) {
  failures.push("PiRoundtable.Windows.csproj must inherit the product version from Directory.Build.props.");
}

for (const script of [
  "scripts/build-windows-x64.ps1",
  "scripts/build-signed-windows-x64.ps1",
  "scripts/test-windows-msi-lifecycle.ps1",
]) {
  const source = await readText(script);
  expectContains(script, source, "Join-Path $PSScriptRoot '..\\VERSION'");
}

const signingSmoke = await readText("scripts/test-windows-signing-pipeline.ps1");
expectContains("scripts/test-windows-signing-pipeline.ps1", signingSmoke, "Join-Path $repoRoot 'VERSION'");

const runtimeIdentity = await readText("packages/runtime-host/src/mcp-client-manager.ts");
const runtimeVersionMatches = [...runtimeIdentity.matchAll(/name: "pi-roundtable-runtime-host", version: "([^"]+)"/g)];
if (runtimeVersionMatches.length !== 1) {
  failures.push("mcp-client-manager.ts must contain exactly one Pi Roundtable runtime identity version.");
} else {
  expect("mcp-client-manager.ts runtime identity", runtimeVersionMatches[0][1], version);
}

const ci = await readText(".github/workflows/ci.yml");
expectContains(".github/workflows/ci.yml", ci, "id: product-version");
expectContains(".github/workflows/ci.yml", ci, "steps.product-version.outputs.version");

const promotion = await readText(".github/workflows/promote-windows-release.yml");
const releaseTagBlock = promotion.match(/release_tag:\s*\n(?<block>(?:\s{8}.+\n)+)/)?.groups?.block ?? "";
if (/\bdefault:/.test(releaseTagBlock)) {
  failures.push("promote-windows-release.yml release_tag must not default to a stale product version.");
}

const stableManifest = await readJson("packaging/windows-x64/update-manifest.json");
const stableVersionMatch = numericVersion.exec(stableManifest.version ?? "");
if (!stableVersionMatch) {
  failures.push("The signed stable update manifest must contain a numeric version.");
} else {
  assertWindowsInstallerRange("The signed stable update manifest", stableVersionMatch.slice(1).map(Number));
  const expectedFileName = `PiRoundtable-${stableManifest.version}-win-${stableManifest.architecture}.msi`;
  expect("update-manifest.json#/asset/fileName", stableManifest.asset?.fileName, expectedFileName);
  const expectedUrlSuffix = `/releases/download/v${stableManifest.version}/${expectedFileName}`;
  if (typeof stableManifest.asset?.url !== "string" || !stableManifest.asset.url.endsWith(expectedUrlSuffix)) {
    failures.push(`update-manifest.json asset URL must end with ${expectedUrlSuffix}.`);
  }
}

if (failures.length > 0) {
  for (const failure of failures) {
    process.stderr.write(`- ${failure}\n`);
  }
  throw new Error(`Repository version contract failed with ${failures.length} error(s).`);
}

process.stdout.write(
  `Verified current build version ${version}; signed stable manifest remains independently bound to ${stableManifest.version}.\n`,
);

function expect(location, actual, expected) {
  if (actual !== expected) {
    failures.push(`${location} is ${JSON.stringify(actual)}; expected ${JSON.stringify(expected)}.`);
  }
}

function expectContains(file, source, expected) {
  if (!source.includes(expected)) {
    failures.push(`${file} is missing required version binding: ${expected}`);
  }
}

function assertWindowsInstallerRange(label, parts) {
  if (parts[0] > 255 || parts[1] > 255 || parts[2] > 65535) {
    failures.push(`${label} exceeds Windows Installer limits (major/minor <= 255 and patch <= 65535).`);
  }
}

async function exists(relativePath) {
  try {
    await access(path.join(repositoryRoot, relativePath));
    return true;
  } catch (error) {
    if (error.code === "ENOENT") {
      return false;
    }
    throw error;
  }
}

async function readText(relativePath) {
  return readFile(path.join(repositoryRoot, relativePath), "utf8");
}

async function readJson(relativePath) {
  try {
    return JSON.parse(await readText(relativePath));
  } catch (error) {
    throw new Error(`${relativePath} is not valid JSON: ${error.message}`, { cause: error });
  }
}
