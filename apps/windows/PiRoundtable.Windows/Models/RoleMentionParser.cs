namespace PiRoundtable.Windows.Models;

internal sealed record RoleMentionParseResult(
    IReadOnlyList<string> RoleIds,
    IReadOnlyList<string> UnknownMentions,
    IReadOnlyList<string> AmbiguousMentions)
{
    public bool IsValid => UnknownMentions.Count == 0 && AmbiguousMentions.Count == 0;
}

internal static class RoleMentionParser
{
    public static RoleMentionParseResult Parse(
        string message,
        IEnumerable<RoleItem> roles)
    {
        var activeRoles = roles
            .Where(role => !role.IsArchived && !string.IsNullOrWhiteSpace(role.DisplayName))
            .ToArray();
        var roleGroups = activeRoles
            .GroupBy(role => role.DisplayName.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Key.Length)
            .ToArray();
        var roleIds = new List<string>();
        var seenRoleIds = new HashSet<string>(StringComparer.Ordinal);
        var unknown = new List<string>();
        var ambiguous = new List<string>();
        var source = message ?? string.Empty;
        var inFence = false;
        var fenceCharacter = '\0';
        var fenceLength = 0;
        var inlineDelimiterLength = 0;

        for (var index = 0; index < source.Length; index++)
        {
            if (IsLineStart(source, index) && TryReadIndentedFence(
                    source,
                    index,
                    out var fenceIndex,
                    out var currentFence,
                    out var currentLength))
            {
                if (!inFence)
                {
                    inFence = true;
                    fenceCharacter = currentFence;
                    fenceLength = currentLength;
                }
                else if (currentFence == fenceCharacter && currentLength >= fenceLength)
                {
                    inFence = false;
                }
                index = fenceIndex + currentLength - 1;
                continue;
            }
            if (inFence)
            {
                continue;
            }
            if (source[index] == '`')
            {
                var delimiterLength = CountRun(source, index, '`');
                inlineDelimiterLength = inlineDelimiterLength == 0
                    ? delimiterLength
                    : delimiterLength == inlineDelimiterLength ? 0 : inlineDelimiterLength;
                index += delimiterLength - 1;
                continue;
            }
            if (inlineDelimiterLength > 0 || !IsAtSign(source[index]) || !IsMentionStartBoundary(source, index))
            {
                continue;
            }

            var nameStart = index + 1;
            var matchingGroup = roleGroups.FirstOrDefault(group =>
                source.AsSpan(nameStart).StartsWith(group.Key, StringComparison.OrdinalIgnoreCase) &&
                IsMentionEndBoundary(source, nameStart + group.Key.Length));
            if (matchingGroup is not null)
            {
                if (matchingGroup.Count() == 1)
                {
                    var roleId = matchingGroup.Single().RoleId;
                    if (seenRoleIds.Add(roleId))
                    {
                        roleIds.Add(roleId);
                    }
                }
                else
                {
                    AddDistinct(ambiguous, matchingGroup.Key);
                }
                index = nameStart + matchingGroup.Key.Length - 1;
                continue;
            }

            var unknownEnd = nameStart;
            while (unknownEnd < source.Length && !IsUnknownMentionTerminator(source[unknownEnd]))
            {
                unknownEnd++;
            }
            var label = source[nameStart..unknownEnd].Trim();
            // A standalone @ is ordinary prose/Markdown punctuation, not a role mention.
            // Only reject a mention-shaped token that actually has a label.
            if (label.Length > 0)
            {
                AddDistinct(unknown, label);
            }
            index = Math.Max(index, unknownEnd - 1);
        }

        return new RoleMentionParseResult(roleIds, unknown, ambiguous);
    }

    private static bool IsAtSign(char value) => value is '@' or '＠';

    private static bool IsMentionStartBoundary(string source, int index) =>
        index == 0 || !(char.IsLetterOrDigit(source[index - 1]) || source[index - 1] == '_');

    private static bool IsMentionEndBoundary(string source, int index) =>
        index >= source.Length || !IsMentionNameContinuation(source[index]);

    private static bool IsMentionNameContinuation(char value) =>
        char.IsLetterOrDigit(value) || value is '_' or '-';

    private static bool IsUnknownMentionTerminator(char value) =>
        char.IsWhiteSpace(value) ||
        ((char.IsPunctuation(value) || char.IsSymbol(value)) && value is not ('_' or '-'));

    private static bool IsLineStart(string source, int index) =>
        index == 0 || source[index - 1] is '\r' or '\n';

    private static bool TryReadFence(string source, int index, out char fenceCharacter, out int length)
    {
        fenceCharacter = source[index];
        length = 0;
        if (fenceCharacter is not ('`' or '~'))
        {
            return false;
        }
        length = CountRun(source, index, fenceCharacter);
        return length >= 3;
    }

    private static bool TryReadIndentedFence(
        string source,
        int lineStart,
        out int fenceIndex,
        out char fenceCharacter,
        out int length)
    {
        fenceIndex = lineStart;
        var spaces = 0;
        while (fenceIndex < source.Length && spaces < 3 && source[fenceIndex] == ' ')
        {
            fenceIndex++;
            spaces++;
        }
        if (fenceIndex >= source.Length)
        {
            fenceCharacter = '\0';
            length = 0;
            return false;
        }
        return TryReadFence(source, fenceIndex, out fenceCharacter, out length);
    }

    private static int CountRun(string source, int index, char value)
    {
        var end = index;
        while (end < source.Length && source[end] == value)
        {
            end++;
        }
        return end - index;
    }

    private static void AddDistinct(ICollection<string> values, string value)
    {
        if (!values.Contains(value, StringComparer.OrdinalIgnoreCase))
        {
            values.Add(value);
        }
    }
}
