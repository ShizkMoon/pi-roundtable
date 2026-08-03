namespace PiRoundtable.Windows.Tests;

using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class InstallerContractTests
{
    [TestMethod]
    public void Per_machine_shortcut_is_advertised_by_the_executable_component()
    {
        var packageSource = File.ReadAllText(FindRepositoryFile(
            "packaging", "windows-x64", "Package.wxs"));
        var generatorSource = File.ReadAllText(FindRepositoryFile(
            "scripts", "windows-packaging.ps1"));

        StringAssert.Contains(packageSource, "Scope=\"perMachine\"");
        StringAssert.Contains(packageSource, "StandardDirectory Id=\"ProgramMenuFolder\"");
        Assert.IsFalse(packageSource.Contains("RegistryValue", StringComparison.Ordinal));
        StringAssert.Contains(generatorSource, "Advertise=\"yes\"");
        StringAssert.Contains(generatorSource, "Directory=\"ApplicationProgramsFolder\"");
        StringAssert.Contains(generatorSource, "RemoveApplicationProgramsFolder");
        Assert.IsFalse(generatorSource.Contains("Root=\"HKCU\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Full_payload_lifecycle_has_a_distinct_timeout_and_waits_before_cleanup()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "scripts", "test-windows-msi-lifecycle.ps1"));

        StringAssert.Contains(source, "elseif ($UseFullPayload)");
        StringAssert.Contains(source, "45");
        StringAssert.Contains(source, "Waiting up to 15 additional minutes for transaction consistency before cleanup");
        StringAssert.Contains(source, "$process.WaitForExit()");
        StringAssert.Contains(source, "catch [InvalidOperationException]");
        StringAssert.Contains(source, "durationSeconds");
    }

    [TestMethod]
    public void Visual_matrix_rejects_duplicate_or_mismatched_theme_evidence()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "scripts", "merge-windows-visual-matrix.ps1"));

        StringAssert.Contains(source, "$themes.Count -ne 3");
        StringAssert.Contains(source, "$uniqueThemeKinds.Count -ne 3");
        StringAssert.Contains(source, "$theme.report.highContrast -ne $expectedHighContrast");
        StringAssert.Contains(source, "$theme.report.expectedHighContrast -ne $expectedHighContrastLabel");
        StringAssert.Contains(source, "$theme.report.requestedTheme -ne $expectedRequestedTheme");
    }

    private static string FindRepositoryFile(params string[] segments)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"Repository file was not found: {Path.Combine(segments)}");
    }
}
