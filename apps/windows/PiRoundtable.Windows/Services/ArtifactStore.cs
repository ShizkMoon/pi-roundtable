using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using PiRoundtable.Windows.Models;

namespace PiRoundtable.Windows.Services;

internal sealed record ArtifactStoreUsage(long StoredBytes, int ArtifactCount, long QuotaBytes);

internal interface IArtifactStore
{
    string DatabasePath { get; }

    long QuotaBytes { get; }

    Task InitializeAsync(CancellationToken cancellationToken = default);

    Task ImportAsync(
        string sourcePath,
        DocumentArtifactDescriptor descriptor,
        CancellationToken cancellationToken = default);

    Task BindToMeetingAsync(
        string artifactId,
        string meetingId,
        CancellationToken cancellationToken = default);

    Task ReleaseUnboundAsync(string artifactId, CancellationToken cancellationToken = default);

    Task<long> GetMeetingArtifactCountAsync(
        string meetingId,
        CancellationToken cancellationToken = default);

    Task DeleteMeetingAsync(string meetingId, CancellationToken cancellationToken = default);

    Task<ArtifactStoreUsage> GetUsageAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Owns the private platform database and content-addressed document bytes.
/// The public protocol receives only normalized text assembled by the Windows
/// client; local paths and CAS locations never cross the runtime boundary.
/// </summary>
internal sealed partial class ArtifactStore : IArtifactStore
{
    internal const long DefaultQuotaBytes = 256L * 1024 * 1024;
    private const int SchemaVersion = 1;
    private readonly string _artifactRoot;
    private readonly Func<DateTimeOffset> _now;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public ArtifactStore(
        string? rootDirectory = null,
        long quotaBytes = DefaultQuotaBytes,
        Func<DateTimeOffset>? now = null)
    {
        if (quotaBytes is < 1 or > 16L * 1024 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(quotaBytes));
        }
        var root = LocalDataRoot.Resolve(rootDirectory);
        DatabasePath = Path.Combine(root, "data", "platform.db");
        _artifactRoot = Path.Combine(root, "artifacts", "sha256");
        QuotaBytes = quotaBytes;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public string DatabasePath { get; }

