using System.Collections.Concurrent;

namespace PiRoundtable.Windows.Services;

/// <summary>
/// Serializes writers that target the same local SQLite file, including writers
/// owned by different store classes. SQLite remains the source of truth; this
/// gate only avoids avoidable lock contention inside one Windows process.
/// </summary>
internal static class LocalDatabaseWriteGate
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    public static SemaphoreSlim For(string databasePath) =>
        Gates.GetOrAdd(Path.GetFullPath(databasePath), static _ => new SemaphoreSlim(1, 1));
}
