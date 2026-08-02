# Dependency baseline

Verified on 2026-08-01. Recheck before a major upgrade.

| Surface | Baseline | Reason |
| --- | --- | --- |
| Pi SDK | `@earendil-works/pi-coding-agent` `0.83.0`; `@earendil-works/pi-ai` `0.83.0`; Node `>=22.19.0` | Exact pins keep the direct adapter reproducible. The repository baseline remains Node 24. |
| Android Gradle Plugin | `9.1.1` | Stable, supports API 37 and Gradle 9.3.1/JDK 17. |
| Kotlin Compose plugin | `2.4.10` | Kotlin 2.x Compose compiler plugin; kept explicit so compiler compatibility is visible. |
| Compose BOM | `2026.06.00` | Stable Compose dependency alignment. |
| Activity Compose | `1.13.0` | Current stable activity integration. |
| Material 3 Adaptive | `1.2.0` | Current stable adaptive library; no RC dependency. |
| Windows App SDK | `2.3.1` | Current stable package baseline. |
| .NET | SDK `10.0.302`; runtime family `10.0` LTS | Current Windows-client baseline, pinned by `global.json` with servicing-patch roll-forward. |

Primary references:

- [Pi SDK](https://github.com/earendil-works/pi/blob/main/packages/coding-agent/docs/sdk.md)
- [Android Gradle Plugin 9.1.1](https://developer.android.com/build/releases/agp-9-1-0-release-notes)
- [Compose compiler and dependency setup](https://developer.android.com/develop/ui/compose/setup-compose-dependencies-and-compiler)
- [Material 3 Adaptive releases](https://developer.android.com/jetpack/androidx/releases/compose-material3-adaptive)
- [Windows App SDK stable channel](https://learn.microsoft.com/windows/apps/windows-app-sdk/stable-channel)
- [Windows App SDK downloads](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads)
- [.NET support policy](https://dotnet.microsoft.com/platform/support/policy/dotnet-core)
