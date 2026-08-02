namespace PiRoundtable.Windows.Services;

internal static class LocalDataRoot
{
    internal const string EnvironmentVariable = "PI_ROUNDTABLE_DATA_ROOT";

    public static string Resolve(string? explicitRoot = null)
    {
        var candidate = string.IsNullOrWhiteSpace(explicitRoot)
            ? Environment.GetEnvironmentVariable(EnvironmentVariable)
            : explicitRoot;
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "PiRoundtable");
        }
        if (!Path.IsPathFullyQualified(candidate))
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariable} 必须是绝对路径，以免把会议数据写入不确定位置。");
        }
        var resolved = Path.GetFullPath(candidate);
        if (Directory.GetParent(resolved) is null)
        {
            throw new InvalidOperationException(
                $"{EnvironmentVariable} 不能直接指向文件系统根目录。");
        }
        return resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
