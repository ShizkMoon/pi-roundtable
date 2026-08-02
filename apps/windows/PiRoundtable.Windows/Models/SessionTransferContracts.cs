using System.Text.Json.Serialization;

namespace PiRoundtable.Windows.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SessionExportPackage
{
    public int PackageVersion { get; set; } = 1;
    public int ProtocolVersion { get; set; } = 1;
    public string SourceSessionId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public DateTimeOffset ExportedAt { get; set; }
    public bool IncludesPrivateMessages { get; set; }
    public List<SessionExportMessage> Messages { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SessionExportMessage
{
    public string MessageId { get; set; } = string.Empty;
    public string Kind { get; set; } = "role";
    public string SpeakerId { get; set; } = string.Empty;
    public string SpeakerName { get; set; } = string.Empty;
    public string Visibility { get; set; } = "public";
    public List<string> AudienceRoleIds { get; set; } = [];
    public string Text { get; set; } = string.Empty;
    public string State { get; set; } = "completed";
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed record SessionImportPreflight(
    SessionExportPackage Package,
    int PublicMessageCount,
    int PrivateMessageCount,
    int SpeakerCount,
    DateTimeOffset? FirstMessageAt,
    DateTimeOffset? LastMessageAt);
