import { createHash } from "node:crypto";
import { verify } from "node:crypto";
import { readFile, stat } from "node:fs/promises";
import path from "node:path";

const options = parseOptions(process.argv.slice(2));
const metadataPath = path.resolve(required("metadata"));
const msiPath = path.resolve(required("msi"));
const materialsDirectory = path.resolve(required("materials-directory"));
const stableManifestPath = path.resolve(required("stable-manifest"));
const version = required("version");
const sourceCommit = required("source-commit");
const releaseTag = required("release-tag");

const candidateParts = parseVersion(version, "candidate version");
if (!/^[0-9a-f]{40}$/.test(sourceCommit)) throw new Error("Source commit must be one lowercase 40-hex Git commit.");
if (releaseTag !== `v${version}`) throw new Error(`Release tag must be exactly v${version}.`);

const metadata = await readJson(metadataPath, "candidate metadata");
assertExactKeys(metadata, [
  "schemaVersion", "productVersion", "sourceCommit", "architecture", "productName", "upgradeCode", "fileName", "size", "sha256",
  "authenticodeRequired",
  "materials",
], "candidate metadata");
const expectedFileName = `PiRoundtable-${version}-win-x64.msi`;
if (metadata.schemaVersion !== 1 || metadata.productVersion !== version || metadata.sourceCommit !== sourceCommit ||
    metadata.architecture !== "x64" || metadata.fileName !== expectedFileName ||
    metadata.productName !== "Pi Roundtable" ||
    metadata.upgradeCode !== "{8F84BF2C-3DBB-4F28-8B97-78D8B384365A}" ||
    !Number.isSafeInteger(metadata.size) || metadata.size <= 0 ||
    typeof metadata.sha256 !== "string" || !/^[0-9A-F]{64}$/.test(metadata.sha256) ||
    typeof metadata.authenticodeRequired !== "boolean" || metadata.materials === null ||
    typeof metadata.materials !== "object" || Array.isArray(metadata.materials)) {
  throw new Error("Candidate metadata is malformed or is not bound to the requested version and source commit.");
}
assertExactKeys(metadata.materials, ["dependencyInventory", "sbom", "thirdPartyNotices"], "candidate materials");
const expectedMaterials = {
  dependencyInventory: `PiRoundtable-${version}-win-x64.dependencies.json`,
  sbom: `PiRoundtable-${version}-win-x64.sbom.cdx.json`,
  thirdPartyNotices: `PiRoundtable-${version}-win-x64.third-party-notices.txt`,
};
for (const [role, fileName] of Object.entries(expectedMaterials)) {
  const material = metadata.materials[role];
  assertExactKeys(material, ["fileName", "size", "sha256"], `${role} material`);
  if (material.fileName !== fileName || !Number.isSafeInteger(material.size) || material.size <= 0 ||
      typeof material.sha256 !== "string" || !/^[0-9A-F]{64}$/.test(material.sha256)) {
    throw new Error(`${role} release material metadata is malformed.`);
  }
  const materialPath = path.join(materialsDirectory, fileName);
  const materialFile = await stat(materialPath);
  const materialBytes = await readFile(materialPath);
  const materialSha256 = createHash("sha256").update(materialBytes).digest("hex").toUpperCase();
  if (materialFile.size !== material.size || materialSha256 !== material.sha256) {
    throw new Error(`${role} release material does not match candidate metadata.`);
  }
  if (role === "dependencyInventory") {
    const inventory = JSON.parse(materialBytes.toString("utf8"));
    if (inventory.schemaVersion !== 1 || inventory.productVersion !== version ||
        inventory.sourceCommit !== sourceCommit || inventory.architecture !== "x64") {
      throw new Error("Dependency inventory is not bound to the candidate identity.");
    }
  } else if (role === "sbom") {
    const sbom = JSON.parse(materialBytes.toString("utf8"));
    const properties = new Map((sbom.metadata?.component?.properties ?? []).map((entry) => [entry.name, entry.value]));
    if (sbom.bomFormat !== "CycloneDX" || sbom.specVersion !== "1.6" ||
        sbom.metadata?.component?.version !== version ||
        properties.get("pi-roundtable:sourceCommit") !== sourceCommit ||
        properties.get("pi-roundtable:architecture") !== "x64") {
      throw new Error("SBOM is not bound to the candidate identity.");
    }
  } else {
    const notices = materialBytes.toString("utf8");
    if (!notices.includes(`Product version: ${version}\r\n`) ||
        !notices.includes(`Source commit: ${sourceCommit}\r\n`)) {
      throw new Error("Third-party notices are not bound to the candidate identity.");
    }
  }
}
if (path.basename(msiPath) !== expectedFileName) throw new Error(`Candidate MSI must be named ${expectedFileName}.`);
const msi = await stat(msiPath);
const sha256 = createHash("sha256").update(await readFile(msiPath)).digest("hex").toUpperCase();
if (msi.size !== metadata.size || sha256 !== metadata.sha256) {
  throw new Error("Candidate MSI size or SHA-256 does not match its run-bound metadata.");
}

