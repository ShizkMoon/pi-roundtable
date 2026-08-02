using PiRoundtable.Windows.Models;

namespace PiRoundtable.Windows.Tests;

[TestClass]
public sealed class SafeMarkdownParserTests
{
    [TestMethod]
    public void Parses_native_markdown_blocks_and_inline_emphasis()
    {
        const string markdown = """
            # Decision

            **Bold** and *italic* with `code` and $x^2$.

            - first
            - second

            > quoted evidence

            ```csharp
            Console.WriteLine("safe");
            ```
            """;

        var blocks = SafeMarkdownParser.Parse(markdown);

        Assert.IsTrue(blocks.Any(block => block.Kind == MarkdownBlockKind.Heading));
        Assert.AreEqual(2, blocks.Count(block => block.Kind == MarkdownBlockKind.ListItem));
        Assert.IsTrue(blocks.Any(block => block.Kind == MarkdownBlockKind.Quote));
        Assert.IsTrue(blocks.Any(block => block.Kind == MarkdownBlockKind.Code));
        var paragraph = blocks.Single(block => block.Kind == MarkdownBlockKind.Paragraph);
        Assert.IsTrue(paragraph.Inlines.Any(inline => inline.Text == "Bold" && inline.Bold));
        Assert.IsTrue(paragraph.Inlines.Any(inline => inline.Text == "italic" && inline.Italic));
        Assert.IsTrue(paragraph.Inlines.Any(inline => inline.Text == "code" && inline.Code));
        Assert.IsTrue(paragraph.Inlines.Any(inline => inline.Text == "x^2" && inline.Math));
    }

    [TestMethod]
    public void Keeps_unsafe_links_inert_and_preserves_safe_https_links()
    {
        const string markdown = "[safe](https://example.com/a) [script](javascript:alert(1)) [file](file:///c:/secret)";

        var inlines = SafeMarkdownParser.Parse(markdown).Single().Inlines;

        Assert.AreEqual("https://example.com/a", inlines.Single(inline => inline.Text == "safe").SafeUrl);
        Assert.IsNull(inlines.Single(inline => inline.Text == "script").SafeUrl);
        Assert.IsNull(inlines.Single(inline => inline.Text == "file").SafeUrl);
    }

    [TestMethod]
    public void Disables_executable_html_and_recognizes_math_blocks()
    {
        const string markdown = """
            <script>alert('not executed')</script>

            $$
            E = mc^2
            $$
            """;

        var blocks = SafeMarkdownParser.Parse(markdown);

        Assert.IsTrue(blocks.SelectMany(block => block.Inlines).Any(inline => inline.Text.Contains("script")));
        Assert.IsTrue(blocks.Any(block => block.Kind == MarkdownBlockKind.Math && block.Text!.Contains("E = mc^2")));
    }

    [TestMethod]
    public void Parses_tables_task_lists_strikethrough_and_code_languages()
    {
        const string markdown = """
            | Item | Status |
            | --- | --- |
            | Router | **ready** |

            - [x] verified
            - [ ] pending

            ~~obsolete~~

            ```powershell
            Get-Process
            ```
            """;

        var blocks = SafeMarkdownParser.Parse(markdown);

        var table = blocks.Single(block => block.Kind == MarkdownBlockKind.Table);
        Assert.AreEqual(2, table.Rows?.Count);
        Assert.IsTrue(table.Rows![0].IsHeader);
        Assert.AreEqual("Item", table.Rows[0].Cells[0].Single().Text);
        Assert.IsTrue(table.Rows[1].Cells[1].Single(inline => inline.Text == "ready").Bold);
        CollectionAssert.AreEqual(
            new[] { "☑", "☐" },
            blocks.Where(block => block.Kind == MarkdownBlockKind.ListItem)
                .Select(block => block.Marker)
                .ToArray());
        Assert.IsTrue(blocks.SelectMany(block => block.Inlines)
            .Any(inline => inline.Text == "obsolete" && inline.Strikethrough));
        Assert.AreEqual(
            "powershell",
            blocks.Single(block => block.Kind == MarkdownBlockKind.Code).Language);
    }

    [TestMethod]
    public void Rejects_obfuscated_non_http_links()
    {
        Assert.IsFalse(SafeMarkdownParser.TryNormalizeLink(" JAVASCRIPT:alert(1)", out _));
        Assert.IsFalse(SafeMarkdownParser.TryNormalizeLink("data:text/html,boom", out _));
        Assert.IsFalse(SafeMarkdownParser.TryNormalizeLink("file:///c:/secret", out _));
        Assert.IsTrue(SafeMarkdownParser.TryNormalizeLink("HTTPS://example.com/path", out var safe));
        Assert.AreEqual("https://example.com/path", safe);
    }

    [TestMethod]
    public void Bounds_oversized_markdown_and_surfaces_a_truncation_notice()
    {
        var markdown = new string('a', SafeMarkdownParser.MaxSourceCharacters + 100);

        var blocks = SafeMarkdownParser.Parse(markdown);

        Assert.IsLessThanOrEqualTo(SafeMarkdownParser.MaxBlocks, blocks.Count);
        Assert.IsTrue(blocks[^1].Inlines.Any(inline => inline.Text.Contains("截断显示", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Bounds_large_tables_and_surfaces_the_visible_limits()
    {
        var header = "| " + string.Join(" | ", Enumerable.Range(1, 14).Select(index => $"C{index}")) + " |";
        var divider = "| " + string.Join(" | ", Enumerable.Repeat("---", 14)) + " |";
        var rows = Enumerable.Range(1, 60)
            .Select(row => "| " + string.Join(" | ", Enumerable.Range(1, 14).Select(column => $"{row}:{column}")) + " |");
        var markdown = string.Join("\n", new[] { header, divider }.Concat(rows));

        var blocks = SafeMarkdownParser.Parse(markdown);
        var table = blocks.Single(block => block.Kind == MarkdownBlockKind.Table);
        var parsedRows = table.Rows ?? throw new AssertFailedException("Expected parsed table rows.");

        Assert.HasCount(SafeMarkdownParser.MaxTableRows, parsedRows);
        Assert.IsTrue(parsedRows.All(row => row.Cells.Count <= SafeMarkdownParser.MaxTableColumns));
        Assert.IsTrue(blocks.Any(block => block.Inlines.Any(inline => inline.Text.Contains("50 行、12 列", StringComparison.Ordinal))));
    }
}
