using Microsoft.UI.Dispatching;
using PiRoundtable.Windows.Services;
using PiRoundtable.Windows.Services.Updater;
using PiRoundtable.Windows.ViewModels;

namespace PiRoundtable.Windows;

/// <summary>
/// Owns production dependency construction for the Windows process. Views and
/// presentation adapters receive composed services and never choose concrete
/// storage, runtime, catalog, or update implementations themselves.
/// </summary>
internal sealed class WindowsApplicationCompositionRoot : IDisposable
{
    private readonly MainViewModelServices _viewModelServices;
    private readonly WindowsUpdateService _updateService;
    private int _windowCreated;
    private int _disposed;

    public WindowsApplicationCompositionRoot()
        : this(
            new MainViewModelServices(
                new RuntimeHostFactory(),
                new MeetingCoreFactory(),
                new MeetingEventIngestionQueueFactory(),
                new MeetingEventStore(),
                new WorkspaceConfigurationStore(),
                new RoundtableSessionStore(),
                new WindowsCredentialStore(),
                new ClientSettingsStore(),
                new ProviderModelDiscoveryService(),
                new CatalogImportService(),
                new LlmCatalogAnalysisService()),
            new WindowsUpdateService())
    {
    }

    internal WindowsApplicationCompositionRoot(
        MainViewModelServices viewModelServices,
        WindowsUpdateService updateService)
    {
        _viewModelServices = viewModelServices ?? throw new ArgumentNullException(nameof(viewModelServices));
        _updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));
    }

    public MainWindow CreateMainWindow()
    {
        ThrowIfDisposed();
        if (Interlocked.Exchange(ref _windowCreated, 1) != 0)
        {
            throw new InvalidOperationException("The Windows composition root owns exactly one main window.");
        }
        return new MainWindow(this);
    }

    internal MainViewModel CreateMainViewModel(DispatcherQueue dispatcherQueue)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(dispatcherQueue);
        return new MainViewModel(new DispatcherQueueAdapter(dispatcherQueue), _viewModelServices);
    }

    internal WindowsUpdateService UpdateService
    {
        get
        {
            ThrowIfDisposed();
            return _updateService;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try
            {
                _updateService.Dispose();
            }
            finally
            {
                _viewModelServices.Dispose();
            }
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(WindowsApplicationCompositionRoot));
        }
    }
}

/// <summary>
/// Immutable dependency bundle for the presentation adapter. Concrete
/// production instances are created only by <see cref="WindowsApplicationCompositionRoot"/>.
/// </summary>
internal sealed class MainViewModelServices : IDisposable
{
    private int _disposed;

    public MainViewModelServices(
        IRuntimeHostFactory runtimeHostFactory,
        IMeetingCoreFactory meetingCoreFactory,
        IMeetingEventIngestionQueueFactory eventIngestionQueueFactory,
        IMeetingEventStore eventStore,
        WorkspaceConfigurationStore workspaceStore,
        RoundtableSessionStore sessionStore,
        WindowsCredentialStore credentialStore,
        ClientSettingsStore clientSettingsStore,
        ProviderModelDiscoveryService providerModelDiscovery,
        CatalogImportService catalogImport,
        LlmCatalogAnalysisService llmCatalogAnalysis)
    {
        RuntimeHostFactory = runtimeHostFactory ?? throw new ArgumentNullException(nameof(runtimeHostFactory));
        MeetingCoreFactory = meetingCoreFactory ?? throw new ArgumentNullException(nameof(meetingCoreFactory));
        EventIngestionQueueFactory = eventIngestionQueueFactory ?? throw new ArgumentNullException(nameof(eventIngestionQueueFactory));
        EventStore = eventStore ?? throw new ArgumentNullException(nameof(eventStore));
        WorkspaceStore = workspaceStore ?? throw new ArgumentNullException(nameof(workspaceStore));
        SessionStore = sessionStore ?? throw new ArgumentNullException(nameof(sessionStore));
        CredentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
        ClientSettingsStore = clientSettingsStore ?? throw new ArgumentNullException(nameof(clientSettingsStore));
        ProviderModelDiscovery = providerModelDiscovery ?? throw new ArgumentNullException(nameof(providerModelDiscovery));
        CatalogImport = catalogImport ?? throw new ArgumentNullException(nameof(catalogImport));
        LlmCatalogAnalysis = llmCatalogAnalysis ?? throw new ArgumentNullException(nameof(llmCatalogAnalysis));
    }

    public IRuntimeHostFactory RuntimeHostFactory { get; }
    public IMeetingCoreFactory MeetingCoreFactory { get; }
    public IMeetingEventIngestionQueueFactory EventIngestionQueueFactory { get; }
    public IMeetingEventStore EventStore { get; }
    public WorkspaceConfigurationStore WorkspaceStore { get; }
    public RoundtableSessionStore SessionStore { get; }
    public WindowsCredentialStore CredentialStore { get; }
    public ClientSettingsStore ClientSettingsStore { get; }
    public ProviderModelDiscoveryService ProviderModelDiscovery { get; }
    public CatalogImportService CatalogImport { get; }
    public LlmCatalogAnalysisService LlmCatalogAnalysis { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        try
        {
            ProviderModelDiscovery.Dispose();
        }
        finally
        {
            LlmCatalogAnalysis.Dispose();
        }
    }
}
