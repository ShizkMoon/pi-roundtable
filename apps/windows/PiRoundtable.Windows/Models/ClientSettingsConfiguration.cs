namespace PiRoundtable.Windows.Models;

public sealed class ClientSettingsConfiguration
{
    public int ConfigurationVersion { get; set; } = 1;
    public string ThemeMode { get; set; } = "system";
    public bool RemoteSyncEnabled { get; set; }
    public string? RemoteSyncEndpoint { get; set; }
    public string RemoteSyncCredentialRef { get; set; } = "wincred://PiRoundtable/sync/default";
}
