namespace PiRoundtable.Windows.Tests;

using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class ReleaseWorkflowContractTests
{
    [TestMethod]
    public void PromotionUsesRunBoundCandidateMetadataAndKeepsStableManifestIndependent()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            ".github", "workflows", "promote-windows-release.yml"));

        StringAssert.Contains(source, "$checkedOutCommit -ne $run.head_sha");
        StringAssert.Contains(source, "$versionSpec = \"$($run.head_sha):VERSION\"");
        StringAssert.Contains(source, "$tagCommit -ne $run.head_sha");
        StringAssert.Contains(source, "verify-windows-release-candidate.mjs");
        StringAssert.Contains(source, "CANDIDATE_METADATA");
        StringAssert.Contains(source, "$metadata.authenticodeRequired");
        StringAssert.Contains(source, "Only a production Authenticode-required candidate may enter a release draft.");
        StringAssert.Contains(source, "WINDOWS_SIGNING_CERTIFICATE_THUMBPRINT");
        StringAssert.Contains(source, "SignerCertificate.Thumbprint");
        StringAssert.Contains(source, "TimeStamperCertificate");
        StringAssert.Contains(source, "gh release download $env:RELEASE_TAG");
        StringAssert.Contains(source, "STABLE_MANIFEST_SHA256");
        StringAssert.Contains(source, "Get-MsiProperty -Path $msiPath -Name 'ProductVersion'");
        StringAssert.Contains(source, "concurrency:");
        Assert.IsFalse(source.Contains("--clobber", StringComparison.Ordinal));
        Assert.IsFalse(source.Contains("$manifest.asset.fileName", StringComparison.Ordinal));
    }

    [TestMethod]
    public void LocalPublisherRequiresReleaseEligibleEvidenceAndRedownloadsBeforePublishing()
    {
        var source = File.ReadAllText(FindRepositoryFile("scripts", "publish-windows-release.ps1"));

        StringAssert.Contains(source, "$rc.releaseEligible -ne $true");
        StringAssert.Contains(source, "Read-RcEvidenceFile");
        StringAssert.Contains(source, "production-signed-windows-build");
        StringAssert.Contains(source, "WINDOWS_SIGNING_CERTIFICATE_THUMBPRINT");
        StringAssert.Contains(source, "$metadata.authenticodeRequired -ne $true");
        StringAssert.Contains(source, "gh' @('release', 'download'");
        StringAssert.Contains(source, "verify-windows-release-candidate.mjs");
        StringAssert.Contains(source, "Get-AuthenticodeSignature");
        StringAssert.Contains(source, "if ($Publish)");
        StringAssert.Contains(source, "'--draft=false'");
        StringAssert.Contains(source, "public-assets");
        Assert.IsFalse(source.Contains("--clobber", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ReleaseCandidateRequiresProviderEvidenceBoundToTheSignedExecutable()
    {
        var qualityGate = File.ReadAllText(FindRepositoryFile("scripts", "invoke-quality-gate.ps1"));
        var providerQa = File.ReadAllText(FindRepositoryFile("scripts", "run-windows-deepseek-roundtable.ps1"));

        StringAssert.Contains(qualityGate, "RealProviderEvidencePath");
        StringAssert.Contains(qualityGate, "real-provider-windows-roundtable");
        StringAssert.Contains(qualityGate, "$report.productVersion -ne $Version");
        StringAssert.Contains(qualityGate, "$report.sourceCommit -ne $sourceCommit");
        StringAssert.Contains(qualityGate, "$report.appExecutableSha256 -ne $candidateAppHash");
        StringAssert.Contains(providerQa, "evidenceClass = 'real-provider-windows-roundtable'");
        StringAssert.Contains(providerQa, "productVersion = $productVersion");
        StringAssert.Contains(providerQa, "sourceCommit = $sourceCommit");
        StringAssert.Contains(providerQa, "appExecutableSha256 = $appExecutableSha256");
    }

    [TestMethod]
    public void ProductionLifecycleRequiresADisposableCleanVmAndExactRunOwnedCleanup()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "scripts", "test-windows-production-msi-lifecycle.ps1"));

        StringAssert.Contains(source, "[switch]$DisposableCleanVm");
        StringAssert.Contains(source, "Production lifecycle evidence must run inside a detected virtual machine");
        StringAssert.Contains(source, "Clean-VM preflight failed");
        StringAssert.Contains(source, "production-clean-vm-stable-to-candidate");
        StringAssert.Contains(source, "$signedReport.sourceCommit -ne $sourceCommit");
        StringAssert.Contains(source, "$candidateSignature.TimeStamperCertificate");
        StringAssert.Contains(source, "ExpectedSignerThumbprint");
        StringAssert.Contains(source, "-AcceptedExitCodes @(1603, 1638)");
        StringAssert.Contains(source, "$owned.attempted");
        Assert.IsFalse(source.Contains("foreach ($leftover in @(Get-RelatedProducts", StringComparison.Ordinal));
    }

    [TestMethod]
    public void StableManifestGeneratorVerifiesMsiIdentityAndProductionAuthenticodeBeforeSigning()
    {
        var source = File.ReadAllText(FindRepositoryFile("scripts", "New-WindowsUpdateManifest.ps1"));

        StringAssert.Contains(source, "ExpectedSignerThumbprint");
        StringAssert.Contains(source, "Get-MsiProperty -Path $resolvedMsi -Name 'ProductVersion'");
        StringAssert.Contains(source, "Get-MsiProperty -Path $resolvedMsi -Name 'UpgradeCode'");
        StringAssert.Contains(source, "Get-AuthenticodeSignature -LiteralPath $resolvedMsi");
        StringAssert.Contains(source, "$authenticode.TimeStamperCertificate");
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
