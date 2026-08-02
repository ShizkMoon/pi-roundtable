using Microsoft.UI.Dispatching;

namespace PiRoundtable.Windows.Services;

internal enum RuntimeHostShutdownMode
{
    Suspend,
    Close,
}

internal interface IUiDispatcher
{
    bool TryEnqueue(Action callback);
}

internal sealed class DispatcherQueueAdapter(DispatcherQueue dispatcherQueue) : IUiDispatcher
{
    private readonly DispatcherQueue _dispatcherQueue = dispatcherQueue;

    public bool TryEnqueue(Action callback) => _dispatcherQueue.TryEnqueue(() => callback());
}

internal interface IRuntimeHostProcess : IAsyncDisposable
{
    event EventHandler<RuntimeMeetingEvent>? MeetingEventReceived;

    event EventHandler<string>? DiagnosticReceived;

    Task StartAsync(RuntimeHostStartOptions options, CancellationToken cancellationToken);

    Task<RuntimeCommandReceipt> SendCommandAsync(
        string kind,
        string? actorId,
        string? targetId,
        IReadOnlyDictionary<string, object?> payload,
        CancellationToken cancellationToken,
        string? commandId = null);

    Task StopAsync(RuntimeHostShutdownMode mode, CancellationToken cancellationToken);

    void Terminate();
}

internal interface IRuntimeHostFactory
{
    IRuntimeHostProcess Create(IMeetingEventStore eventStore);
}

internal sealed class RuntimeHostFactory : IRuntimeHostFactory
{
    public IRuntimeHostProcess Create(IMeetingEventStore eventStore) => new RuntimeHostProcess(eventStore);
}

internal interface IMeetingCoreSession : IDisposable
{
    void Apply(RuntimeMeetingEvent meetingEvent);
}

internal interface IMeetingCoreFactory
{
    IMeetingCoreSession Create();
}

internal sealed class MeetingCoreFactory : IMeetingCoreFactory
{
    public IMeetingCoreSession Create() => new MeetingCoreSession();
}
