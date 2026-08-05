using PiRoundtable.Windows.Models;

namespace PiRoundtable.Windows.Services;

internal sealed class WorkspaceController(
    WorkspaceConfigurationStore workspaceStore,
    RoundtableSessionStore sessionStore,
    ClientSettingsStore clientSettingsStore)
{
    private readonly WorkspaceConfigurationStore _workspaceStore =
        workspaceStore ?? throw new ArgumentNullException(nameof(workspaceStore));
    private readonly RoundtableSessionStore _sessionStore =
        sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
    private readonly ClientSettingsStore _clientSettingsStore =
        clientSettingsStore ?? throw new ArgumentNullException(nameof(clientSettingsStore));

    public string ConfigurationPath => _workspaceStore.ConfigurationPath;

    public Task<WorkspaceConfiguration> LoadAsync(CancellationToken cancellationToken) =>
        _workspaceStore.LoadAsync(cancellationToken);

    public Task SaveAsync(
        WorkspaceConfiguration workspace,
        CancellationToken cancellationToken) =>
        _workspaceStore.SaveAsync(workspace, cancellationToken);

    public Task<IReadOnlyList<RoundtableSessionConfiguration>> LoadSessionsAsync(
        CancellationToken cancellationToken) =>
        _sessionStore.LoadAllAsync(cancellationToken);

    public Task<ClientSettingsConfiguration> LoadClientSettingsAsync(
        CancellationToken cancellationToken) =>
        _clientSettingsStore.LoadAsync(cancellationToken);

    public Task SaveClientSettingsAsync(
        ClientSettingsConfiguration settings,
        CancellationToken cancellationToken) =>
        _clientSettingsStore.SaveAsync(settings, cancellationToken);
}

internal sealed class RoleProfileController(
    WindowsCredentialStore credentialStore,
    ProviderModelDiscoveryService providerModelDiscovery)
{
    private readonly WindowsCredentialStore _credentialStore =
        credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
    private readonly ProviderModelDiscoveryService _providerModelDiscovery =
        providerModelDiscovery ?? throw new ArgumentNullException(nameof(providerModelDiscovery));

    public Task SaveCredentialAsync(
        string credentialReference,
        string secret,
        CancellationToken cancellationToken) =>
        _credentialStore.SaveAsync(credentialReference, secret, cancellationToken);

    public Task<string?> ReadCredentialAsync(
        string credentialReference,
        CancellationToken cancellationToken) =>
        _credentialStore.ReadAsync(credentialReference, cancellationToken);

    public Task<IReadOnlyList<ProviderModelCandidate>> DiscoverModelsAsync(
        ProviderProfileConfiguration provider,
        string apiKey,
        CancellationToken cancellationToken) =>
        _providerModelDiscovery.DiscoverAsync(provider, apiKey, cancellationToken);
}

internal sealed class CatalogController(
    CatalogImportService catalogImport,
    LlmCatalogAnalysisService llmCatalogAnalysis)
{
    private readonly CatalogImportService _catalogImport =
        catalogImport ?? throw new ArgumentNullException(nameof(catalogImport));
    private readonly LlmCatalogAnalysisService _llmCatalogAnalysis =
        llmCatalogAnalysis ?? throw new ArgumentNullException(nameof(llmCatalogAnalysis));

    public Task<CatalogCheckout> PrepareAsync(Uri source, CancellationToken cancellationToken) =>
        _catalogImport.PrepareAsync(source, cancellationToken);

    public Task<CatalogImportAnalysis> AnalyzeAsync(
        string kind,
        CatalogRepositorySnapshot snapshot,
        ProviderProfileConfiguration provider,
        ModelProfileConfiguration model,
        string apiKey,
        CancellationToken cancellationToken) =>
        _llmCatalogAnalysis.AnalyzeAsync(
            kind,
            snapshot,
            provider,
            model,
            apiKey,
            cancellationToken);

    public Task<CatalogInstallResult> InstallAsync(
        CatalogCheckout checkout,
        string kind,
        string catalogId,
        string relativeRoot,
        CancellationToken cancellationToken) =>
        _catalogImport.InstallAsync(
            checkout,
            kind,
            catalogId,
            relativeRoot,
            cancellationToken);
}
