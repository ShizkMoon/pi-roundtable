import { readFile, rename, unlink, writeFile } from "node:fs/promises";
import { spawnSync } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const lockPath = path.join(repositoryRoot, ".version-update.lock");
const journalPath = path.join(repositoryRoot, ".version-update-journal.json");
const nextVersion = process.argv[2] ?? "";
const trackedPaths = Object.freeze([
  "VERSION",
  "package.json",
  "package-lock.json",
  "packages/protocol-ts/package.json",
  "packages/runtime-host/package.json",
  "packages/sync-server/package.json",
  "packaging/windows-runtime/package.json",
  "packaging/windows-runtime/protocol/package.json",
  "packaging/windows-runtime/package-lock.json",
  "packages/runtime-host/src/mcp-client-manager.ts",
]);
const trackedPathSet = new Set(trackedPaths);
const maximumJournalContentBytes = 16 * 1024 * 1024;

await acquireLock();
try {
  await recoverInterruptedTransaction();
  const match = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.exec(nextVersion);
  if (!match) {
    throw new Error("Usage: node scripts/set-repository-version.mjs <major.minor.patch>");
  }
  assertWindowsInstallerRange(match.slice(1).map(Number));

  const originals = new Map();
  for (const relativePath of trackedPaths) {
    originals.set(relativePath, await readText(relativePath));
  }

  // Validate every structure and compute every output before the first target
  // file changes. The journal below then makes an interrupted multi-file update
  // recoverable even though filesystems do not provide a cross-file rename.
  const rootPackage = parseJson(originals, "package.json");
  const rootLock = parseJson(originals, "package-lock.json");
  const protocolPackage = parseJson(originals, "packages/protocol-ts/package.json");
  const runtimePackage = parseJson(originals, "packages/runtime-host/package.json");
  const syncPackage = parseJson(originals, "packages/sync-server/package.json");
  const packagedRuntimePackage = parseJson(originals, "packaging/windows-runtime/package.json");
  const packagedProtocol = parseJson(originals, "packaging/windows-runtime/protocol/package.json");
  const packagedRuntimeLock = parseJson(originals, "packaging/windows-runtime/package-lock.json");

  requireRecord(rootLock.packages?.[""], "package-lock.json packages['']");
  requireRecord(rootLock.packages?.["packages/protocol-ts"], "package-lock protocol workspace");
  requireRecord(rootLock.packages?.["packages/runtime-host"]?.dependencies, "package-lock runtime dependencies");
  requireRecord(rootLock.packages?.["packages/sync-server"]?.dependencies, "package-lock sync dependencies");
  requireRecord(runtimePackage.dependencies, "runtime-host dependencies");
  requireRecord(syncPackage.dependencies, "sync-server dependencies");
  requireRecord(packagedRuntimeLock.packages?.[""], "packaged runtime root lock entry");
  requireRecord(packagedRuntimeLock.packages?.protocol, "packaged runtime protocol lock entry");

  rootPackage.version = nextVersion;
  rootLock.version = nextVersion;
  rootLock.packages[""].version = nextVersion;
  protocolPackage.version = nextVersion;
  runtimePackage.version = nextVersion;
  runtimePackage.dependencies["@pi-roundtable/protocol"] = nextVersion;
  syncPackage.version = nextVersion;
  syncPackage.dependencies["@pi-roundtable/protocol"] = nextVersion;
  rootLock.packages["packages/protocol-ts"].version = nextVersion;
  rootLock.packages["packages/runtime-host"].version = nextVersion;
  rootLock.packages["packages/runtime-host"].dependencies["@pi-roundtable/protocol"] = nextVersion;
  rootLock.packages["packages/sync-server"].version = nextVersion;
  rootLock.packages["packages/sync-server"].dependencies["@pi-roundtable/protocol"] = nextVersion;
  packagedRuntimePackage.version = nextVersion;
  packagedRuntimeLock.version = nextVersion;
  packagedRuntimeLock.packages[""].version = nextVersion;
  packagedProtocol.version = nextVersion;
  packagedRuntimeLock.packages.protocol.version = nextVersion;

  const runtimeIdentityPath = "packages/runtime-host/src/mcp-client-manager.ts";
  const runtimeIdentity = originals.get(runtimeIdentityPath);
  const identityPattern = /(name: "pi-roundtable-runtime-host", version: ")([^"]+)(")/g;
  const identityMatches = [...runtimeIdentity.matchAll(identityPattern)];
  if (identityMatches.length !== 1) {
    throw new Error(`${runtimeIdentityPath} must contain exactly one runtime identity version.`);
  }

  const outputs = new Map([
    ["VERSION", `${nextVersion}\n`],
    ["package.json", serializeJsonIfChanged(originals.get("package.json"), rootPackage)],
    ["package-lock.json", serializeJsonIfChanged(originals.get("package-lock.json"), rootLock)],
    ["packages/protocol-ts/package.json", replaceTopLevelVersion(
      originals.get("packages/protocol-ts/package.json"),
      protocolPackage.version,
    )],
    ["packages/runtime-host/package.json", serializeJsonIfChanged(originals.get("packages/runtime-host/package.json"), runtimePackage)],
    ["packages/sync-server/package.json", serializeJsonIfChanged(originals.get("packages/sync-server/package.json"), syncPackage)],
    ["packaging/windows-runtime/package.json", serializeJsonIfChanged(originals.get("packaging/windows-runtime/package.json"), packagedRuntimePackage)],
    ["packaging/windows-runtime/protocol/package.json", serializeJsonIfChanged(originals.get("packaging/windows-runtime/protocol/package.json"), packagedProtocol)],
    ["packaging/windows-runtime/package-lock.json", serializeJsonIfChanged(originals.get("packaging/windows-runtime/package-lock.json"), packagedRuntimeLock)],
    [runtimeIdentityPath, runtimeIdentity.replace(identityPattern, `$1${nextVersion}$3`)],
  ]);
  const changes = [...outputs].filter(([relativePath, content]) => content !== originals.get(relativePath));
  if (changes.length === 0) {
    runContractCheck();
    process.stdout.write(`Repository already uses product version ${nextVersion}.\n`);
    process.stdout.write("The signed stable update manifest and historical QA/documentation versions were intentionally left unchanged.\n");
    process.exitCode = 0;
  } else {
    const journal = {
      schemaVersion: 1,
      processId: process.pid,
      createdAt: new Date().toISOString(),
      nextVersion,
      originals: changes.map(([relativePath]) => ({
        relativePath,
        contentBase64: Buffer.from(originals.get(relativePath), "utf8").toString("base64"),
      })),
    };
    await writeAtomic(journalPath, `${JSON.stringify(journal)}\n`);
    try {
      for (const [relativePath, content] of changes) {
        await writeAtomic(path.join(repositoryRoot, relativePath), content);
      }
      runContractCheck({ transactionActive: true });
      await unlink(journalPath);
      process.stdout.write("The signed stable update manifest and historical QA/documentation versions were intentionally left unchanged.\n");
    } catch (error) {
      try {
        await restoreJournal(journal);
        await unlink(journalPath);
      } catch (rollbackError) {
        throw new AggregateError(
          [error, rollbackError],
          "Version update failed and rollback is incomplete; the journal was retained for deterministic recovery.",
        );
      }
      throw new Error("Version update failed and tracked version files were rolled back and verified.", { cause: error });
    }
  }
} finally {
  await unlink(lockPath).catch(() => {});
}

