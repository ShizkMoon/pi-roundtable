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
}
