using System.Text.Json;
using PiRoundtable.Windows.Services;

namespace PiRoundtable.Windows.Tests;

[TestClass]
public sealed class RuntimeMeetingEventParserTests
{
    [TestMethod]
    public void Parser_accepts_valid_unknown_namespaced_kind_without_mapping_it_to_the_core()
    {
        var parsed = Parse("""
            {
              "protocolVersion": 1,
              "meetingId": "meeting.parser",
              "eventId": "event.future",
              "sequence": 1,
              "runtimeGeneration": 2,
              "kind": "vendor.future_event",
              "occurredAt": "2026-08-04T00:00:00.000Z",
              "visibility": "public",
              "payload": {}
            }
            """);

        Assert.AreEqual("vendor.future_event", parsed.Kind);
        Assert.IsFalse(MeetingCoreSession.IsSupportedEventKind(parsed.Kind));
    }

    [TestMethod]
    public void Parser_requires_explicit_visibility_and_fails_closed_on_unknown_values()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => Parse(ValidEvent.Replace(
            "\"visibility\": \"private\",",
            "\"visibility\": \"secret\",")));
        Assert.ThrowsExactly<InvalidOperationException>(() => Parse(ValidEvent.Replace(
            "\"visibility\": \"private\",",
            string.Empty)));
    }

    [TestMethod]
    public void Parser_enforces_public_and_private_audience_contracts()
    {
        var privateEvent = Parse(ValidEvent);
        CollectionAssert.AreEqual(
            new[] { "user.direct_host", "role.parser" },
            privateEvent.Audience.ToArray());

        Assert.ThrowsExactly<InvalidOperationException>(() => Parse(ValidEvent.Replace(
            "[\"user.direct_host\", \"role.parser\"]",
            "[]")));
        Assert.ThrowsExactly<InvalidOperationException>(() => Parse(ValidEvent.Replace(
            "[\"user.direct_host\", \"role.parser\"]",
            "[\"user.direct_host\", \"user.direct_host\"]")));
        Assert.ThrowsExactly<InvalidOperationException>(() => Parse(ValidEvent
            .Replace("\"visibility\": \"private\"", "\"visibility\": \"public\"")));
    }

    [TestMethod]
    public void Parser_rejects_control_suffixes_and_non_rfc3339_timestamps()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => Parse(ValidEvent.Replace(
            "\"event.private\"",
            "\"event.private\\n\"")));
        Assert.ThrowsExactly<InvalidOperationException>(() => Parse(ValidEvent.Replace(
            "\"message.direct_sent\"",
            "\"message.direct_sent\\n\"")));
        Assert.ThrowsExactly<InvalidOperationException>(() => Parse(ValidEvent.Replace(
            "2026-08-04T00:00:00.000Z",
            "2026-08-04")));
    }

    [TestMethod]
    public void Parser_rejects_duplicate_properties_instead_of_using_the_last_value()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => Parse(ValidEvent.Replace(
            "\"meetingId\": \"meeting.parser\",",
            "\"meetingId\": \"meeting.parser\",\n  \"meetingId\": \"meeting.other\",")));
    }

    private const string ValidEvent = """
        {
          "protocolVersion": 1,
          "meetingId": "meeting.parser",
          "eventId": "event.private",
          "sequence": 1,
          "runtimeGeneration": 2,
          "kind": "message.direct_sent",
          "occurredAt": "2026-08-04T00:00:00.000Z",
          "actorId": "user.direct_host",
          "targetId": "role.parser",
          "visibility": "private",
          "audience": ["user.direct_host", "role.parser"],
          "payload": {}
        }
        """;

    private static RuntimeMeetingEvent Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return RuntimeMeetingEventParser.Parse(document.RootElement);
    }
}
