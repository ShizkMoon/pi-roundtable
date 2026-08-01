using System.Runtime.InteropServices;

namespace PiRoundtable.Windows.Services;

// Scaffold only: activate after an explicit native build copies the DLL beside the app.
internal static partial class NativeMeetingCore
{
    private const string LibraryName = "pi_roundtable_core";

    [LibraryImport(LibraryName, EntryPoint = "pr_meeting_create")]
    internal static partial nint Create();

    [LibraryImport(LibraryName, EntryPoint = "pr_meeting_destroy")]
    internal static partial void Destroy(nint meeting);

    [LibraryImport(LibraryName, EntryPoint = "pr_meeting_last_sequence")]
    internal static partial ulong LastSequence(nint meeting);

    [LibraryImport(LibraryName, EntryPoint = "pr_meeting_runtime_generation")]
    internal static partial ulong RuntimeGeneration(nint meeting);
}
