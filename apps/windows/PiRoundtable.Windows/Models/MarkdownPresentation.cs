using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace PiRoundtable.Windows.Models;

internal enum MarkdownBlockKind
{
    Paragraph,
    Heading,
    ListItem,
    Quote,
    Code,
    Math,
    Table,
    ThematicBreak,
}

internal sealed record MarkdownInlinePresentation(
    string Text,
    bool Bold = false,
    bool Italic = false,
    bool Strikethrough = false,
    bool Code = false,
    bool Math = false,
    string? SafeUrl = null,
    bool IsLineBreak = false);

internal sealed record MarkdownTableRowPresentation(
    bool IsHeader,
    IReadOnlyList<IReadOnlyList<MarkdownInlinePresentation>> Cells);

internal sealed record MarkdownBlockPresentation(
    MarkdownBlockKind Kind,
    IReadOnlyList<MarkdownInlinePresentation> Inlines,
    string? Text = null,
    int Level = 0,
    string? Marker = null,
    string? Language = null,
    IReadOnlyList<MarkdownTableRowPresentation>? Rows = null);

internal static class SafeMarkdownParser
{
    internal const int MaxSourceCharacters = 131_072;
    internal const int MaxBlocks = 512;
    internal const int MaxTableRows = 50;
    internal const int MaxTableColumns = 12;
    private const string TruncationNotice = "内容过长，已为保持界面响应而截断显示。";

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .UseMathematics()
        .Build();

    public static IReadOnlyList<MarkdownBlockPresentation> Parse(string? markdown)
    {
        var source = markdown ?? string.Empty;
        if (source.Length == 0)
        {
            return [];
        }

        var wasTruncated = source.Length > MaxSourceCharacters;
        if (wasTruncated)
        {
            source = source[..MaxSourceCharacters];
        }

        var document = Markdown.Parse(source, Pipeline);
        var result = new List<MarkdownBlockPresentation>();
        foreach (var block in document)
        {
            if (result.Count >= MaxBlocks)
            {
                wasTruncated = true;
                break;
            }
            AppendBlock(result, block, source, 0);
        }
        if (wasTruncated)
        {
            if (result.Count >= MaxBlocks)
            {
                result.RemoveAt(result.Count - 1);
            }
            result.Add(new MarkdownBlockPresentation(
                MarkdownBlockKind.Quote,
                [new MarkdownInlinePresentation(TruncationNotice)]));
        }
        return result;
    }

    public static bool TryNormalizeLink(string? value, out string? safeUrl)
    {
        safeUrl = null;
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http"))
        {
            return false;
        }
        safeUrl = uri.AbsoluteUri;
        return true;
    }

    private static void AppendBlock(
        ICollection<MarkdownBlockPresentation> output,
        Block block,
        string source,
        int listLevel)
    {
        switch (block)
        {
            case Table table:
                var rows = ParseTable(table, out var tableTruncated);
                output.Add(new MarkdownBlockPresentation(
                    MarkdownBlockKind.Table,
                    [],
                    Rows: rows));
                if (tableTruncated && output.Count < MaxBlocks)
                {
                    output.Add(new MarkdownBlockPresentation(
                        MarkdownBlockKind.Quote,
                        [new MarkdownInlinePresentation("表格过大，仅显示前 50 行、12 列。")]));
                }
                return;
            case MathBlock math:
                output.Add(new MarkdownBlockPresentation(
                    MarkdownBlockKind.Math,
                    [],
                    math.Lines.ToString().Trim()));
                return;
            case FencedCodeBlock fenced:
                output.Add(new MarkdownBlockPresentation(
                    MarkdownBlockKind.Code,
                    [],
                    fenced.Lines.ToString(),
                    Language: fenced.Info?.Trim()));
                return;
            case CodeBlock code:
                output.Add(new MarkdownBlockPresentation(
                    MarkdownBlockKind.Code,
                    [],
                    code.Lines.ToString()));
                return;
            case HeadingBlock heading:
                output.Add(new MarkdownBlockPresentation(
                    MarkdownBlockKind.Heading,
                    ParseInlines(heading.Inline),
                    Level: heading.Level));
                return;
            case ParagraphBlock paragraph:
                output.Add(new MarkdownBlockPresentation(
                    MarkdownBlockKind.Paragraph,
                    ParseInlines(paragraph.Inline)));
                return;
            case QuoteBlock quote:
                output.Add(new MarkdownBlockPresentation(
                    MarkdownBlockKind.Quote,
                    ParseContainerInlines(quote)));
                return;
            case ListBlock list:
                AppendList(output, list, source, listLevel);
                return;
            case ThematicBreakBlock:
                output.Add(new MarkdownBlockPresentation(MarkdownBlockKind.ThematicBreak, []));
                return;
            default:
                var fallback = SliceSource(block, source);
                if (!string.IsNullOrWhiteSpace(fallback))
                {
                    output.Add(new MarkdownBlockPresentation(
                        MarkdownBlockKind.Paragraph,
                        [new MarkdownInlinePresentation(fallback)]));
                }
                return;
        }
    }

    private static void AppendList(
        ICollection<MarkdownBlockPresentation> output,
        ListBlock list,
        string source,
        int level)
    {
        var fallbackOrder = 1;
        foreach (var item in list.OfType<ListItemBlock>())
        {
            if (output.Count >= MaxBlocks)
            {
                return;
            }
            var marker = TryGetTaskState(item, out var isChecked)
                ? isChecked ? "☑" : "☐"
                : list.IsOrdered
                ? $"{(item.Order > 0 ? item.Order : fallbackOrder)}."
                : "•";
            var inlines = ParseDirectItemInlines(item);
            if (inlines.Count > 0)
            {
                output.Add(new MarkdownBlockPresentation(
                    MarkdownBlockKind.ListItem,
                    inlines,
                    Level: level,
                    Marker: marker));
            }
            foreach (var nested in item.OfType<ListBlock>())
            {
                AppendList(output, nested, source, level + 1);
            }
            fallbackOrder++;
        }
    }

