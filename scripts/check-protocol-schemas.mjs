import { readdir, readFile } from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";

const expectedDraft = "https://json-schema.org/draft/2020-12/schema";
const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const schemaDirectory = path.join(repositoryRoot, "protocol", "schema");
const fileNames = (await readdir(schemaDirectory))
  .filter((name) => name.endsWith(".schema.json"))
  .sort((left, right) => left.localeCompare(right));

if (fileNames.length === 0) {
  throw new Error("protocol/schema does not contain any .schema.json files.");
}

const schemas = [];
const schemasById = new Map();
for (const fileName of fileNames) {
  const filePath = path.join(schemaDirectory, fileName);
  let schema;
  try {
    schema = JSON.parse(await readFile(filePath, "utf8"));
  } catch (error) {
    throw new Error(`${fileName}: invalid JSON: ${error.message}`, { cause: error });
  }

  if (!isRecord(schema)) {
    throw new Error(`${fileName}: schema root must be an object.`);
  }
  if (schema.$schema !== expectedDraft) {
    throw new Error(`${fileName}: $schema must be ${expectedDraft}.`);
  }
  if (typeof schema.$id !== "string") {
    throw new Error(`${fileName}: $id must be an absolute URL.`);
  }

  let schemaId;
  try {
    schemaId = new URL(schema.$id);
  } catch (error) {
    throw new Error(`${fileName}: $id is not an absolute URL.`, { cause: error });
  }
  if (schemaId.hash || schemaId.search || path.posix.basename(schemaId.pathname) !== fileName) {
    throw new Error(`${fileName}: $id must end with its file name and contain no query or fragment.`);
  }
  if (schemasById.has(schemaId.href)) {
    throw new Error(`${fileName}: duplicate $id ${schemaId.href}.`);
  }

  const entry = { fileName, schema, schemaId: schemaId.href };
  schemas.push(entry);
  schemasById.set(schemaId.href, entry);
}

for (const entry of schemas) {
  for (const reference of collectReferences(entry.schema)) {
    resolveReference(entry, reference);
  }
}

process.stdout.write(
  `Verified ${schemas.length} protocol schemas: JSON, draft, unique IDs, and local references are valid.\n`,
);

function collectReferences(value, result = []) {
  if (Array.isArray(value)) {
    for (const item of value) {
      collectReferences(item, result);
    }
    return result;
  }
  if (!isRecord(value)) {
    return result;
  }
  for (const [key, child] of Object.entries(value)) {
    if (key === "$ref") {
      if (typeof child !== "string" || child.length === 0) {
        throw new Error("Every $ref must be a non-empty string.");
      }
      result.push(child);
    } else {
      collectReferences(child, result);
    }
  }
  return result;
}

function resolveReference(source, reference) {
  let resolved;
  try {
    resolved = new URL(reference, source.schemaId);
  } catch (error) {
    throw new Error(`${source.fileName}: invalid $ref ${reference}.`, { cause: error });
  }

  const resourceId = new URL(resolved.href);
  const fragment = resourceId.hash;
  resourceId.hash = "";
  const target = schemasById.get(resourceId.href);
  if (!target) {
    throw new Error(`${source.fileName}: $ref ${reference} does not resolve to a local schema ID.`);
  }
  if (fragment === "" || fragment === "#") {
    return;
  }

  let decoded;
  try {
    decoded = decodeURIComponent(fragment.slice(1));
  } catch (error) {
    throw new Error(`${source.fileName}: $ref ${reference} contains invalid percent encoding.`, { cause: error });
  }
  if (!decoded.startsWith("/")) {
    throw new Error(`${source.fileName}: $ref ${reference} must use a JSON Pointer fragment.`);
  }
  let current = target.schema;
  for (const encodedToken of decoded.slice(1).split("/")) {
    const token = encodedToken.replaceAll("~1", "/").replaceAll("~0", "~");
    if (Array.isArray(current)) {
      if (!/^(0|[1-9]\d*)$/.test(token) || Number(token) >= current.length) {
        throw new Error(`${source.fileName}: $ref ${reference} contains an invalid array index.`);
      }
      current = current[Number(token)];
      continue;
    }
    if (!isRecord(current)) {
      throw new Error(`${source.fileName}: $ref ${reference} traverses a non-container value.`);
    }
    if (!Object.hasOwn(current, token)) {
      throw new Error(`${source.fileName}: $ref ${reference} points to a missing JSON Pointer token.`);
    }
    current = current[token];
  }
}

function isRecord(value) {
  return value !== null && typeof value === "object" && !Array.isArray(value);
}
