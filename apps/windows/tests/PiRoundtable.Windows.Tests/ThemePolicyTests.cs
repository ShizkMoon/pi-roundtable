using PiRoundtable.Windows.Services;

namespace PiRoundtable.Windows.Tests;

[TestClass]
public sealed class ThemePolicyTests
{
    [TestMethod]
    [DataRow("1", "light", "light")]
    [DataRow("1", "dark", "dark")]
    [DataRow("1", "system", "system")]
    [DataRow(null, "dark", null)]
    [DataRow("0", "dark", null)]
    [DataRow("1", "invalid", null)]
    public void VisualQaOverrideRequiresTheExplicitQaSentinel(
        string? enabled,
        string? requestedTheme,
        string? expected)
    {
        Assert.AreEqual(expected, ThemePolicy.GetVisualQaOverride(enabled, requestedTheme));
    }

    [TestMethod]
    [DataRow("light", null, "light")]
    [DataRow("dark", null, "dark")]
    [DataRow("system", null, "system")]
    [DataRow("invalid", null, "system")]
    [DataRow("light", "dark", "dark")]
    [DataRow("dark", "light", "light")]
    [DataRow("dark", "system", "system")]
    [DataRow("light", "invalid", "light")]
    public void ResolveModeHonorsAValidVisualQaOverride(
        string configured,
        string? visualOverride,
        string expected)
    {
        Assert.AreEqual(expected, ThemePolicy.ResolveMode(configured, false, visualOverride));
    }

    [TestMethod]
    [DataRow("light", "light")]
    [DataRow("dark", "dark")]
    [DataRow("system", null)]
    public void ResolveModeAlwaysDefersToWindowsHighContrast(
        string configured,
        string? visualOverride)
    {
        Assert.AreEqual("system", ThemePolicy.ResolveMode(configured, true, visualOverride));
    }
}
