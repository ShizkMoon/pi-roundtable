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
