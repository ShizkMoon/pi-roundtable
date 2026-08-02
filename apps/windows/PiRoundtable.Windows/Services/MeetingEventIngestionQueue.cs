namespace PiRoundtable.Windows.Services;

internal sealed class MeetingEventIngestionQueue(
    Func<RuntimeMeetingEvent, Task> acceptEventAsync,
    Func<string, Task> reportFaultAsync,
    Action<string>? trace = null)
{
    private readonly Func<RuntimeMeetingEvent, Task> _acceptEventAsync = acceptEventAsync;
    private readonly Func<string, Task> _reportFaultAsync = reportFaultAsync;
    private readonly Action<string>? _trace = trace;
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;
    private ulong _runtimeGeneration;
    private ulong _acceptedSequence;
    private bool _faulted;

    public async Task ResetAsync(ulong runtimeGeneration, ulong acceptedSequence)
    {
        if (runtimeGeneration == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(runtimeGeneration));
        }

        await DrainAsync();
        lock (_gate)
        {
            _runtimeGeneration = runtimeGeneration;
            _acceptedSequence = acceptedSequence;
            _faulted = false;
            Trace($"reset generation={runtimeGeneration} sequence={acceptedSequence}");
        }
    }

    public void Enqueue(RuntimeMeetingEvent meetingEvent)
    {
        lock (_gate)
        {
            Trace($"enqueue sequence={meetingEvent.Sequence} generation={meetingEvent.RuntimeGeneration} kind={meetingEvent.Kind}");
            _tail = ProcessAfterAsync(_tail, meetingEvent);
        }
    }

    public Task DrainAsync()
    {
        lock (_gate)
        {
            return _tail;
        }
    }

    private async Task ProcessAfterAsync(Task previous, RuntimeMeetingEvent meetingEvent)
    {
        try
        {
            await previous;
        }
        catch
        {
            // The preceding event reported its own failure. Keep this queue drainable.
        }

        ulong expectedSequence;
        lock (_gate)
        {
            if (_faulted)
            {
                Trace($"ignore sequence={meetingEvent.Sequence} reason=faulted");
                return;
            }
            if (meetingEvent.RuntimeGeneration != _runtimeGeneration)
            {
                Trace($"ignore sequence={meetingEvent.Sequence} reason=generation expected={_runtimeGeneration} actual={meetingEvent.RuntimeGeneration}");
                return;
            }
            if (meetingEvent.Sequence <= _acceptedSequence)
            {
                Trace($"ignore sequence={meetingEvent.Sequence} reason=duplicate accepted={_acceptedSequence}");
                return;
            }
            expectedSequence = _acceptedSequence + 1;
            if (meetingEvent.Sequence == expectedSequence)
            {
                // Reserve the sequence before any UI dispatch. Later events therefore never
                // depend on a cross-thread ViewModel property becoming visible.
                _acceptedSequence = meetingEvent.Sequence;
                Trace($"reserve sequence={meetingEvent.Sequence}");
            }
            else
            {
                _faulted = true;
                Trace($"fault sequence={meetingEvent.Sequence} expected={expectedSequence}");
            }
        }

        if (meetingEvent.Sequence != expectedSequence)
        {
            await _reportFaultAsync(
                $"Runtime Host 事件序号不连续：期待 {expectedSequence}，收到 {meetingEvent.Sequence}。会议已安全暂停。");
            return;
        }

        try
        {
            await _acceptEventAsync(meetingEvent);
            Trace($"accepted sequence={meetingEvent.Sequence}");
        }
        catch (Exception error)
        {
            lock (_gate)
            {
                _faulted = true;
            }
            await _reportFaultAsync(error.Message);
        }
    }

    private void Trace(string message)
    {
        try
        {
            _trace?.Invoke(message);
        }
        catch
        {
            // Diagnostic output must never affect event ingestion.
        }
    }
}
