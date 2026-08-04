import { createHash } from "node:crypto";
import { readFile, stat, writeFile } from "node:fs/promises";
import path from "node:path";

const options = parseOptions(process.argv.slice(2));
const version = required("version");
const sourceCommit = required("source-commit");
const msiPath = path.resolve(required("msi"));
const outputPath = path.resolve(required("output"));
const authenticodeRequired = parseBoolean(required("authenticode-required"));
const productName = required("product-name");
const upgradeCode = required("upgrade-code").toUpperCase();
const sbomPath = path.resolve(required("sbom"));
const dependencyInventoryPath = path.resolve(required("dependency-inventory"));
const noticesPath = path.resolve(required("notices"));

assertVersion(version);
if (!/^[0-9a-f]{40}$/.test(sourceCommit)) {
  throw new Error("--source-commit must be one lowercase 40-hex Git commit.");
}
if (productName !== "Pi Roundtable" || upgradeCode !== "{8F84BF2C-3DBB-4F28-8B97-78D8B384365A}") {
  throw new Error("Release metadata requires the production ProductName and UpgradeCode.");
}

const expectedFileName = `PiRoundtable-${version}-win-x64.msi`;
if (path.basename(msiPath) !== expectedFileName) {
  throw new Error(`MSI file name must be ${expectedFileName}.`);
}
const bytes = await readFile(msiPath);
const file = await stat(msiPath);
const metadata = {
  schemaVersion: 1,
  productVersion: version,
  sourceCommit,
  architecture: "x64",
  productName,
  upgradeCode,
  fileName: expectedFileName,
  size: file.size,
  sha256: createHash("sha256").update(bytes).digest("hex").toUpperCase(),
  authenticodeRequired,
  materials: {
    dependencyInventory: await describeMaterial(dependencyInventoryPath),
    sbom: await describeMaterial(sbomPath),
    thirdPartyNotices: await describeMaterial(noticesPath),
  },
};
await writeFile(outputPath, `${JSON.stringify(metadata, null, 2)}\n`, "utf8");
process.stdout.write(`Windows release metadata: ${outputPath}\n`);

function parseOptions(args) {
  if (args.length === 0 || args.length % 2 !== 0) {
    throw new Error("Expected --version, --source-commit, --msi, --output, and --authenticode-required arguments.");
  }
  const parsed = new Map();
  for (let index = 0; index < args.length; index += 2) {
    const name = args[index];
    if (!name?.startsWith("--") || parsed.has(name.slice(2))) throw new Error(`Invalid or duplicate option: ${name}`);
    parsed.set(name.slice(2), args[index + 1]);
  }
  return parsed;
}

function required(name) {
  const value = options.get(name);
  if (value === undefined || value.length === 0) throw new Error(`--${name} is required.`);
  return value;
}

function parseBoolean(value) {
  if (value === "true") return true;
  if (value === "false") return false;
  throw new Error("--authenticode-required must be true or false.");
}

function assertVersion(value) {
  const match = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.exec(value);
  if (!match) throw new Error("Version must be a three-part numeric product version without leading zeroes.");
  const parts = match.slice(1).map(Number);
  if (parts[0] > 255 || parts[1] > 255 || parts[2] > 65535) {
    throw new Error("Version exceeds Windows Installer limits.");
  }
}

async function describeMaterial(filePath) {
  const material = await stat(filePath);
  if (!material.isFile() || material.size <= 0) throw new Error(`Release material is missing or empty: ${filePath}`);
  return {
    fileName: path.basename(filePath),
    size: material.size,
    sha256: createHash("sha256").update(await readFile(filePath)).digest("hex").toUpperCase(),
  };
}
