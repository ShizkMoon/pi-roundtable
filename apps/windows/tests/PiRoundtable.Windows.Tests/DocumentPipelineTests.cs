using System.IO.Compression;
using System.Text;
using PiRoundtable.Windows.Models;
using PiRoundtable.Windows.Services;

namespace PiRoundtable.Windows.Tests;

[TestClass]
public sealed class DocumentPipelineTests
{
    [TestMethod]
    public async Task Reads_markdown_and_validates_pdf_signature_without_executing_content()
    {
        var root = TestRoot();
        try
        {
            Directory.CreateDirectory(root);
            var markdownPath = Path.Combine(root, "notes.md");
            await File.WriteAllTextAsync(markdownPath, "# Evidence\n\n$x^2$", new UTF8Encoding(false));
            var markdown = await new DocumentPipeline().PreflightAsync(markdownPath);
            Assert.AreEqual(DocumentArtifactFormat.Markdown, markdown.Descriptor.Format);
            Assert.AreEqual(DocumentArtifactSupport.SourceText, markdown.Descriptor.Support);
            Assert.AreEqual("# Evidence\n\n$x^2$", markdown.NormalizedText);
            Assert.AreEqual(64, markdown.Descriptor.ArtifactId.Length);

            var pdfPath = Path.Combine(root, "paper.pdf");
            await File.WriteAllBytesAsync(pdfPath, "%PDF-1.7\n%%EOF"u8.ToArray());
            var pdf = await new DocumentPipeline().PreflightAsync(pdfPath);
            Assert.AreEqual(DocumentArtifactSupport.MetadataOnly, pdf.Descriptor.Support);
            Assert.IsNull(pdf.NormalizedText);
            Assert.HasCount(1, pdf.Descriptor.Warnings);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Extracts_drawio_labels_and_prohibits_xml_entities()
    {
        var root = TestRoot();
        try
        {
            Directory.CreateDirectory(root);
            var drawIoPath = Path.Combine(root, "flow.drawio");
            await File.WriteAllTextAsync(
                drawIoPath,
                "<mxfile><diagram><mxGraphModel><root><mxCell value=\"Start &amp; verify\"/></root></mxGraphModel></diagram></mxfile>",
                new UTF8Encoding(false));
            var artifact = await new DocumentPipeline().PreflightAsync(drawIoPath);
            Assert.AreEqual("Start & verify", artifact.NormalizedText);

            await File.WriteAllTextAsync(
                drawIoPath,
                "<!DOCTYPE mxfile [<!ENTITY xxe SYSTEM 'file:///c:/secret'>]><mxfile><mxCell value=\"&xxe;\"/></mxfile>",
                new UTF8Encoding(false));
            await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => new DocumentPipeline().PreflightAsync(drawIoPath));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Extracts_text_from_minimal_docx_pptx_and_xlsx_packages()
    {
        var root = TestRoot();
        try
        {
            Directory.CreateDirectory(root);
            var pipeline = new DocumentPipeline();
            var docx = Path.Combine(root, "document.docx");
            await WritePackageAsync(docx, new Dictionary<string, string>
            {
                ["[Content_Types].xml"] = "<Types/>",
                ["word/document.xml"] = "<w:document xmlns:w=\"urn:w\"><w:p><w:r><w:t>Decision</w:t></w:r></w:p></w:document>",
            });
            StringAssert.Contains((await pipeline.PreflightAsync(docx)).NormalizedText!, "Decision");

            var pptx = Path.Combine(root, "deck.pptx");
            await WritePackageAsync(pptx, new Dictionary<string, string>
            {
                ["[Content_Types].xml"] = "<Types/>",
                ["ppt/slides/slide1.xml"] = "<p:sld xmlns:p=\"urn:p\" xmlns:a=\"urn:a\"><a:t>Finding</a:t></p:sld>",
            });
            StringAssert.Contains((await pipeline.PreflightAsync(pptx)).NormalizedText!, "Finding");

            var xlsx = Path.Combine(root, "table.xlsx");
            await WritePackageAsync(xlsx, new Dictionary<string, string>
            {
                ["[Content_Types].xml"] = "<Types/>",
                ["xl/workbook.xml"] = "<workbook/>",
                ["xl/sharedStrings.xml"] = "<sst><si><t>Metric</t></si></sst>",
                ["xl/worksheets/sheet1.xml"] = "<worksheet><sheetData><row><c t=\"s\"><v>0</v></c><c><v>42</v></c></row></sheetData></worksheet>",
            });
            StringAssert.Contains((await pipeline.PreflightAsync(xlsx)).NormalizedText!, "Metric\t42");
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    [TestMethod]
    public async Task Rejects_extension_magic_mismatch_macros_and_package_traversal()
    {
        var root = TestRoot();
        try
        {
            Directory.CreateDirectory(root);
            var pipeline = new DocumentPipeline();
            var fakeOffice = Path.Combine(root, "fake.docx");
            await File.WriteAllTextAsync(fakeOffice, "not zip");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => pipeline.PreflightAsync(fakeOffice));

            var macro = Path.Combine(root, "macro.docm");
            await File.WriteAllTextAsync(macro, "not executed");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => pipeline.PreflightAsync(macro));

            var traversal = Path.Combine(root, "traversal.docx");
            await WritePackageAsync(traversal, new Dictionary<string, string>
            {
                ["[Content_Types].xml"] = "<Types/>",
                ["word/document.xml"] = "<document/>",
                ["../escape.xml"] = "<escape/>",
            });
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => pipeline.PreflightAsync(traversal));
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    private static async Task WritePackageAsync(string path, IReadOnlyDictionary<string, string> entries)
    {
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var (name, content) in entries)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Fastest);
            await using var stream = entry.Open();
            await stream.WriteAsync(Encoding.UTF8.GetBytes(content));
        }
    }

    private static string TestRoot() =>
        Path.Combine(Path.GetTempPath(), "PiRoundtable.Tests", Guid.NewGuid().ToString("N"));

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
