using System.Text.Json;
using PiRoundtable.Windows.Models;

namespace PiRoundtable.Windows.Services;

internal sealed class ClientSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public ClientSettingsStore()
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PiRoundtable");
        Directory.CreateDirectory(root);
        ConfigurationPath = Path.Combine(root, "client-settings.json");
    }

    public string ConfigurationPath { get; }

    public async Task<ClientSettingsConfiguration> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ConfigurationPath))
        {
            return new ClientSettingsConfiguration();
        }
        await using var stream = File.OpenRead(ConfigurationPath);
        var configuration = await JsonSerializer.DeserializeAsync<ClientSettingsConfiguration>(
            stream,
            SerializerOptions,
            cancellationToken) ?? new ClientSettingsConfiguration();
        return ConfigurationNormalizer.Normalize(configuration);
    }

    public async Task SaveAsync(ClientSettingsConfiguration configuration, CancellationToken cancellationToken)
    {
        ConfigurationNormalizer.Normalize(configuration);
        var temporaryPath = ConfigurationPath + ".tmp";
        await using (var stream = File.Create(temporaryPath))
        {
            await JsonSerializer.SerializeAsync(stream, configuration, SerializerOptions, cancellationToken);
        }
        File.Move(temporaryPath, ConfigurationPath, true);
    }
}
