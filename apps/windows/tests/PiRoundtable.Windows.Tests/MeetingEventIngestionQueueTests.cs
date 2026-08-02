using System.Text.Json;
using PiRoundtable.Windows.Services;

namespace PiRoundtable.Windows.Tests;

[TestClass]
public sealed class MeetingEventIngestionQueueTests
{
    [TestMethod]
    public async Task Serial_queue_accepts_a_burst_without_depending_on_UI_sequence_state()
    {
        var accepted = new List<ulong>();
        var faults = new List<string>();
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queue = new MeetingEventIngestionQueue(
            async meetingEvent =>
            {
                if (meetingEvent.Sequence == 1)
                {
                    await releaseFirst.Task;
                }
                accepted.Add(meetingEvent.Sequence);
            },
            message =>
            {
                faults.Add(message);
                return Task.CompletedTask;
            });
        await queue.ResetAsync(1, 0);

        foreach (var sequence in Enumerable.Range(1, 5).Select(value => (ulong)value))
        {
            queue.Enqueue(Event(sequence, 1));
        }
        releaseFirst.TrySetResult();
        await queue.DrainAsync();

        CollectionAssert.AreEqual(new ulong[] { 1, 2, 3, 4, 5 }, accepted);
        Assert.IsEmpty(faults);
    }

    [TestMethod]
    public async Task Serial_queue_ignores_stale_generation_and_faults_on_a_real_gap()
    {
        var accepted = new List<ulong>();
        var faults = new List<string>();
        var queue = new MeetingEventIngestionQueue(
            meetingEvent =>
            {
                accepted.Add(meetingEvent.Sequence);
                return Task.CompletedTask;
            },
            message =>
            {
                faults.Add(message);
                return Task.CompletedTask;
            });
        await queue.ResetAsync(2, 4);

        queue.Enqueue(Event(5, 1));
        queue.Enqueue(Event(5, 2));
        queue.Enqueue(Event(7, 2));
        queue.Enqueue(Event(6, 2));
        await queue.DrainAsync();

        CollectionAssert.AreEqual(new ulong[] { 5 }, accepted);
        Assert.HasCount(1, faults);
        StringAssert.Contains(faults[0], "期待 6，收到 7");
    }

    private static RuntimeMeetingEvent Event(ulong sequence, ulong runtimeGeneration) => new(
        "meeting.queue-test",
        $"event-{sequence}",
        sequence,
        runtimeGeneration,
        sequence == 1 ? "runtime.lease_acquired" : "role.registered",
        DateTimeOffset.UtcNow,
        sequence == 1 ? null : "role.test",
        null,
        null,
        "public",
        [],
        JsonDocument.Parse("{}").RootElement.Clone());
}
