import { createHash } from "node:crypto";
import { execFileSync } from "node:child_process";
import { mkdir, readFile, stat, writeFile } from "node:fs/promises";
import path from "node:path";

const options = parseOptions(process.argv.slice(2));
const version = required("version");
const sourceCommit = required("source-commit");
const runtimeRoot = path.resolve(required("runtime-root"));
const nugetAssetsPath = path.resolve(required("nuget-assets"));
const nugetLockPath = path.resolve(required("nuget-lock"));
const globalJsonPath = path.resolve(required("global-json"));
const wixProjectPath = path.resolve(required("wix-project"));
const outputDirectory = path.resolve(required("output-directory"));
const packagedNoticesOutput = path.resolve(required("packaged-notices-output"));
const packagedLicenseOutput = path.resolve(required("packaged-license-output"));
const repositoryLicense = path.resolve(required("repository-license"));

assertVersion(version);
if (!/^[0-9a-f]{40}$/.test(sourceCommit)) throw new Error("--source-commit must be one lowercase 40-hex Git commit.");

const runtimeLockPath = path.join(runtimeRoot, "package-lock.json");
const runtimeLock = await readJson(runtimeLockPath, "Windows Runtime npm lock");
if (runtimeLock.version !== version || runtimeLock.packages?.[""]?.version !== version) {
  throw new Error("Windows Runtime npm lock is not bound to the candidate version.");
}
const nugetAssets = await readJson(nugetAssetsPath, "Windows NuGet assets");
const nugetLock = await readJson(nugetLockPath, "Windows NuGet lock");
const globalJson = await readJson(globalJsonPath, "global.json");
const wixProject = await readFile(wixProjectPath, "utf8");
const wixVersion = /<Project\s+Sdk=["']WixToolset\.Sdk\/([^"']+)["']/.exec(wixProject)?.[1];
if (typeof globalJson.sdk?.version !== "string" || wixVersion === undefined) {
  throw new Error("Unable to resolve declared .NET or WiX tool versions.");
}
const actualDotnetVersion = runVersion("dotnet", ["--version"]);
const actualNpmVersion = process.platform === "win32"
  ? runVersion("cmd.exe", ["/d", "/s", "/c", "npm --version"])
  : runVersion("npm", ["--version"]);

const npmComponents = await collectNpmComponents(runtimeLock);
const nugetComponents = await collectNugetComponents(nugetAssets, nugetLock);
const nodeComponent = {
  ecosystem: "runtime",
  name: "Node.js",
  version: process.versions.node,
  license: "Node.js license bundle",
  source: "https://github.com/nodejs/node",
  purl: `pkg:generic/node@${process.versions.node}`,
};
const components = [nodeComponent, ...npmComponents, ...nugetComponents].sort(compareComponents);

await mkdir(outputDirectory, { recursive: true });
await mkdir(path.dirname(packagedNoticesOutput), { recursive: true });
await mkdir(path.dirname(packagedLicenseOutput), { recursive: true });

const lockHashes = {
  npmPackageLockSha256: await sha256File(runtimeLockPath),
  nugetPackagesLockSha256: await sha256File(nugetLockPath),
};
const dependencyInventory = {
  schemaVersion: 1,
  productVersion: version,
  sourceCommit,
  architecture: "x64",
  nodeRuntimeVersion: process.versions.node,
  locks: lockHashes,
  shippedComponents: components,
  buildTools: [
    { name: "Node.js", version: process.versions.node, source: "build process" },
    { name: "npm", version: actualNpmVersion, source: "build process" },
    { name: ".NET SDK", version: actualDotnetVersion, requestedVersion: globalJson.sdk.version, source: "global.json and build process" },
    { name: "WiX Toolset SDK", version: wixVersion, source: "packaging/windows-x64/PiRoundtable.Installer.wixproj" },
  ],
};
const inventoryName = `PiRoundtable-${version}-win-x64.dependencies.json`;
const sbomName = `PiRoundtable-${version}-win-x64.sbom.cdx.json`;
const noticesName = `PiRoundtable-${version}-win-x64.third-party-notices.txt`;
const inventoryPath = path.join(outputDirectory, inventoryName);
const sbomPath = path.join(outputDirectory, sbomName);
const noticesPath = path.join(outputDirectory, noticesName);

await writeJson(inventoryPath, dependencyInventory);
await writeJson(sbomPath, {
  bomFormat: "CycloneDX",
  specVersion: "1.6",
  version: 1,
  metadata: {
    component: {
      type: "application",
      name: "Pi Roundtable for Windows",
      version,
      properties: [
        { name: "pi-roundtable:sourceCommit", value: sourceCommit },
        { name: "pi-roundtable:architecture", value: "x64" },
      ],
    },
  },
  components: components.map(toCycloneDxComponent),
});

