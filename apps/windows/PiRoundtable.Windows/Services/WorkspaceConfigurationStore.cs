using System.Text.Json;
using System.Text.Json.Serialization;
using PiRoundtable.Windows.Models;

namespace PiRoundtable.Windows.Services;

internal sealed class WorkspaceConfigurationStore
{
    private readonly string _configurationPath;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public WorkspaceConfigurationStore(string? rootDirectory = null)
    {
        var directory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PiRoundtable");
        _configurationPath = Path.Combine(directory, "workspace.v1.json");
    }

    public string ConfigurationPath => _configurationPath;

    public async Task<WorkspaceConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_configurationPath))
        {
            return new WorkspaceConfiguration();
        }

        await using var stream = new FileStream(
            _configurationPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16_384,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var configuration = await JsonSerializer.DeserializeAsync<WorkspaceConfiguration>(
            stream,
            _jsonOptions,
            cancellationToken) ?? throw new InvalidDataException("工作区配置为空。");
        if (configuration.ConfigurationVersion != 1)
        {
            throw new InvalidDataException("工作区配置版本不受支持。");
        }
        return configuration;
    }

    public async Task SaveAsync(
        WorkspaceConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.ConfigurationVersion = 1;
        configuration.UpdatedAt = DateTimeOffset.UtcNow;
        var directory = Path.GetDirectoryName(_configurationPath)
            ?? throw new InvalidOperationException("工作区配置路径无效。");
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(_configurationPath)}.{Guid.NewGuid():N}.tmp");
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
                await JsonSerializer.SerializeAsync(stream, configuration, _jsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporaryPath, _configurationPath, overwrite: true);
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
