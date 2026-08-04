# Windows Runtime dependency lock

This directory is the reproducible dependency manifest for the Runtime Host
embedded in the Windows MSI. The build replaces the placeholder `protocol`
directory with the compiled local `packages/protocol-ts` package, then runs
`npm ci --omit=dev --ignore-scripts` against the committed lock file.

When a runtime dependency changes, update `package.json`, run
`npm install --package-lock-only --ignore-scripts` in this directory, and
review the resulting integrity/version diff before committing it.

The Windows package build combines this exact npm lock with the application
NuGet lock to generate a commit-bound dependency inventory, CycloneDX SBOM,
and third-party notice file. Missing installed-package license metadata fails
the build instead of silently producing a partial inventory.