    public long QuotaBytes { get; }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
            Directory.CreateDirectory(_artifactRoot);
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    CREATE TABLE IF NOT EXISTS platform_schema_info (
                        singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                        schema_version INTEGER NOT NULL
                    );
                    INSERT OR IGNORE INTO platform_schema_info(singleton, schema_version) VALUES(1, 1);
                    CREATE TABLE IF NOT EXISTS artifact_descriptors (
                        artifact_id TEXT PRIMARY KEY,
                        file_name TEXT NOT NULL,
                        format TEXT NOT NULL,
                        media_type TEXT NOT NULL,
                        byte_length INTEGER NOT NULL,
                        support TEXT NOT NULL,
                        warnings_json TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        last_accessed_at TEXT NOT NULL
                    );
                    CREATE TABLE IF NOT EXISTS artifact_bindings (
                        meeting_id TEXT NOT NULL,
                        artifact_id TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        PRIMARY KEY (meeting_id, artifact_id),
                        FOREIGN KEY (artifact_id) REFERENCES artifact_descriptors(artifact_id)
                            ON UPDATE RESTRICT ON DELETE CASCADE
                    );
                    CREATE INDEX IF NOT EXISTS ix_artifact_bindings_artifact
                        ON artifact_bindings(artifact_id);
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await ValidateSchemaAsync(connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            await CleanupOrphanFilesAsync(connection, cancellationToken);
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ImportAsync(
        string sourcePath,
        DocumentArtifactDescriptor descriptor,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ValidateDescriptor(descriptor);
        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        string? createdPath = null;
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var existing = await ReadDescriptorAsync(connection, descriptor.ArtifactId, cancellationToken);
            if (existing is not null)
            {
                if (existing != DescriptorFingerprint(descriptor))
                {
                    throw new InvalidDataException("相同工件摘要对应的描述不一致。");
                }
                var artifactPath = ArtifactPath(descriptor.ArtifactId);
                if (!await MatchesDescriptorAsync(artifactPath, descriptor, cancellationToken))
                {
                    DeleteFileBestEffort(artifactPath);
                    createdPath = await CopyVerifiedAsync(sourcePath, descriptor, cancellationToken);
                }
                await TouchAsync(connection, null, descriptor.ArtifactId, cancellationToken);
                createdPath = null;
                return;
            }

            createdPath = await CopyVerifiedAsync(sourcePath, descriptor, cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var pathsToDelete = await EnsureQuotaAsync(
                connection,
                transaction,
                descriptor.ByteLength,
                cancellationToken);
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO artifact_descriptors(
                        artifact_id, file_name, format, media_type, byte_length,
                        support, warnings_json, created_at, last_accessed_at)
                    VALUES(
                        $artifact_id, $file_name, $format, $media_type, $byte_length,
                        $support, $warnings_json, $created_at, $last_accessed_at)
                    """;
                AddDescriptorParameters(insert, descriptor, _now());
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            foreach (var path in pathsToDelete)
            {
                DeleteFileBestEffort(path);
            }
            createdPath = null;
        }
        finally
        {
            if (createdPath is not null)
            {
                DeleteFileBestEffort(createdPath);
            }
            _gate.Release();
        }
    }

    public async Task BindToMeetingAsync(
        string artifactId,
        string meetingId,
        CancellationToken cancellationToken = default)
    {
        ValidateArtifactId(artifactId);
        ValidateMeetingId(meetingId);
        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT OR IGNORE INTO artifact_bindings(meeting_id, artifact_id, created_at)
                    VALUES($meeting_id, $artifact_id, $created_at)
                    """;
                command.Parameters.AddWithValue("$meeting_id", meetingId);
                command.Parameters.AddWithValue("$artifact_id", artifactId);
                command.Parameters.AddWithValue("$created_at", _now().ToString("O"));
                try
                {
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (SqliteException error) when (error.SqliteErrorCode == 19)
                {
                    throw new InvalidDataException("待发送工件不存在或已被配额清理，请重新选择文件。", error);
                }
            }
            await TouchAsync(connection, transaction, artifactId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReleaseUnboundAsync(
        string artifactId,
        CancellationToken cancellationToken = default)
    {
        ValidateArtifactId(artifactId);
        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var removed = await DeleteDescriptorIfUnboundAsync(
                connection,
                transaction,
                artifactId,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            if (removed)
            {
                DeleteFileBestEffort(ArtifactPath(artifactId));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<long> GetMeetingArtifactCountAsync(
        string meetingId,
        CancellationToken cancellationToken = default)
    {
        ValidateMeetingId(meetingId);
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM artifact_bindings WHERE meeting_id = $meeting_id";
        command.Parameters.AddWithValue("$meeting_id", meetingId);
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    public async Task DeleteMeetingAsync(
        string meetingId,
        CancellationToken cancellationToken = default)
    {
        ValidateMeetingId(meetingId);
        await InitializeAsync(cancellationToken);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var artifactIds = new List<string>();
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = "SELECT artifact_id FROM artifact_bindings WHERE meeting_id = $meeting_id";
                select.Parameters.AddWithValue("$meeting_id", meetingId);
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    artifactIds.Add(reader.GetString(0));
                }
            }
            await using (var deleteBindings = connection.CreateCommand())
            {
                deleteBindings.Transaction = transaction;
                deleteBindings.CommandText = "DELETE FROM artifact_bindings WHERE meeting_id = $meeting_id";
                deleteBindings.Parameters.AddWithValue("$meeting_id", meetingId);
                await deleteBindings.ExecuteNonQueryAsync(cancellationToken);
            }
            var removedIds = new List<string>();
            foreach (var artifactId in artifactIds)
            {
                if (await DeleteDescriptorIfUnboundAsync(
                        connection,
                        transaction,
                        artifactId,
                        cancellationToken))
                {
                    removedIds.Add(artifactId);
                }
            }
            await transaction.CommitAsync(cancellationToken);
            foreach (var artifactId in removedIds)
            {
                DeleteFileBestEffort(ArtifactPath(artifactId));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ArtifactStoreUsage> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(byte_length), 0), COUNT(*) FROM artifact_descriptors";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new ArtifactStoreUsage(reader.GetInt64(0), reader.GetInt32(1), QuotaBytes);
    }

    private async Task<IReadOnlyList<string>> EnsureQuotaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long incomingBytes,
        CancellationToken cancellationToken)
    {
        long storedBytes;
        await using (var total = connection.CreateCommand())
        {
            total.Transaction = transaction;
            total.CommandText = "SELECT COALESCE(SUM(byte_length), 0) FROM artifact_descriptors";
            storedBytes = (long)(await total.ExecuteScalarAsync(cancellationToken) ?? 0L);
        }
        if (storedBytes + incomingBytes <= QuotaBytes)
        {
            return [];
        }
        var candidates = new List<(string ArtifactId, long ByteLength)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = transaction;
            select.CommandText = """
                SELECT d.artifact_id, d.byte_length
                FROM artifact_descriptors AS d
                LEFT JOIN artifact_bindings AS b ON b.artifact_id = d.artifact_id
                WHERE b.artifact_id IS NULL
                ORDER BY d.last_accessed_at ASC, d.artifact_id ASC
                """;
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add((reader.GetString(0), reader.GetInt64(1)));
            }
        }
        var paths = new List<string>();
        foreach (var candidate in candidates)
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM artifact_descriptors WHERE artifact_id = $artifact_id";
            delete.Parameters.AddWithValue("$artifact_id", candidate.ArtifactId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
            storedBytes -= candidate.ByteLength;
            paths.Add(ArtifactPath(candidate.ArtifactId));
            if (storedBytes + incomingBytes <= QuotaBytes)
            {
                return paths;
            }
        }
        throw new InvalidOperationException("工件配额已被会话引用占满；请删除不再需要的会话后重试。");
    }

    private async Task<string> CopyVerifiedAsync(
        string sourcePath,
        DocumentArtifactDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(sourcePath);
        var info = new FileInfo(fullPath);
        if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
            info.Length != descriptor.ByteLength)
        {
            throw new InvalidDataException("文档在预检后已丢失或发生变化，请重新选择。");
        }
        var target = ArtifactPath(descriptor.ArtifactId);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target))
        {
            if (await MatchesDescriptorAsync(target, descriptor, cancellationToken))
            {
                return target;
            }
            DeleteFileBestEffort(target);
            if (File.Exists(target))
            {
                throw new IOException("无法替换损坏的内容寻址工件。");
            }
        }
        var temporary = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using var source = new FileStream(
                fullPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using var destination = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long length = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }
                length = checked(length + read);
                if (length > DocumentPipeline.MaximumInputBytes)
                {
                    throw new InvalidDataException("文档在预检后超出大小限制。");
                }
                hash.AppendData(buffer.AsSpan(0, read));
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            await destination.FlushAsync(cancellationToken);
            destination.Flush(flushToDisk: true);
            await destination.DisposeAsync();
            var digest = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (length != descriptor.ByteLength || digest != descriptor.ArtifactId)
            {
                throw new InvalidDataException("文档在预检后发生变化，请重新确认。");
            }
            try
            {
                File.Move(temporary, target, overwrite: false);
            }
            catch (IOException) when (File.Exists(target))
            {
                DeleteFileBestEffort(temporary);
            }
            return target;
        }
        catch
        {
            DeleteFileBestEffort(temporary);
            throw;
        }
    }