const notices = [
  "Pi Roundtable Windows x64 third-party notices",
  `Product version: ${version}`,
  `Source commit: ${sourceCommit}`,
  "",
  "This inventory records software shipped in the Windows x64 package. Package-specific license files remain in the staged npm dependency tree where supplied. The bundled Node.js license is installed as runtime/LICENSE.node.txt.",
  "",
  ...components.flatMap((component) => [
    `[${component.ecosystem}] ${component.name}@${component.version}`,
    `License: ${component.license}`,
    `Source: ${component.source || "not declared"}`,
    "",
  ]),
].join("\r\n");
await writeFile(noticesPath, notices, "utf8");
await writeFile(packagedNoticesOutput, notices, "utf8");
await writeFile(packagedLicenseOutput, await readFile(repositoryLicense));

process.stdout.write(`Dependency inventory: ${inventoryPath}\nSBOM: ${sbomPath}\nThird-party notices: ${noticesPath}\n`);

async function collectNpmComponents(lock) {
  const result = [];
  for (const [lockPath, entry] of Object.entries(lock.packages ?? {})) {
    if (!lockPath.startsWith("node_modules/") || entry.link === true) continue;
    const packagePath = path.join(runtimeRoot, ...lockPath.split("/"));
    const packageJsonPath = path.join(packagePath, "package.json");
    let packageJson;
    try {
      packageJson = await readJson(packageJsonPath, `npm package ${lockPath}`);
    } catch (error) {
      if (error.cause?.code === "ENOENT" && entry.optional === true) continue;
      throw error;
    }
    const name = packageJson.name;
    const packageVersion = entry.version ?? packageJson.version;
    const license = normalizeLicense(entry.license ?? packageJson.license);
    if (typeof name !== "string" || typeof packageVersion !== "string" || license === "") {
      throw new Error(`npm package ${lockPath} lacks name, version, or license metadata.`);
    }
    result.push({
      ecosystem: "npm",
      name,
      version: packageVersion,
      license,
      source: entry.resolved ?? normalizeRepository(packageJson.repository),
      purl: npmPurl(name, packageVersion),
      integrity: parseIntegrity(entry.integrity, `npm package ${name}@${packageVersion}`),
    });
  }
  return result;
}

async function collectNugetComponents(assets, lock) {
  const result = [];
  const packageFolders = Object.keys(assets.packageFolders ?? {});
  if (packageFolders.length === 0) throw new Error("NuGet assets contain no package folders.");
  const lockedPackages = new Map();
  for (const dependencies of Object.values(lock.dependencies ?? {})) {
    for (const [name, entry] of Object.entries(dependencies)) {
      if (typeof entry.resolved === "string") {
        lockedPackages.set(
          `${name.toLowerCase()}/${entry.resolved}`,
          decodeBase64Digest("SHA-512", entry.contentHash, `NuGet package ${name}@${entry.resolved}`),
        );
      }
    }
  }
  for (const [key, entry] of Object.entries(assets.libraries ?? {})) {
    if (entry.type !== "package") continue;
    const separator = key.lastIndexOf("/");
    const name = key.slice(0, separator);
    const packageVersion = key.slice(separator + 1);
    const packageKey = `${name.toLowerCase()}/${packageVersion}`;
    if (!lockedPackages.has(packageKey)) throw new Error(`NuGet package ${key} is absent from packages.lock.json.`);
    let packagePath;
    for (const folder of packageFolders) {
      const candidate = path.join(folder, entry.path ?? packageKey);
      try {
        if ((await stat(candidate)).isDirectory()) { packagePath = candidate; break; }
      } catch (error) {
        if (error.code !== "ENOENT") throw error;
      }
    }
    if (packagePath === undefined) throw new Error(`NuGet package cache entry is missing for ${key}.`);
    const nuspecPath = path.join(packagePath, `${name.toLowerCase()}.nuspec`);
    const nuspec = await readFile(nuspecPath, "utf8");
    const license = parseNugetLicense(nuspec);
    if (license === "") throw new Error(`NuGet package ${key} lacks license metadata.`);
    result.push({
      ecosystem: "nuget",
      name,
      version: packageVersion,
      license,
      source: parseXmlValue(nuspec, "repository", "url") || parseXmlElement(nuspec, "projectUrl") || parseXmlElement(nuspec, "licenseUrl"),
      purl: `pkg:nuget/${encodeURIComponent(name)}@${encodeURIComponent(packageVersion)}`,
      integrity: lockedPackages.get(packageKey),
    });
  }
  return result;
}

