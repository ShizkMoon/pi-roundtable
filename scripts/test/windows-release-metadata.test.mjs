import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { copyFile, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
const generator = path.join(repositoryRoot, "scripts", "generate-windows-release-metadata.mjs");
const verifier = path.join(repositoryRoot, "scripts", "verify-windows-release-candidate.mjs");

test("release metadata binds exact-run MSI and materials while stable remains an older baseline", async (context) => {
  const directory = await mkdtemp(path.join(os.tmpdir(), "pi-roundtable-release-metadata-"));
  context.after(() => rm(directory, { recursive: true, force: true }));
  const version = "0.4.0";
  const commit = "a".repeat(40);
  const prefix = `PiRoundtable-${version}-win-x64`;
  const msi = path.join(directory, `${prefix}.msi`);
  const metadata = path.join(directory, `${prefix}.release.json`);
  const inventory = path.join(directory, `${prefix}.dependencies.json`);
  const sbom = path.join(directory, `${prefix}.sbom.cdx.json`);
  const notices = path.join(directory, `${prefix}.third-party-notices.txt`);
  const stable = path.join(repositoryRoot, "packaging", "windows-x64", "update-manifest.json");
  await writeFile(msi, "candidate-msi-bytes");
  await writeFile(inventory, JSON.stringify({ schemaVersion: 1, productVersion: version, sourceCommit: commit, architecture: "x64" }));
  await writeFile(sbom, JSON.stringify({
    bomFormat: "CycloneDX",
    specVersion: "1.6",
    metadata: { component: { version, properties: [
      { name: "pi-roundtable:sourceCommit", value: commit },
      { name: "pi-roundtable:architecture", value: "x64" },
    ] } },
  }));
  await writeFile(notices, `Product version: ${version}\r\nSource commit: ${commit}\r\n`);
  run(generator, [
    "--version", version,
    "--source-commit", commit,
    "--msi", msi,
    "--output", metadata,
    "--authenticode-required", "false",
    "--product-name", "Pi Roundtable",
    "--upgrade-code", "{8F84BF2C-3DBB-4F28-8B97-78D8B384365A}",
    "--sbom", sbom,
    "--dependency-inventory", inventory,
    "--notices", notices,
  ]);
  assert.doesNotThrow(() => run(verifier, verificationArguments()));

  await writeFile(notices, `${await readFile(notices, "utf8")}tampered`);
  assert.throws(() => run(verifier, verificationArguments()), /release material does not match candidate metadata/);

  await writeFile(notices, `Product version: ${version}\r\nSource commit: ${commit}\r\n`);
  run(generator, [
    "--version", version,
    "--source-commit", commit,
    "--msi", msi,
    "--output", metadata,
    "--authenticode-required", "false",
    "--product-name", "Pi Roundtable",
    "--upgrade-code", "{8F84BF2C-3DBB-4F28-8B97-78D8B384365A}",
    "--sbom", sbom,
    "--dependency-inventory", inventory,
    "--notices", notices,
  ]);
  const tamperedStable = path.join(directory, "update-manifest.json");
  const stableDocument = JSON.parse(await readFile(stable, "utf8"));
  stableDocument.asset.size += 1;
  await writeFile(tamperedStable, `${JSON.stringify(stableDocument, null, 2)}\n`);
  await copyFile(
    path.join(repositoryRoot, "packaging", "windows-x64", "update-public-key.pem"),
    path.join(directory, "update-public-key.pem"),
  );
  assert.throws(() => run(verifier, verificationArguments(tamperedStable)), /signature is invalid/);

  function verificationArguments(stableManifest = stable) {
    return [
      "--metadata", metadata,
      "--msi", msi,
      "--materials-directory", directory,
      "--stable-manifest", stableManifest,
      "--version", version,
      "--source-commit", commit,
      "--release-tag", `v${version}`,
    ];
  }
});

function run(script, args) {
  return execFileSync(process.execPath, [script, ...args], { cwd: repositoryRoot, encoding: "utf8", stdio: "pipe" });
}
