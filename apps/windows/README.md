# Windows client

Native C#/WinUI 3 shell for the first desktop Runtime Owner. The project is unpackaged during early development so it can focus on runtime/process integration before MSIX identity and signing are chosen.

The project targets .NET 10 LTS and Windows App SDK 2.3.1. The repository `global.json` pins the verified .NET 10 feature band while allowing servicing patches. The current workstation has the required .NET SDK; WinUI restore/build verification still depends on the installed Visual Studio workloads and package restore.

The first installer target is an x64/ARM64 MSI that includes the .NET runtime, Windows App SDK, and Windows Runtime Host, then guides the user through provider, model, credential, and local-data configuration. The project already selects self-contained framework/runtime deployment; onboarding, secure credential storage, signing, upgrade/repair behavior, and MSI packaging are still planned rather than implemented by this scaffold.

`Services/NativeMeetingCore.cs` declares the narrow C ABI bridge. The native DLL must be copied beside the application when that bridge is first activated; the scaffold does not silently load an unverified binary.