function toCycloneDxComponent(component) {
  const value = {
    type: "library",
    "bom-ref": component.purl,
    name: component.name,
    version: component.version,
    scope: "required",
    licenses: [{ license: { name: component.license } }],
    purl: component.purl,
    properties: [{ name: "pi-roundtable:ecosystem", value: component.ecosystem }],
  };
  if (component.integrity?.content) value.hashes = [{ alg: component.integrity.algorithm, content: component.integrity.content }];
  if (component.source) value.externalReferences = [{ type: "distribution", url: component.source }];
  return value;
}

function parseIntegrity(value, label) {
  if (typeof value !== "string") throw new Error(`${label} lacks registry integrity metadata.`);
  const match = /^(sha512|sha256)-(.+)$/.exec(value);
  if (!match) throw new Error(`${label} has unsupported registry integrity metadata.`);
  return decodeBase64Digest(match[1].toUpperCase().replace("SHA", "SHA-"), match[2], label);
}

function decodeBase64Digest(algorithm, value, label) {
  if (typeof value !== "string" || value.length === 0) throw new Error(`${label} lacks a content hash.`);
  const bytes = Buffer.from(value, "base64");
  const expectedLength = algorithm === "SHA-512" ? 64 : algorithm === "SHA-256" ? 32 : 0;
  if (expectedLength === 0 || bytes.length !== expectedLength || bytes.toString("base64") !== value) {
    throw new Error(`${label} has malformed ${algorithm} content hash data.`);
  }
  return { algorithm, content: bytes.toString("hex").toUpperCase() };
}

function parseNugetLicense(nuspec) {
  const match = /<license\s+type=["']([^"']+)["'][^>]*>([^<]+)<\/license>/i.exec(nuspec);
  if (match) return `${decodeXml(match[1])}:${decodeXml(match[2]).trim()}`;
  const url = parseXmlElement(nuspec, "licenseUrl");
  return url === "" ? "" : `url:${url}`;
}

function parseXmlElement(xml, name) {
  return decodeXml(new RegExp(`<${name}[^>]*>([^<]+)</${name}>`, "i").exec(xml)?.[1] ?? "").trim();
}

function parseXmlValue(xml, element, attribute) {
  return decodeXml(new RegExp(`<${element}[^>]*\\s${attribute}=["']([^"']+)["'][^>]*/?>`, "i").exec(xml)?.[1] ?? "").trim();
}

function decodeXml(value) {
  return value.replaceAll("&amp;", "&").replaceAll("&quot;", '"').replaceAll("&apos;", "'").replaceAll("&lt;", "<").replaceAll("&gt;", ">");
}

function normalizeLicense(value) {
  if (typeof value === "string") return value.trim();
  if (value && typeof value.type === "string") return value.type.trim();
  return "";
}

function normalizeRepository(value) {
  if (typeof value === "string") return value;
  return typeof value?.url === "string" ? value.url.replace(/^git\+/, "").replace(/\.git$/, "") : "";
}

function npmPurl(name, packageVersion) {
  const encoded = name.startsWith("@") ? `%40${name.slice(1)}` : name;
  return `pkg:npm/${encoded}@${encodeURIComponent(packageVersion)}`;
}

function compareComponents(left, right) {
  return `${left.ecosystem}\0${left.name}\0${left.version}`.localeCompare(`${right.ecosystem}\0${right.name}\0${right.version}`);
}

function parseOptions(args) {
  if (args.length === 0 || args.length % 2 !== 0) throw new Error("Release inventory arguments are incomplete.");
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

function assertVersion(value) {
  const match = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.exec(value);
  if (!match) throw new Error("Version must be a three-part numeric product version.");
  const parts = match.slice(1).map(Number);
  if (parts[0] > 255 || parts[1] > 255 || parts[2] > 65535) throw new Error("Version exceeds Windows Installer limits.");
}

async function readJson(filePath, label) {
  try { return JSON.parse(await readFile(filePath, "utf8")); }
  catch (error) { throw new Error(`${label} is not valid JSON: ${filePath}`, { cause: error }); }
}

async function sha256File(filePath) {
  return createHash("sha256").update(await readFile(filePath)).digest("hex").toUpperCase();
}

async function writeJson(filePath, value) {
  await writeFile(filePath, `${JSON.stringify(value, null, 2)}\n`, "utf8");
}

function runVersion(command, args) {
  const value = execFileSync(command, args, { encoding: "utf8", windowsHide: true }).trim();
  if (!/^\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?$/.test(value)) {
    throw new Error(`Unable to resolve an exact version from ${command}.`);
  }
  return value;
}
