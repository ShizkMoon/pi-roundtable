using System.Text;
using System.Text.Json;
using PiRoundtable.Windows.Services;

namespace PiRoundtable.Windows.Tests;

[TestClass]
public sealed class MeetingEventStoreTests
{
    [TestMethod]
    public async Task Event_store_encrypts_and_round_trips_contiguous_events()
    {
        var root = TestRoot();
        try
        {
            var store = new MeetingEventStore(root);
            var first = Event(1, "runtime.lease_acquired", "sensitive meeting text");
            var second = Event(2, "meeting.opened", "private prompt material");

            Assert.IsTrue(await store.AppendAsync(first));
            Assert.IsTrue(await store.AppendAsync(second));
            Assert.IsFalse(await store.AppendAsync(second));

            var loaded = await store.LoadEventsAsync("meeting-test");
            Assert.HasCount(2, loaded);
            Assert.AreEqual(2UL, loaded[1].Sequence);
            Assert.AreEqual("private prompt material", loaded[1].Payload.GetProperty("message").GetString());

            var checkpoint = await store.GetCheckpointAsync("meeting-test");
            Assert.IsNotNull(checkpoint);
            Assert.AreEqual(2UL, checkpoint.LastSequence);
            Assert.IsFalse(checkpoint.CleanShutdown);

            foreach (var path in Directory.EnumerateFiles(Path.GetDirectoryName(store.DatabasePath)!, "roundtable.db*"))
            {
                var bytes = await File.ReadAllBytesAsync(path);
                var text = Encoding.UTF8.GetString(bytes);
                Assert.IsFalse(text.Contains("sensitive meeting text", StringComparison.Ordinal));
                Assert.IsFalse(text.Contains("private prompt material", StringComparison.Ordinal));
            }
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Event_store_rejects_a_sequence_gap()
    {
        var root = TestRoot();
        try
        {
            var store = new MeetingEventStore(root);
            await store.AppendAsync(Event(1, "runtime.lease_acquired", "first"));

            var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => store.AppendAsync(Event(3, "meeting.opened", "gap")));
            StringAssert.Contains(error.Message, "期待 2");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Lease_release_marks_a_clean_checkpoint()
    {
        var root = TestRoot();
        try
        {
            var store = new MeetingEventStore(root);
            await store.AppendAsync(Event(1, "runtime.lease_acquired", "lease"));
            await store.AppendAsync(Event(2, "runtime.lease_released", "released"));

            var checkpoint = await store.GetCheckpointAsync("meeting-test");
            Assert.IsNotNull(checkpoint);
            Assert.IsTrue(checkpoint.CleanShutdown);
            Assert.IsFalse(checkpoint.IsClosed);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Duplicate_event_id_requires_identical_full_content()
    {
        var root = TestRoot();
        try
        {
            var store = new MeetingEventStore(root);
            var original = Event(1, "runtime.lease_acquired", "original");
            await store.AppendAsync(original);

            var changed = original with
            {
                Payload = JsonDocument.Parse("{\"message\":\"changed\"}").RootElement.Clone(),
            };
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.AppendAsync(changed));

            var crossMeeting = original with { MeetingId = "another-meeting" };
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.AppendAsync(crossMeeting));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Concurrent_writers_cannot_commit_two_events_at_the_same_sequence()
    {
        var root = TestRoot();
        try
        {
            var firstStore = new MeetingEventStore(root);
            var secondStore = new MeetingEventStore(root);
            await firstStore.AppendAsync(Event(1, "runtime.lease_acquired", "lease"));

            var first = CaptureAsync(() => firstStore.AppendAsync(Event(2, "meeting.opened", "one")));
            var second = CaptureAsync(() => secondStore.AppendAsync(
                Event(2, "meeting.opened", "two") with { EventId = "event-two" }));
            var outcomes = await Task.WhenAll(first, second);

            Assert.AreEqual(1, outcomes.Count(outcome => outcome is null));
            Assert.AreEqual(1, outcomes.Count(outcome => outcome is InvalidDataException));
            Assert.HasCount(2, await firstStore.LoadEventsAsync("meeting-test"));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Closed_meeting_can_release_its_final_lease()
    {
        var root = TestRoot();
        try
        {
            var store = new MeetingEventStore(root);
            await store.AppendAsync(Event(1, "runtime.lease_acquired", "lease"));
            await store.AppendAsync(Event(2, "meeting.opened", "opened"));
            await store.AppendAsync(Event(3, "meeting.closed", "closed"));
            await store.AppendAsync(Event(4, "runtime.lease_released", "released"));

            var checkpoint = await store.GetCheckpointAsync("meeting-test");
            Assert.IsNotNull(checkpoint);
            Assert.IsTrue(checkpoint.IsClosed);
            Assert.IsTrue(checkpoint.CleanShutdown);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static RuntimeMeetingEvent Event(ulong sequence, string kind, string message)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { message }));
        return new RuntimeMeetingEvent(
            "meeting-test",
            $"event-{sequence}",
            sequence,
            1,
            kind,
            DateTimeOffset.UtcNow,
            "runtime-test",
            null,
            null,
            "public",
            [],
            document.RootElement.Clone());
    }

    private static string TestRoot() =>
        Path.Combine(Path.GetTempPath(), "PiRoundtable.Tests", Guid.NewGuid().ToString("N"));

    private static async Task<Exception?> CaptureAsync(Func<Task<bool>> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
