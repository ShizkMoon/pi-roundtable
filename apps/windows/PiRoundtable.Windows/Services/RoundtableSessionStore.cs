using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PiRoundtable.Windows.Models;

namespace PiRoundtable.Windows.Services;

internal sealed class RoundtableSessionStore
{
    private static readonly Regex SafeIdPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly string _sessionsDirectory;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public RoundtableSessionStore(string? rootDirectory = null)
    {
        var directory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PiRoundtable");
        _sessionsDirectory = Path.Combine(directory, "sessions");
    }

    public async Task<IReadOnlyList<RoundtableSessionConfiguration>> LoadAllAsync(
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_sessionsDirectory))
        {
            return [];
        }
        var sessions = new List<RoundtableSessionConfiguration>();
        foreach (var path in Directory.EnumerateFiles(_sessionsDirectory, "*.json").Order())
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16_384,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var session = await JsonSerializer.DeserializeAsync<RoundtableSessionConfiguration>(
                stream,
                _jsonOptions,
                cancellationToken) ?? throw new InvalidDataException($"会话文件为空：{path}");
            if (session.SessionVersion != 1)
            {
                throw new InvalidDataException($"会话配置版本不受支持：{path}");
            }
            sessions.Add(session);
        }
        return sessions.OrderByDescending(session => session.UpdatedAt).ToArray();
    }

    public async Task SaveAsync(
        RoundtableSessionConfiguration session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!SafeIdPattern.IsMatch(session.SessionId))
        {
            throw new InvalidDataException("会话 ID 不符合公共协议，无法安全持久化。");
        }
        Directory.CreateDirectory(_sessionsDirectory);
        session.SessionVersion = 1;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        var path = Path.Combine(_sessionsDirectory, $"{session.SessionId}.json");
        var temporaryPath = Path.Combine(
            _sessionsDirectory,
            $".{session.SessionId}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16_384,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, session, _jsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
