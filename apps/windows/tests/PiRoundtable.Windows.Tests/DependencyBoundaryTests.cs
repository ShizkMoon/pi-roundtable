using PiRoundtable.Windows.Services;
using PiRoundtable.Windows.ViewModels;

namespace PiRoundtable.Windows.Tests;

[TestClass]
public sealed class DependencyBoundaryTests
{
    [TestMethod]
    public void ViewModel_can_be_created_without_a_WinUI_dispatcher_or_native_core()
    {
        var viewModel = new MainViewModel(
            new ImmediateDispatcher(),
            new ThrowingRuntimeHostFactory(),
            new ThrowingMeetingCoreFactory(),
            new ThrowingMeetingEventStore());

        Assert.AreEqual("等待配置", viewModel.StatusText);
        Assert.HasCount(2, viewModel.Roles);
        Assert.IsNotNull(viewModel.SelectedSession);
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
    }
}