async function acquireLock() {
  try {
    await writeFile(lockPath, `${JSON.stringify({ processId: process.pid, createdAt: new Date().toISOString() })}\n`, {
      encoding: "utf8",
      flag: "wx",
    });
  } catch (error) {
    if (error.code !== "EEXIST") {
      throw error;
    }
    let owner;
    try {
      owner = JSON.parse(await readFile(lockPath, "utf8"));
    } catch {
      throw new Error("Version update lock is unreadable; inspect .version-update.lock before retrying.");
    }
    if (Number.isSafeInteger(owner.processId) && isProcessRunning(owner.processId)) {
      throw new Error(`Another version update is active in process ${owner.processId}.`);
    }
    await unlink(lockPath);
    await writeFile(lockPath, `${JSON.stringify({ processId: process.pid, createdAt: new Date().toISOString() })}\n`, {
      encoding: "utf8",
      flag: "wx",
    });
  }
}

async function recoverInterruptedTransaction() {
  let source;
  try {
    source = await readFile(journalPath, "utf8");
  } catch (error) {
    if (error.code === "ENOENT") {
      return;
    }
    throw error;
  }
  let journal;
  try {
    journal = JSON.parse(source);
  } catch (error) {
    throw new Error("Version update journal is corrupt and cannot be recovered automatically.", { cause: error });
  }
  await restoreJournal(journal);
  await unlink(journalPath);
  process.stdout.write("Recovered tracked version files from an interrupted update.\n");
}

