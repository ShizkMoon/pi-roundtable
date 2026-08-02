using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PiRoundtable.Windows.Models;

namespace PiRoundtable.Windows.Services;

internal static partial class SessionTransferService
{
    public const int MaximumPackageBytes = 32 * 1024 * 1024;
    public const int MaximumMessages = 10_000;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        MaxDepth = 16,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
    };

    public static SessionExportPackage CreatePackage(
        SessionItem session,
        bool includePrivateMessages,
        DateTimeOffset? exportedAt = null)
    {
        ArgumentNullException.ThrowIfNull(session);
        var messages = session.Transcript
            .Concat(includePrivateMessages
                ? session.PrivateThreads.Values.SelectMany(thread => thread)
                : [])
            .OrderBy(message => message.OccurredAt)
            .ThenBy(message => message.MessageId, StringComparer.Ordinal)
            .Select(message => new SessionExportMessage
            {
                MessageId = message.MessageId,
                Kind = message.Kind,
                SpeakerId = message.RoleId,
                SpeakerName = message.Speaker,
                Visibility = message.Visibility,
                AudienceRoleIds = message.Visibility == "private"
                    ? message.AudienceRoleIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList()
                    : [],
                Text = message.Text,
                State = ToExportState(message.State),
                OccurredAt = message.OccurredAt,
            })
            .ToList();
        return new SessionExportPackage
        {
            SourceSessionId = session.SessionId,
            Title = string.IsNullOrWhiteSpace(session.Title) ? "未命名圆桌会议" : session.Title.Trim(),
            ExportedAt = exportedAt ?? DateTimeOffset.UtcNow,
            IncludesPrivateMessages = includePrivateMessages,
            Messages = messages,
        };
    }

    public static byte[] SerializeJson(SessionExportPackage package)
    {
        return JsonSerializer.SerializeToUtf8Bytes(package, SerializerOptions);
    }

    public static string RenderMarkdown(SessionExportPackage package)
    {
        var builder = new StringBuilder();
        builder.Append("# ").AppendLine(EscapeHeading(package.Title));
        builder.AppendLine();
        builder.Append("- 导出时间：").AppendLine(package.ExportedAt.ToString("O"));
        builder.Append("- 来源会话：`").Append(EscapeInlineCode(package.SourceSessionId)).AppendLine("`");
        builder.Append("- 内容范围：").AppendLine(package.IncludesPrivateMessages ? "公开消息与明确包含的私聊" : "仅公开消息");
        foreach (var message in package.Messages)
        {
            builder.AppendLine();
            builder.Append("## ").Append(EscapeHeading(message.SpeakerName)).Append(" · ")
                .AppendLine(message.OccurredAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            if (message.Visibility == "private")
            {
                builder.Append("> 私聊 audience：")
                    .AppendLine(string.Join("、", message.AudienceRoleIds.Select(value => $"`{EscapeInlineCode(value)}`")));
                builder.AppendLine();
            }
            builder.AppendLine(message.Text.Replace("\0", "�", StringComparison.Ordinal));
        }
        return builder.ToString();
    }

    public static SessionImportPreflight Preflight(ReadOnlySpan<byte> json)
    {
        if (json.IsEmpty || json.Length > MaximumPackageBytes)
        {
            throw new InvalidDataException("会话包为空或超过 32 MiB 限制。");
        }
        RejectDuplicateProperties(json);
        var package = JsonSerializer.Deserialize<SessionExportPackage>(json, SerializerOptions)
            ?? throw new InvalidDataException("会话包无法解析。");
        ValidatePackage(package);
        var ordered = package.Messages.OrderBy(message => message.OccurredAt).ToArray();
        return new SessionImportPreflight(
            package,
            package.Messages.Count(message => message.Visibility == "public"),
            package.Messages.Count(message => message.Visibility == "private"),
            package.Messages.Select(message => message.SpeakerId).Distinct(StringComparer.Ordinal).Count(),
            ordered.FirstOrDefault()?.OccurredAt,
            ordered.LastOrDefault()?.OccurredAt);
    }

    private static void ValidatePackage(SessionExportPackage package)
    {
        if (package.PackageVersion != 1 || package.ProtocolVersion != 1)
        {
            throw new InvalidDataException("会话包版本不受支持。");
        }
        ValidateId(package.SourceSessionId, "sourceSessionId");
        ValidateText(package.Title, 1, 256, "title");
        if (package.ExportedAt == default || package.ExportedAt > DateTimeOffset.UtcNow.AddMinutes(10))
        {
            throw new InvalidDataException("会话包导出时间无效。");
        }
        if (package.Messages.Count > MaximumMessages)
        {
            throw new InvalidDataException("会话包消息数超过 10000 条限制。");
        }

        var messageIds = new HashSet<string>(StringComparer.Ordinal);
        DateTimeOffset? previous = null;
        foreach (var (message, index) in package.Messages.Select((value, index) => (value, index)))
        {
            ValidateId(message.MessageId, $"messages[{index}].messageId");
            ValidateId(message.SpeakerId, $"messages[{index}].speakerId");
            ValidateText(message.SpeakerName, 1, 128, $"messages[{index}].speakerName");
            ValidateText(message.Text, 0, 1_048_576, $"messages[{index}].text");
            if (!messageIds.Add(message.MessageId))
            {
                throw new InvalidDataException($"会话包包含重复消息 ID：{message.MessageId}。");
            }
            if (message.Kind is not ("host" or "role" or "system") ||
                message.State is not ("submitted" or "streaming" or "completed" or "cancelled") ||
                message.Visibility is not ("public" or "private") ||
                message.OccurredAt == default)
            {
                throw new InvalidDataException($"会话包消息 {index} 的枚举或时间字段无效。");
            }
            if (previous is not null && message.OccurredAt < previous)
            {
                throw new InvalidDataException("会话包消息必须按时间非递减排列。");
            }
            previous = message.OccurredAt;
            var audience = message.AudienceRoleIds.Distinct(StringComparer.Ordinal).ToArray();
            if (audience.Length != message.AudienceRoleIds.Count || audience.Any(value => !IdRegex().IsMatch(value)))
            {
                throw new InvalidDataException($"会话包消息 {index} 的 audience 无效或重复。");
            }
            if ((message.Visibility == "public" && audience.Length != 0) ||
                (message.Visibility == "private" && audience.Length == 0) ||
                (message.Visibility == "private" && !package.IncludesPrivateMessages))
            {
                throw new InvalidDataException($"会话包消息 {index} 的 visibility 与 audience/内容范围不一致。");
            }
        }
    }

    private static void ValidateId(string value, string field)
    {
        if (!IdRegex().IsMatch(value))
        {
            throw new InvalidDataException($"会话包字段 {field} 不是有效 ID。");
        }
    }

    private static void ValidateText(string? value, int minimum, int maximum, string field)
    {
        if (value is null || value.Length < minimum || value.Length > maximum || value.Contains('\0'))
        {
            throw new InvalidDataException($"会话包字段 {field} 长度或内容无效。");
        }
    }

    private static void RejectDuplicateProperties(ReadOnlySpan<byte> json)
    {
        using var parsed = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16,
        });
        Visit(parsed.RootElement);

        static void Visit(JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.Object)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (!names.Add(property.Name))
                    {
                        throw new InvalidDataException($"会话包包含重复字段：{property.Name}。");
                    }
                    Visit(property.Value);
                }
            }
            else if (element.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in element.EnumerateArray())
                {
                    Visit(item);
                }
            }
        }
    }

    private static string ToExportState(string state)
    {
        return state switch
        {
            "已发送" => "submitted",
            "生成中" => "streaming",
            "已完成" => "completed",
            _ when state.StartsWith("已取消", StringComparison.Ordinal) ||
                state.StartsWith("失败", StringComparison.Ordinal) => "cancelled",
            _ => "completed",
        };
    }

    private static string EscapeHeading(string value) => value.Replace("#", "\\#", StringComparison.Ordinal).Replace("\r", " ").Replace("\n", " ");

    private static string EscapeInlineCode(string value) => value.Replace("`", "'", StringComparison.Ordinal);

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex IdRegex();
}