    private async Task CleanupOrphanFilesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT artifact_id FROM artifact_descriptors";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                known.Add(reader.GetString(0));
            }
        }
        foreach (var file in Directory.EnumerateFiles(_artifactRoot, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            var artifactId = name.EndsWith(".blob", StringComparison.Ordinal)
                ? name[..^5]
                : string.Empty;
            if (!known.Contains(artifactId))
            {
                DeleteFileBestEffort(file);
            }
        }
    }

    private static async Task<bool> MatchesDescriptorAsync(
        string path,
        DocumentArtifactDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0 ||
                info.Length != descriptor.ByteLength)
            {
                return false;
            }
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }
                hash.AppendData(buffer.AsSpan(0, read));
            }
            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant() == descriptor.ArtifactId;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<string?> ReadDescriptorAsync(
        SqliteConnection connection,
        string artifactId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT file_name, format, media_type, byte_length, support, warnings_json
            FROM artifact_descriptors WHERE artifact_id = $artifact_id
            """;
        command.Parameters.AddWithValue("$artifact_id", artifactId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }
        return string.Join('\n',
            reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetInt64(3).ToString(System.Globalization.CultureInfo.InvariantCulture),
            reader.GetString(4), reader.GetString(5));
    }

    private static string DescriptorFingerprint(DocumentArtifactDescriptor descriptor) => string.Join('\n',
        descriptor.FileName,
        descriptor.Format.ToString(),
        descriptor.MediaType,
        descriptor.ByteLength.ToString(System.Globalization.CultureInfo.InvariantCulture),
        descriptor.Support.ToString(),
        JsonSerializer.Serialize(descriptor.Warnings));

    private static async Task TouchAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string artifactId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE artifact_descriptors
            SET last_accessed_at = $last_accessed_at
            WHERE artifact_id = $artifact_id
            """;
        command.Parameters.AddWithValue("$last_accessed_at", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$artifact_id", artifactId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<bool> DeleteDescriptorIfUnboundAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string artifactId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM artifact_descriptors
            WHERE artifact_id = $artifact_id
              AND NOT EXISTS (
                  SELECT 1 FROM artifact_bindings WHERE artifact_id = $artifact_id)
            """;
        command.Parameters.AddWithValue("$artifact_id", artifactId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static void AddDescriptorParameters(
        SqliteCommand command,
        DocumentArtifactDescriptor descriptor,
        DateTimeOffset now)
    {
        command.Parameters.AddWithValue("$artifact_id", descriptor.ArtifactId);
        command.Parameters.AddWithValue("$file_name", descriptor.FileName);
        command.Parameters.AddWithValue("$format", descriptor.Format.ToString());
        command.Parameters.AddWithValue("$media_type", descriptor.MediaType);
        command.Parameters.AddWithValue("$byte_length", descriptor.ByteLength);
        command.Parameters.AddWithValue("$support", descriptor.Support.ToString());
        command.Parameters.AddWithValue("$warnings_json", JsonSerializer.Serialize(descriptor.Warnings));
        command.Parameters.AddWithValue("$created_at", now.ToString("O"));
        command.Parameters.AddWithValue("$last_accessed_at", now.ToString("O"));
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true,
            DefaultTimeout = 5,
        }.ToString());
        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA busy_timeout=5000; PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;";
            await command.ExecuteNonQueryAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task ValidateSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT schema_version FROM platform_schema_info WHERE singleton = 1";
        var version = await command.ExecuteScalarAsync(cancellationToken);
        if (version is not long rawVersion || rawVersion != SchemaVersion)
        {
            throw new InvalidDataException("不支持或损坏的 platform.db 架构版本。");
        }
        await using var tables = connection.CreateCommand();
        tables.Transaction = transaction;
        tables.CommandText = """
            SELECT name FROM sqlite_schema
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%'
            ORDER BY name
            """;
        var actual = new List<string>();
        await using var reader = await tables.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            actual.Add(reader.GetString(0));
        }
        string[] expected = ["artifact_bindings", "artifact_descriptors", "platform_schema_info"];
        if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
        {
            throw new InvalidDataException("platform.db 包含未审阅的表或缺少必要表。");
        }
    }

    private string ArtifactPath(string artifactId) =>
        Path.Combine(_artifactRoot, artifactId[..2], artifactId + ".blob");

    private static void ValidateDescriptor(DocumentArtifactDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ValidateArtifactId(descriptor.ArtifactId);
        if (string.IsNullOrWhiteSpace(descriptor.FileName) || descriptor.FileName.Length > 255 ||
            Path.GetFileName(descriptor.FileName) != descriptor.FileName ||
            string.IsNullOrWhiteSpace(descriptor.MediaType) || descriptor.MediaType.Length > 128 ||
            descriptor.ByteLength is < 1 or > DocumentPipeline.MaximumInputBytes ||
            descriptor.Warnings.Count > 16 || descriptor.Warnings.Any(item => item.Length > 512))
        {
            throw new ArgumentException("工件描述无效。", nameof(descriptor));
        }
    }

    private static void ValidateArtifactId(string artifactId)
    {
        if (string.IsNullOrWhiteSpace(artifactId) || !Sha256().IsMatch(artifactId))
        {
            throw new ArgumentException("工件摘要无效。", nameof(artifactId));
        }
    }

    private static void ValidateMeetingId(string meetingId)
    {
        if (string.IsNullOrWhiteSpace(meetingId) || meetingId.Length > 128 || !SafeId().IsMatch(meetingId))
        {
            throw new ArgumentException("会议标识无效。", nameof(meetingId));
        }
    }

    private static void DeleteFileBestEffort(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeId();
}
