using PiRoundtable.Windows.Services;
using PiRoundtable.Windows.ViewModels;

namespace PiRoundtable.Windows.Tests;

[TestClass]
public sealed class DependencyBoundaryTests
{
    [TestMethod]
    public void ViewModel_can_be_created_without_a_WinUI_dispatcher_or_native_core()
    {
        var root = TestRoot();
        try
        {
            using var services = new MainViewModelServices(
                new ThrowingRuntimeHostFactory(),
                new ThrowingMeetingCoreFactory(),
                new MeetingEventIngestionQueueFactory(),
                new ThrowingMeetingEventStore(),
                new WorkspaceConfigurationStore(root),
                new RoundtableSessionStore(root),
                new WindowsCredentialStore(),
                new ClientSettingsStore(root),
                new ProviderModelDiscoveryService(),
                new CatalogImportService(root),
                new LlmCatalogAnalysisService(),
                new MeetingCommandGateway(),
                new RoleMemoryStore(root),
                new DocumentPipeline(),
                new ArtifactStore(root));
            var viewModel = new MainViewModel(
                new ImmediateDispatcher(),
                services);

            Assert.AreEqual("等待配置", viewModel.StatusText);
            Assert.HasCount(2, viewModel.Roles);
            Assert.IsNotNull(viewModel.SelectedSession);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [TestMethod]
    public void Views_delegate_concrete_application_construction_to_the_composition_root()
    {
        var rootSource = File.ReadAllText(FindRepositoryFile(
            "apps", "windows", "PiRoundtable.Windows", "ApplicationCompositionRoot.cs"));
        var appSource = File.ReadAllText(FindRepositoryFile(
            "apps", "windows", "PiRoundtable.Windows", "App.xaml.cs"));
        var windowSource = File.ReadAllText(FindRepositoryFile(
            "apps", "windows", "PiRoundtable.Windows", "MainWindow.xaml.cs"));
        var viewModelSource = File.ReadAllText(FindRepositoryFile(
            "apps", "windows", "PiRoundtable.Windows", "ViewModels", "MainViewModel.cs"));
        string[] productionConstructions =
        [
            "new RuntimeHostFactory(",
            "new MeetingCoreFactory(",
            "new MeetingEventIngestionQueueFactory(",
            "new MeetingEventStore(",
            "new WorkspaceConfigurationStore(",
            "new RoundtableSessionStore(",
            "new WindowsCredentialStore(",
            "new ClientSettingsStore(",
            "new ProviderModelDiscoveryService(",
            "new CatalogImportService(",
            "new LlmCatalogAnalysisService(",
            "new MeetingCommandGateway(",
            "new RoleMemoryStore(",
            "new DocumentPipeline(",
            "new ArtifactStore(",
            "new WindowsUpdateService(",
        ];

        foreach (var construction in productionConstructions)
        {
            StringAssert.Contains(rootSource, construction);
            Assert.IsFalse(appSource.Contains(construction, StringComparison.Ordinal));
            Assert.IsFalse(windowSource.Contains(construction, StringComparison.Ordinal));
            Assert.IsFalse(viewModelSource.Contains(construction, StringComparison.Ordinal));
        }
        StringAssert.Contains(appSource, "_compositionRoot.CreateMainWindow()");
        StringAssert.Contains(rootSource, "_viewModelServices.Dispose()");
        var closedCheckpointGate = viewModelSource.IndexOf(
            "if (checkpoint?.IsClosed == true)",
            StringComparison.Ordinal);
        var runtimeCreation = viewModelSource.IndexOf(
            "runtime = _sessionController.CreateRuntime();",
            StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, closedCheckpointGate);
        Assert.IsGreaterThan(closedCheckpointGate, runtimeCreation);
        Assert.IsLessThanOrEqualTo(
            4_810,
            File.ReadLines(FindRepositoryFile(
                "apps", "windows", "PiRoundtable.Windows", "ViewModels", "MainViewModel.cs")).Count(),
            "Lower this presentation-adapter budget whenever another use case moves behind a controller.");
    }

    private static string TestRoot() => Path.Combine(
        Path.GetTempPath(),
        "pi-roundtable-tests",
        Guid.NewGuid().ToString("N"));

    private static string FindRepositoryFile(params string[] segments)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. segments]);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        throw new FileNotFoundException($"Repository file was not found: {Path.Combine(segments)}");
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool TryEnqueue(Action callback)
        {
            callback();
            return true;
        }
    }

    private sealed class ThrowingRuntimeHostFactory : IRuntimeHostFactory
    {
        public IRuntimeHostProcess Create(IMeetingEventStore eventStore) =>
            throw new AssertFailedException("Runtime creation is not expected during construction.");
    }

    private sealed class ThrowingMeetingCoreFactory : IMeetingCoreFactory
    {
        public IMeetingCoreSession Create() =>
            throw new AssertFailedException("Core creation is not expected during construction.");
    }

    private sealed class ThrowingMeetingEventStore : IMeetingEventStore
    {
        public string DatabasePath => "unused";

        public Task InitializeAsync(CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Event store access is not expected during construction.");

        public Task<bool> AppendAsync(
            RuntimeMeetingEvent meetingEvent,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Event append is not expected during construction.");

        public Task<IReadOnlyList<RuntimeMeetingEvent>> LoadEventsAsync(
            string meetingId,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Event load is not expected during construction.");

        public Task<MeetingStoreCheckpoint?> GetCheckpointAsync(
            string meetingId,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Checkpoint access is not expected during construction.");

        public Task<CommandJournalReservation> ReserveCommandAsync(
            string meetingId,
            string commandId,
            string fingerprint,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Command journal access is not expected during construction.");

        public Task CompleteCommandAsync(
            string meetingId,
            string fingerprint,
            RuntimeCommandReceipt receipt,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Command journal access is not expected during construction.");

        public Task MarkCommandInterruptedAsync(
            string meetingId,
            string commandId,
            string fingerprint,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Command journal access is not expected during construction.");

        public Task<MeetingDeletionImpact> GetDeletionImpactAsync(
            string meetingId,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Meeting deletion is not expected during construction.");

        public Task DeleteMeetingAsync(
            string meetingId,
            CancellationToken cancellationToken = default) =>
            throw new AssertFailedException("Meeting deletion is not expected during construction.");
    }
}