const stableRaw = await readFile(stableManifestPath, "utf8");
const stable = await readJson(stableManifestPath, "stable update manifest");
if (stableRaw !== `${JSON.stringify(stable, null, 2)}\n`) {
  throw new Error("Stable update manifest must use canonical JSON formatting without duplicate properties.");
}
assertExactKeys(stable, ["manifestVersion", "productId", "channel", "architecture", "version", "publishedAt", "asset", "signature"], "stable update manifest");
assertExactKeys(stable.asset, ["url", "fileName", "size", "sha256", "authenticodeRequired"], "stable manifest asset");
assertExactKeys(stable.signature, ["algorithm", "keyId", "value"], "stable manifest signature");
const stableParts = parseVersion(stable.version, "stable manifest version");
const stableFileName = `PiRoundtable-${stable.version}-win-x64.msi`;
if (stable.manifestVersion !== 1 || stable.productId !== "PiRoundtable.Windows" ||
    stable.channel !== "stable" || stable.architecture !== "x64" ||
    stable.asset.fileName !== stableFileName || !Number.isSafeInteger(stable.asset.size) || stable.asset.size <= 0 ||
    typeof stable.asset.sha256 !== "string" || !/^[0-9A-F]{64}$/.test(stable.asset.sha256) ||
    typeof stable.asset.authenticodeRequired !== "boolean" ||
    stable.asset.url !== `https://github.com/ShizkMoon/pi-roundtable/releases/download/v${stable.version}/${stableFileName}` ||
    stable.signature.algorithm !== "ECDSA_P256_SHA256" || stable.signature.keyId !== "stable-2026-08" ||
    !Number.isFinite(Date.parse(stable.publishedAt))) {
  throw new Error("Stable update manifest is not a valid x64 stable baseline.");
}
const publicKeyPath = path.join(path.dirname(stableManifestPath), "update-public-key.pem");
const signature = Buffer.from(stable.signature.value, "base64");
if (signature.length !== 64 || !verify(
  "sha256",
  Buffer.from(canonicalizeStableManifest(stable), "utf8"),
  { key: await readFile(publicKeyPath, "utf8"), dsaEncoding: "ieee-p1363" },
  signature,
)) {
  throw new Error("Stable update manifest signature is invalid.");
}
if (compareVersions(stableParts, candidateParts) >= 0) {
  throw new Error("Candidate version must be newer than the independently signed stable baseline.");
}

process.stdout.write(`${JSON.stringify({
  verified: true,
  productVersion: version,
  sourceCommit,
  fileName: metadata.fileName,
  size: metadata.size,
  sha256: metadata.sha256,
  authenticodeRequired: metadata.authenticodeRequired,
  stableVersion: stable.version,
})}\n`);

function parseOptions(args) {
  if (args.length === 0 || args.length % 2 !== 0) throw new Error("Candidate verification arguments are incomplete.");
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

function parseVersion(value, label) {
  const match = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.exec(value ?? "");
  if (!match) throw new Error(`${label} must be a three-part numeric version.`);
  const parts = match.slice(1).map(Number);
  if (parts[0] > 255 || parts[1] > 255 || parts[2] > 65535) throw new Error(`${label} exceeds Windows Installer limits.`);
  return parts;
}

function compareVersions(left, right) {
  for (let index = 0; index < 3; index += 1) {
    if (left[index] !== right[index]) return left[index] - right[index];
  }
  return 0;
}

async function readJson(filePath, label) {
  try {
    return JSON.parse(await readFile(filePath, "utf8"));
  } catch (error) {
    throw new Error(`${label} is not valid JSON.`, { cause: error });
  }
}

function assertExactKeys(value, expected, label) {
  if (value === null || typeof value !== "object" || Array.isArray(value)) throw new Error(`${label} must be an object.`);
  const actual = Object.keys(value).sort();
  const wanted = [...expected].sort();
  if (actual.length !== wanted.length || actual.some((key, index) => key !== wanted[index])) {
    throw new Error(`${label} has missing or unknown fields.`);
  }
}

function canonicalizeStableManifest(manifest) {
  return [
    `manifestVersion=${manifest.manifestVersion}`,
    `productId=${manifest.productId}`,
    `channel=${manifest.channel}`,
    `architecture=${manifest.architecture}`,
    `version=${manifest.version}`,
    `publishedAt=${manifest.publishedAt}`,
    `asset.url=${manifest.asset.url}`,
    `asset.fileName=${manifest.asset.fileName}`,
    `asset.size=${manifest.asset.size}`,
    `asset.sha256=${manifest.asset.sha256.toUpperCase()}`,
    `asset.authenticodeRequired=${manifest.asset.authenticodeRequired.toString().toLowerCase()}`,
    `signature.algorithm=${manifest.signature.algorithm}`,
    `signature.keyId=${manifest.signature.keyId}`,
    "",
  ].join("\n");
}
