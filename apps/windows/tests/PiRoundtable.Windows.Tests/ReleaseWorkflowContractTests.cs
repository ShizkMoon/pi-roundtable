namespace PiRoundtable.Windows.Tests;

using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class ReleaseWorkflowContractTests
{
    [TestMethod]
    public void ReleaseCandidateBuildsAndVerifiesThePersonalReleaseWithoutExternalInfrastructure()
    {
        var source = File.ReadAllText(FindRepositoryFile("scripts", "invoke-quality-gate.ps1"));

        StringAssert.Contains(source, "personal-windows-release-build");
        StringAssert.Contains(source, "scripts/build-windows-x64.ps1");
        StringAssert.Contains(source, "scripts/verify-windows-release-candidate.mjs");
        StringAssert.Contains(source, "full-payload-isolated-msi-lifecycle");
        StringAssert.Contains(source, "'-UseFullPayload'");
        StringAssert.Contains(source, "releaseEligible = $Scope -eq 'ReleaseCandidate'");
        Assert.IsFalse(source.Contains("SignedBuildReportPath", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("ExpectedSignerThumbprint", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("ProductionLifecycleReportPath", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("VisualReportPath96", StringComparison.Ordinal));
    }

    [TestMethod]
    public void LocalPublisherRequiresExactMainEvidenceAndRedownloadsBeforePublishing()
    {
        var source = File.ReadAllText(FindRepositoryFile("scripts", "publish-windows-release.ps1"));

        StringAssert.Contains(source, "$rc.releaseEligible -ne $true");
        StringAssert.Contains(source, "Read-RcEvidenceFile");
        StringAssert.Contains(source, "personal-windows-release-build");
        StringAssert.Contains(source, "fullPayloadIsolatedLifecycle");
        StringAssert.Contains(source, "Assert-OptionalAuthenticode");
        StringAssert.Contains(source, "gh' @('release', 'download'");
        StringAssert.Contains(source, "verify-windows-release-candidate.mjs");
        StringAssert.Contains(source, "if ($Publish)");
        StringAssert.Contains(source, "'--draft=false'");
        StringAssert.Contains(source, "public-assets");
        Assert.IsFalse(source.Contains("WINDOWS_SIGNING_CERTIFICATE_THUMBPRINT", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("productionLifecycle", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("visualMatrix", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("--clobber", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ProviderAndVisualHarnessesRemainAvailableButAreNotReleaseRequirements()
    {
        var qualityGate = File.ReadAllText(FindRepositoryFile("scripts", "invoke-quality-gate.ps1"));
        var providerQa = File.ReadAllText(FindRepositoryFile("scripts", "run-windows-deepseek-roundtable.ps1"));
        var visualQa = File.ReadAllText(FindRepositoryFile("scripts", "run-windows-theme-visual-qa.ps1"));

        StringAssert.Contains(providerQa, "evidenceClass = 'real-provider-windows-roundtable'");
        StringAssert.Contains(providerQa, "appExecutableSha256 = $appExecutableSha256");
        StringAssert.Contains(visualQa, "real-windows-theme-dpi-visual-qa");
        Assert.IsFalse(qualityGate.Contains("RealProviderEvidencePath", StringComparison.Ordinal));
        Assert.IsFalse(qualityGate.Contains("VisualReportPath192", StringComparison.Ordinal));
    }

    [TestMethod]
    public void OptionalProductionLifecycleStillRequiresADisposableVmAndExactRunOwnedCleanup()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "scripts", "test-windows-production-msi-lifecycle.ps1"));

        StringAssert.Contains(source, "[switch]$DisposableCleanVm");
        StringAssert.Contains(source, "Production lifecycle evidence must run inside a detected virtual machine");
        StringAssert.Contains(source, "Clean-VM preflight failed");
        StringAssert.Contains(source, "production-clean-vm-stable-to-candidate");
        StringAssert.Contains(source, "$owned.attempted");
        Assert.IsFalse(source.Contains("foreach ($leftover in @(Get-RelatedProducts", StringComparison.Ordinal));
    }

    [TestMethod]
    public void StableManifestAllowsUnsignedMsiAndValidatesAuthenticodeWhenRequested()
    {
        var source = File.ReadAllText(FindRepositoryFile("scripts", "New-WindowsUpdateManifest.ps1"));

        StringAssert.Contains(source, "Get-MsiProperty -Path $resolvedMsi -Name 'ProductVersion'");
        StringAssert.Contains(source, "Get-MsiProperty -Path $resolvedMsi -Name 'UpgradeCode'");
        StringAssert.Contains(source, "if ($AuthenticodeRequired)");
        StringAssert.Contains(source, "ExpectedSignerThumbprint is required when AuthenticodeRequired is enabled.");
        StringAssert.Contains(source, "Get-AuthenticodeSignature -LiteralPath $resolvedMsi");
        Assert.IsFalse(source.Contains("Stable release manifests must require production Authenticode", StringComparison.Ordinal));
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
