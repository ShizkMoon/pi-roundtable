namespace PiRoundtable.Windows.Services;

using System.ComponentModel;
using System.Runtime.InteropServices;

/// <summary>
/// Resolves the effective application theme without allowing a persisted or
/// test-only preference to override Windows high-contrast colors.
/// </summary>
internal static class ThemePolicy
{
    private const uint SpiGetHighContrast = 0x0042;
    private const uint HighContrastEnabled = 0x00000001;

    internal const string VisualQaOverrideVariable = "PI_ROUNDTABLE_VISUAL_QA_THEME";
    internal const string VisualQaEnabledVariable = "PI_ROUNDTABLE_VISUAL_QA";

    internal static string? GetVisualQaOverride(string? enabled, string? requestedTheme)
    {
        return enabled == "1" && requestedTheme is "light" or "dark" or "system"
            ? requestedTheme
            : null;
    }

    internal static bool IsWindowsHighContrastEnabled()
    {
        var value = new HighContrast
        {
            Size = (uint)Marshal.SizeOf<HighContrast>(),
        };
        if (!SystemParametersInfo(SpiGetHighContrast, value.Size, ref value, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Unable to read Windows high-contrast state.");
        }
        return (value.Flags & HighContrastEnabled) != 0;
    }

    internal static string ResolveMode(
        string? configuredMode,
        bool highContrast,
        string? visualQaOverride = null)
    {
        if (highContrast)
        {
            return "system";
        }

        if (visualQaOverride is "light" or "dark" or "system")
        {
            return visualQaOverride;
        }

        return configuredMode is "light" or "dark" ? configuredMode : "system";
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HighContrast
    {
        internal uint Size;
        internal uint Flags;
        internal nint DefaultScheme;
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(
        uint action,
        uint parameter,
        ref HighContrast value,
        uint update);
}