    private static IReadOnlyList<MarkdownInlinePresentation> ParseDirectItemInlines(ListItemBlock item)
    {
        var result = new List<MarkdownInlinePresentation>();
        foreach (var child in item)
        {
            if (child is not LeafBlock { Inline: not null } leaf)
            {
                continue;
            }
            if (result.Count > 0)
            {
                result.Add(new MarkdownInlinePresentation(string.Empty, IsLineBreak: true));
            }
            AppendInlines(result, leaf.Inline.FirstChild, false, false, false, null);
        }
        return result;
    }

    private static IReadOnlyList<MarkdownInlinePresentation> ParseContainerInlines(ContainerBlock container)
    {
        var result = new List<MarkdownInlinePresentation>();
        foreach (var child in container)
        {
            if (child is LeafBlock { Inline: not null } leaf)
            {
                if (result.Count > 0)
                {
                    result.Add(new MarkdownInlinePresentation(string.Empty, IsLineBreak: true));
                }
                AppendInlines(result, leaf.Inline.FirstChild, false, false, false, null);
            }
            else if (child is ContainerBlock nested)
            {
                var nestedInlines = ParseContainerInlines(nested);
                if (nestedInlines.Count > 0 && result.Count > 0)
                {
                    result.Add(new MarkdownInlinePresentation(string.Empty, IsLineBreak: true));
                }
                result.AddRange(nestedInlines);
            }
        }
        return result;
    }

    private static IReadOnlyList<MarkdownInlinePresentation> ParseInlines(ContainerInline? container)
    {
        var result = new List<MarkdownInlinePresentation>();
        AppendInlines(result, container?.FirstChild, false, false, false, null);
        return result;
    }

    private static void AppendInlines(
        ICollection<MarkdownInlinePresentation> output,
        Inline? current,
        bool bold,
        bool italic,
        bool strikethrough,
        string? linkUrl)
    {
        while (current is not null)
        {
            switch (current)
            {
                case LiteralInline literal:
                    output.Add(new MarkdownInlinePresentation(
                        literal.Content.ToString(),
                        bold,
                        italic,
                        strikethrough,
                        SafeUrl: linkUrl));
                    break;
                case CodeInline code:
                    output.Add(new MarkdownInlinePresentation(
                        code.Content,
                        bold,
                        italic,
                        strikethrough,
                        Code: true,
                        SafeUrl: linkUrl));
                    break;
                case MathInline math:
                    output.Add(new MarkdownInlinePresentation(
                        math.Content.ToString(),
                        bold,
                        italic,
                        strikethrough,
                        Math: true,
                        SafeUrl: linkUrl));
                    break;
                case LineBreakInline:
                    output.Add(new MarkdownInlinePresentation(string.Empty, IsLineBreak: true));
                    break;
                case EmphasisInline emphasis:
                    AppendInlines(
                        output,
                        emphasis.FirstChild,
                        bold || (emphasis.DelimiterChar != '~' && emphasis.DelimiterCount >= 2),
                        italic || (emphasis.DelimiterChar != '~' && emphasis.DelimiterCount == 1),
                        strikethrough || emphasis.DelimiterChar == '~',
                        linkUrl);
                    break;
                case LinkInline link when !link.IsImage:
                    _ = TryNormalizeLink(link.Url, out var safeUrl);
                    AppendInlines(output, link.FirstChild, bold, italic, strikethrough, safeUrl);
                    break;
                case LinkInline image:
                    AppendInlines(output, image.FirstChild, bold, italic, strikethrough, null);
                    output.Add(new MarkdownInlinePresentation(" [image hidden]", Italic: true));
                    break;
                case TaskList:
                    break;
                case ContainerInline nested:
                    AppendInlines(output, nested.FirstChild, bold, italic, strikethrough, linkUrl);
                    break;
            }
            current = current.NextSibling;
        }
    }

    private static bool TryGetTaskState(ListItemBlock item, out bool isChecked)
    {
        foreach (var leaf in item.OfType<LeafBlock>())
        {
            for (var inline = leaf.Inline?.FirstChild; inline is not null; inline = inline.NextSibling)
            {
                if (inline is TaskList task)
                {
                    isChecked = task.Checked;
                    return true;
                }
            }
        }
        isChecked = false;
        return false;
    }

    private static IReadOnlyList<MarkdownTableRowPresentation> ParseTable(Table table, out bool truncated)
    {
        var rows = new List<MarkdownTableRowPresentation>();
        var sourceRows = table.OfType<TableRow>().ToArray();
        truncated = sourceRows.Length > MaxTableRows ||
            sourceRows.Any(row => row.OfType<TableCell>().Skip(MaxTableColumns).Any());
        foreach (var row in sourceRows.Take(MaxTableRows))
        {
            var cells = row
                .OfType<TableCell>()
                .Take(MaxTableColumns)
                .Select(cell => ParseContainerInlines(cell))
                .ToArray();
            rows.Add(new MarkdownTableRowPresentation(row.IsHeader, cells));
        }
        return rows;
    }

    private static string SliceSource(Block block, string source)
    {
        if (block.Span.Start < 0 || block.Span.End < block.Span.Start || block.Span.Start >= source.Length)
        {
            return string.Empty;
        }
        var length = Math.Min(block.Span.End - block.Span.Start + 1, source.Length - block.Span.Start);
        return source.Substring(block.Span.Start, length);
    }
}
