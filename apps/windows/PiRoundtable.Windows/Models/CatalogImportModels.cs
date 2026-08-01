namespace PiRoundtable.Windows.Models;

internal sealed record CatalogRepositorySnapshot(
    Uri Source,
    string RequestedSubpath,
    IReadOnlyList<string> Files,
    IReadOnlyDictionary<string, string> TextFiles,
    IReadOnlyList<string> SkillRoots);

internal sealed class CatalogImportAnalysis
{
    public string Kind { get; init; } = string.Empty;
    public string RelativeRoot { get; init; } = ".";
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Risk { get; init; } = "medium";
    public List<string> RiskReasons { get; init; } = [];
    public bool Recommended { get; init; }
    public string? Transport { get; init; }
    public string? Command { get; init; }
    public List<string> Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }

    public string AuditSummary
    {
        get
        {
            var reasons = RiskReasons.Count == 0 ? "未报告特定风险" : string.Join("；", RiskReasons);
            var summary = $"{Description} 风险：{Risk}。{reasons}";
            return summary[..Math.Min(summary.Length, 1024)];
        }
    }
}

internal sealed record CatalogInstallResult(string InstallDirectory, string ContentDigest);
