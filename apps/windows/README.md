# Windows client

Native C#/WinUI 3 client for the first desktop Runtime Owner. The implemented local client starts the stdio Runtime Host, creates long-term and temporary roles, promotes or archives roles, streams normalized speech events, and supports cancellation and explicit interruption. It applies events to the C++ core before changing visible state.

The project targets .NET 10 LTS and Windows App SDK 2.3.1. The repository `global.json` pins the verified .NET 10 feature band while allowing servicing patches. The current workstation has the required .NET SDK; WinUI restore/build verification still depends on the installed Visual Studio workloads and package restore.

The first installer target is an x64/ARM64 MSI that includes the .NET runtime, Windows App SDK, Node Runtime Host, and native meeting core. The project already selects self-contained framework/runtime deployment. Provider/model/API-key input is implemented, but the key remains memory-only; Windows Credential Manager, signing, upgrade/repair behavior, bundled Node, ARM64 native-core packaging, and MSI creation remain planned.

For development, build TypeScript and C++ before building/running WinUI. The x64 project copies `out/build/dev/core/pi_roundtable_core.dll` beside the application when it exists. The client locates `packages/runtime-host/dist/host-main.js` by walking to the repository root; packaged-path overrides are available through `PI_ROUNDTABLE_RUNTIME_HOST_SCRIPT` and `PI_ROUNDTABLE_NODE_PATH`.
