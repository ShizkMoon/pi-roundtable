namespace PiRoundtable.Windows.Tests;

using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class ReleaseWorkflowContractTests
{
    [TestMethod]
    public void PromotionReadsAuthenticodePolicyFromTheSignedAssetAndPinsTheSigner()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            ".github", "workflows", "promote-windows-release.yml"));

        StringAssert.Contains(source, "$manifest.asset.authenticodeRequired");
        Assert.IsFalse(source.Contains("$manifest.authenticodeRequired", StringComparison.Ordinal));
        StringAssert.Contains(source, "WINDOWS_SIGNING_CERTIFICATE_THUMBPRINT");
        StringAssert.Contains(source, "SignerCertificate.Thumbprint");
        StringAssert.Contains(source, "TimeStamperCertificate");
        StringAssert.Contains(source, "gh release download $env:RELEASE_TAG");
        StringAssert.Contains(source, "Assert-ReleaseMsi -Path $uploadedMsi.FullName");
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
