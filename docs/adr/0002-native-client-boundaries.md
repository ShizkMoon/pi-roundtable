# ADR 0002: Native platform shells with a narrow shared core

- Status: accepted
- Date: 2026-08-01

## Decision

Use Kotlin/Jetpack Compose Material 3 on Android and C#/WinUI 3 on Windows. Share protocol definitions and a deterministic C++ reducer rather than a cross-platform widget layer. Tauri 2 remains a candidate for a future desktop client only if delivery speed or cross-platform reach becomes more important than native interaction and accessibility.

Mac, iOS, and HarmonyOS projects are deferred until devices and a test loop exist. Their future clients will consume the same protocol rather than forcing placeholder projects into the repository now.
