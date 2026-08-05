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
        var directory = LocalDataRoot.Resolve(rootDirectory);
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
            sessions.Add(ConfigurationNormalizer.Normalize(session));
        }
        return sessions.OrderByDescending(session => session.UpdatedAt).ToArray();
    }

    public async Task SaveAsync(
        RoundtableSessionConfiguration session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ConfigurationNormalizer.Normalize(session);
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

    internal string StageDelete(string sessionId)
    {
        if (!SafeIdPattern.IsMatch(sessionId))
        {
            throw new InvalidDataException("会话 ID 不符合安全删除规则。");
        }
        Directory.CreateDirectory(_sessionsDirectory);
        var source = Path.Combine(_sessionsDirectory, $"{sessionId}.json");
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("找不到要删除的会话定义。", source);
        }
        var ticket = Path.Combine(_sessionsDirectory, $".{sessionId}.{Guid.NewGuid():N}.delete-pending");
        File.Move(source, ticket);
        return ticket;
    }

    internal void RollbackDelete(string sessionId, string ticket)
    {
        var destination = Path.Combine(_sessionsDirectory, $"{sessionId}.json");
        if (File.Exists(ticket) && !File.Exists(destination))
        {
            File.Move(ticket, destination);
        }
    }

    internal static void CompleteDelete(string ticket)
    {
        if (File.Exists(ticket))
        {
            File.Delete(ticket);
        }
    }

    internal IReadOnlyList<(string SessionId, string Ticket)> GetPendingDeletes()
    {
        if (!Directory.Exists(_sessionsDirectory))
        {
            return [];
        }
        var result = new List<(string SessionId, string Ticket)>();
        foreach (var ticket in Directory.EnumerateFiles(_sessionsDirectory, ".*.delete-pending"))
        {
            var name = Path.GetFileName(ticket);
            const string suffix = ".delete-pending";
            var body = name[1..^suffix.Length];
            var separator = body.LastIndexOf('.');
            var sessionId = separator > 0 ? body[..separator] : string.Empty;
            if (SafeIdPattern.IsMatch(sessionId))
            {
                result.Add((sessionId, ticket));
            }
        }
        return result;
    }
}
