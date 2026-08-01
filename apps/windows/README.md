# Windows client

Native C#/WinUI 3 shell for the first desktop Runtime Owner. The project is unpackaged during early development so it can focus on runtime/process integration before MSIX identity and signing are chosen.

Current workstation note: only Visual Studio Build Tools and .NET runtimes are installed. Install the .NET 8 SDK plus the Visual Studio WinUI application development workload before expecting this project to restore or build.

`Services/NativeMeetingCore.cs` declares the narrow C ABI bridge. The native DLL must be copied beside the application when that bridge is first activated; the scaffold does not silently load an unverified binary.
