# Repository instructions

## Architecture invariants

- Treat `protocol/schema` as the public cross-platform contract. Do not expose raw Pi session records or internal TypeScript types to clients.
- Keep `core` deterministic, C++20, dependency-light, and free of network/UI/runtime-process concerns.
- Keep all Pi SDK integration code inside `packages/runtime-host`.
- A meeting has one authoritative runtime owner at a time. Every write must carry the active `runtimeGeneration`.
- The sync server relays and persists normalized events; it is not the default model executor.
- Android is a UI-only client. Windows owns the local runtime in the first implementation cycle.

## Status language

Use `implemented`, `scaffolded`, `planned`, `pending`, and `verified` precisely. A schema, interface, or UI mock is not a deployed feature.

## Verification

Before handing off a change, run the smallest relevant checks:

- C++: `cmake --preset dev`, `cmake --build --preset dev`, `ctest --preset dev`.
- TypeScript: `npm run build`, `npm test`.
- Android: `apps/android/gradlew.bat -p apps/android :app:assembleDebug` when JDK/SDK and network/cache permit.
- WinUI: `dotnet build apps/windows/PiRoundtable.Windows/PiRoundtable.Windows.csproj` when the required SDK/workload is installed.

Do not commit generated build output, credentials, raw Pi sessions, or local server data.
