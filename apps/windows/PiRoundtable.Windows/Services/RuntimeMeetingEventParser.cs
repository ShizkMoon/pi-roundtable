using System.Text.Json;
using System.Text.RegularExpressions;

namespace PiRoundtable.Windows.Services;

internal static partial class RuntimeMeetingEventParser
{
    private static readonly HashSet<string> EventProperties = new(StringComparer.Ordinal)
    {
        "protocolVersion",
        "meetingId",
        "eventId",
        "sequence",
        "runtimeGeneration",
        "kind",
        "occurredAt",
        "actorId",
        "targetId",
        "causationId",
        "visibility",
        "audience",
        "payload",
    };

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("^[a-z][a-z0-9_]*(?:\\.[a-z][a-z0-9_]*)+\\z", RegexOptions.CultureInvariant)]
    private static partial Regex EventKindPattern();

    [GeneratedRegex("^\\d{4}-\\d{2}-\\d{2}T\\d{2}:\\d{2}:\\d{2}(?:\\.\\d{1,9})?(?:Z|[+-]\\d{2}:\\d{2})\\z", RegexOptions.CultureInvariant)]
    private static partial Regex TimestampPattern();

    public static RuntimeMeetingEvent Parse(JsonElement eventElement)
    {
        try
        {
            return ParseCore(eventElement);
        }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidOperationException("Runtime Host event frame violates protocol v1.", error);
        }
    }

    private static RuntimeMeetingEvent ParseCore(JsonElement eventElement)
    {
        if (eventElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("event must be an object");
        }
        var seenProperties = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in eventElement.EnumerateObject())
        {
            if (!EventProperties.Contains(property.Name) || !seenProperties.Add(property.Name))
            {
                throw new InvalidOperationException("event contains an unsupported or duplicate property");
            }
        }
        if (RequiredInt32(eventElement, "protocolVersion") != 1)
        {
            throw new InvalidOperationException("unsupported protocol version");
        }

        var meetingId = RequiredIdentifier(eventElement, "meetingId");
        var eventId = RequiredIdentifier(eventElement, "eventId");
        var sequence = RequiredPositiveUInt64(eventElement, "sequence");
        var runtimeGeneration = RequiredPositiveUInt64(eventElement, "runtimeGeneration");
        var kind = RequiredString(eventElement, "kind");
        if (kind.Length > 128 || !EventKindPattern().IsMatch(kind))
        {
            throw new InvalidOperationException("event kind is not namespaced");
        }
        var occurredAtValue = RequiredString(eventElement, "occurredAt");
        if (!TimestampPattern().IsMatch(occurredAtValue) ||
            !DateTimeOffset.TryParse(occurredAtValue, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var occurredAt))
        {
            throw new InvalidOperationException("occurredAt must be an RFC 3339 date-time");
        }
        var actorId = OptionalIdentifier(eventElement, "actorId");
        var targetId = OptionalIdentifier(eventElement, "targetId");
        var causationId = OptionalIdentifier(eventElement, "causationId");
        var visibility = RequiredString(eventElement, "visibility");
        if (visibility is not ("public" or "private"))
        {
            throw new InvalidOperationException("visibility must be public or private");
        }

        var hasAudience = eventElement.TryGetProperty("audience", out var audienceElement);
        if (visibility == "public" && hasAudience)
        {
            throw new InvalidOperationException("public events cannot carry an audience");
        }
        if (visibility == "private" && !hasAudience)
        {
            throw new InvalidOperationException("private events require an audience");
        }
        var audience = hasAudience ? ParseAudience(audienceElement) : [];
        if (visibility == "private" && audience.Length == 0)
        {
            throw new InvalidOperationException("private events require an audience");
        }

        var payload = eventElement.GetProperty("payload");
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("payload must be an object");
        }
        return new RuntimeMeetingEvent(
            meetingId,
            eventId,
            sequence,
            runtimeGeneration,
            kind,
            occurredAt,
            actorId,
            targetId,
            causationId,
            visibility,
            audience,
            payload.Clone());
    }

    private static string[] ParseAudience(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("audience must be an array");
        }
        var audience = new List<string>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } value ||
                !IdentifierPattern().IsMatch(value) || !unique.Add(value))
            {
                throw new InvalidOperationException("audience contains an invalid or duplicate principal");
            }
            audience.Add(value);
        }
        return [.. audience];
    }

    private static int RequiredInt32(JsonElement element, string name) =>
        element.GetProperty(name).GetInt32();

    private static ulong RequiredPositiveUInt64(JsonElement element, string name)
    {
        var value = element.GetProperty(name).GetUInt64();
        return value > 0 ? value : throw new InvalidOperationException($"{name} must be positive");
    }

    private static string RequiredString(JsonElement element, string name)
    {
        var property = element.GetProperty(name);
        return property.ValueKind == JsonValueKind.String && property.GetString() is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{name} must be a non-empty string");
    }

    private static string RequiredIdentifier(JsonElement element, string name)
    {
        var value = RequiredString(element, name);
        return IdentifierPattern().IsMatch(value)
            ? value
            : throw new InvalidOperationException($"{name} must be a protocol identifier");
    }

    private static string? OptionalIdentifier(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (property.ValueKind != JsonValueKind.String || property.GetString() is not { } value ||
            !IdentifierPattern().IsMatch(value))
        {
            throw new InvalidOperationException($"{name} must be null or a protocol identifier");
        }
        return value;
    }
}