async function restoreJournal(journal) {
  if (journal?.schemaVersion !== 1 ||
      !Number.isSafeInteger(journal.processId) ||
      journal.processId <= 0 ||
      !Number.isFinite(Date.parse(journal.createdAt)) ||
      !/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.test(journal.nextVersion ?? "") ||
      !Array.isArray(journal.originals) ||
      journal.originals.length === 0 ||
      journal.originals.length > trackedPaths.length) {
    throw new Error("Version update journal has an unsupported structure.");
  }
  const seenPaths = new Set();
  const restorations = journal.originals.map((entry) => {
    if (entry === null || typeof entry !== "object" ||
        typeof entry.relativePath !== "string" ||
        typeof entry.contentBase64 !== "string" ||
        seenPaths.has(entry.relativePath)) {
      throw new Error("Version update journal contains an invalid or duplicate entry.");
    }
    seenPaths.add(entry.relativePath);
    const target = resolveTrackedPath(entry.relativePath);
    const bytes = decodeStrictBase64(entry.contentBase64);
    if (bytes.length > maximumJournalContentBytes) {
      throw new Error("Version update journal entry exceeds its recovery size limit.");
    }
    let content;
    try {
      content = new TextDecoder("utf-8", { fatal: true }).decode(bytes);
    } catch (error) {
      throw new Error("Version update journal contains non-UTF-8 recovery content.", { cause: error });
    }
    return { relativePath: entry.relativePath, target, content };
  });
  for (const restoration of restorations) {
    await writeAtomic(restoration.target, restoration.content);
  }
  for (const restoration of restorations) {
    if (await readFile(restoration.target, "utf8") !== restoration.content) {
      throw new Error(`Version rollback verification failed for ${restoration.relativePath}.`);
    }
  }
}

function resolveTrackedPath(relativePath) {
  if (typeof relativePath !== "string" || path.isAbsolute(relativePath) || !trackedPathSet.has(relativePath)) {
    throw new Error("Version update journal contains an invalid path.");
  }
  const resolved = path.resolve(repositoryRoot, relativePath);
  const rootPrefix = `${repositoryRoot}${path.sep}`;
  if (!resolved.startsWith(rootPrefix)) {
    throw new Error("Version update journal path escapes the repository.");
  }
  return resolved;
}

function decodeStrictBase64(value) {
  if (value.length === 0 ||
      value.length > Math.ceil(maximumJournalContentBytes / 3) * 4 + 4 ||
      !/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(value)) {
    throw new Error("Version update journal contains invalid Base64 recovery content.");
  }
  const bytes = Buffer.from(value, "base64");
  if (bytes.toString("base64") !== value) {
    throw new Error("Version update journal contains non-canonical Base64 recovery content.");
  }
  return bytes;
}

function runContractCheck({ transactionActive = false } = {}) {
  const check = spawnSync(
    process.execPath,
    [path.join(repositoryRoot, "scripts", "check-repository-version.mjs")],
    {
      cwd: repositoryRoot,
      encoding: "utf8",
      env: {
        ...process.env,
        PI_ROUNDTABLE_VERSION_TRANSACTION: transactionActive ? "1" : "0",
      },
    },
  );
  if (check.stdout) {
    process.stdout.write(check.stdout);
  }
  if (check.stderr) {
    process.stderr.write(check.stderr);
  }
  if (check.status !== 0) {
    throw new Error("Repository version contract is inconsistent.");
  }
}

function assertWindowsInstallerRange(parts) {
  if (parts[0] > 255 || parts[1] > 255 || parts[2] > 65535) {
    throw new Error("Windows Installer product versions require major/minor <= 255 and patch <= 65535.");
  }
}

function isProcessRunning(processId) {
  try {
    process.kill(processId, 0);
    return true;
  } catch (error) {
    return error.code === "EPERM";
  }
}

function parseJson(originals, relativePath) {
  try {
    return JSON.parse(originals.get(relativePath));
  } catch (error) {
    throw new Error(`${relativePath} is not valid JSON.`, { cause: error });
  }
}

function requireRecord(value, label) {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    throw new Error(`${label} is missing or has the wrong structure.`);
  }
}

function serializeJsonIfChanged(original, value) {
  if (JSON.stringify(JSON.parse(original)) === JSON.stringify(value)) {
    return original;
  }
  return `${JSON.stringify(value, null, 2)}\n`;
}

function replaceTopLevelVersion(original, nextValue) {
  const pattern = /^(  "version"\s*:\s*")([^"]+)(",?\s*)$/gm;
  const matches = [...original.matchAll(pattern)];
  if (matches.length !== 1) {
    throw new Error("packages/protocol-ts/package.json must contain exactly one top-level version property.");
  }
  return original.replace(pattern, `$1${nextValue}$3`);
}

async function readText(relativePath) {
  return readFile(path.join(repositoryRoot, relativePath), "utf8");
}

async function writeAtomic(target, content) {
  const temporary = `${target}.version-${process.pid}.tmp`;
  await writeFile(temporary, content, "utf8");
  await rename(temporary, target);
}
