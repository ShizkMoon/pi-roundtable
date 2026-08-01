# Dependency baseline

Verified on 2026-08-01. Recheck before a major upgrade.

| Surface | Baseline | Reason |
| --- | --- | --- |
| Pi SDK | pending version pin | Pi is the planned default embedded runtime. Select and verify an exact SDK version during the direct-integration milestone. |
| Oh My Pi | optional compatibility target `v17.2.2`; RPC docs at commit `8baa3300bc23c721fc80c0307f0b7a8218be8fcb` | Keep OMP behind the public stdio JSONL adapter rather than importing private packages. Protocol v2 supports lossless chunking. |
| Android Gradle Plugin | `9.1.1` | Stable, supports API 37 and Gradle 9.3.1/JDK 17. |
| Kotlin Compose plugin | `2.4.10` | Kotlin 2.x Compose compiler plugin; kept explicit so compiler compatibility is visible. |
| Compose BOM | `2026.06.00` | Stable Compose dependency alignment. |
| Activity Compose | `1.13.0` | Current stable activity integration. |
| Material 3 Adaptive | `1.2.0` | Current stable adaptive library; no RC dependency. |
| Windows App SDK | `2.3.1` | Current stable package baseline. |
| .NET | `8.0` | Supported baseline for the C# WinUI shell. |

Primary references:

- [Pi SDK](https://github.com/earendil-works/pi/blob/main/packages/coding-agent/docs/sdk.md)
- [Oh My Pi RPC protocol](https://github.com/can1357/oh-my-pi/blob/8baa3300bc23c721fc80c0307f0b7a8218be8fcb/docs/rpc.md)
- [Android Gradle Plugin 9.1.1](https://developer.android.com/build/releases/agp-9-1-0-release-notes)
- [Compose compiler and dependency setup](https://developer.android.com/develop/ui/compose/setup-compose-dependencies-and-compiler)
- [Material 3 Adaptive releases](https://developer.android.com/jetpack/androidx/releases/compose-material3-adaptive)
- [Windows App SDK stable channel](https://learn.microsoft.com/windows/apps/windows-app-sdk/stable-channel)
- [Windows App SDK downloads](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads)
