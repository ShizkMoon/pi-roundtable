import { rmSync } from "node:fs";
import { dirname, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const outputDirectories = [
  "packages/protocol-ts/dist",
  "packages/runtime-host/dist",
  "packages/sync-server/dist",
];

for (const relativePath of outputDirectories) {
  const outputPath = resolve(repositoryRoot, relativePath);
  if (!outputPath.startsWith(`${repositoryRoot}${sep}`)) {
    throw new Error(`Refusing to clean a path outside the repository: ${outputPath}`);
  }
  rmSync(outputPath, { force: true, recursive: true });
}
