using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using PiRoundtable.Windows.Models;

namespace PiRoundtable.Windows.Services;

internal interface IDocumentPipeline
{
    Task<DocumentArtifactPreflight> PreflightAsync(
        string path,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Converts untrusted local documents into a bounded normalized artifact. This
/// layer never executes document content, invokes Office, compiles TeX, follows
/// links, or exposes an unchecked path to the model runtime.
/// </summary>
internal sealed class DocumentPipeline : IDocumentPipeline
{
    internal const long MaximumInputBytes = 32L * 1024 * 1024;
    internal const long MaximumExpandedPackageBytes = 128L * 1024 * 1024;
    internal const int MaximumPackageEntries = 2_048;
    internal const int MaximumExtractedCharacters = 400_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<DocumentArtifactPreflight> PreflightAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var file = new FileInfo(fullPath);
        if (!file.Exists)
        {
            throw new FileNotFoundException("找不到要导入的文档。", fullPath);
        }
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("不允许通过符号链接或重解析点导入文档。");
        }
        if (file.Length is < 1 or > MaximumInputBytes)
        {
            throw new InvalidDataException($"文档大小必须位于 1 字节到 {MaximumInputBytes} 字节之间。");
        }

        var format = ResolveFormat(file.Extension);
        byte[] bytes;
        await using (var stream = new FileStream(
                         fullPath,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            var openedAttributes = File.GetAttributes(fullPath);
            if ((openedAttributes & FileAttributes.ReparsePoint) != 0 ||
                stream.Length is < 1 or > MaximumInputBytes)
            {
                throw new InvalidDataException("打开后的文档属性或大小不符合安全限制。");
            }
            bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
        }

        var warnings = new List<string>();
        var normalizedText = format switch
        {
            DocumentArtifactFormat.Markdown => ReadBoundedUtf8(bytes, warnings),
            DocumentArtifactFormat.Latex => ReadBoundedUtf8(bytes, warnings),
            DocumentArtifactFormat.DrawIo => ExtractDrawIoText(bytes, warnings),
            DocumentArtifactFormat.WordOpenXml => ExtractOfficeText(bytes, format, warnings),
            DocumentArtifactFormat.ExcelOpenXml => ExtractOfficeText(bytes, format, warnings),
            DocumentArtifactFormat.PowerPointOpenXml => ExtractOfficeText(bytes, format, warnings),
            DocumentArtifactFormat.Pdf => ValidatePdf(bytes, warnings),
            _ => throw new InvalidDataException("不支持的文档格式。"),
        };
        var support = format switch
        {
            DocumentArtifactFormat.Markdown or DocumentArtifactFormat.Latex =>
                DocumentArtifactSupport.SourceText,
            DocumentArtifactFormat.Pdf => DocumentArtifactSupport.MetadataOnly,
            _ => DocumentArtifactSupport.ExtractedText,
        };
        var descriptor = new DocumentArtifactDescriptor(
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
            file.Name,
            format,
            MediaType(format),
            bytes.LongLength,
            support,
            warnings);
        return new DocumentArtifactPreflight(descriptor, normalizedText);
    }

    private static DocumentArtifactFormat ResolveFormat(string extension) => extension.ToLowerInvariant() switch
    {
        ".md" or ".markdown" => DocumentArtifactFormat.Markdown,
        ".tex" => DocumentArtifactFormat.Latex,
        ".drawio" => DocumentArtifactFormat.DrawIo,
        ".docx" => DocumentArtifactFormat.WordOpenXml,
        ".xlsx" => DocumentArtifactFormat.ExcelOpenXml,
        ".pptx" => DocumentArtifactFormat.PowerPointOpenXml,
        ".pdf" => DocumentArtifactFormat.Pdf,
        ".docm" or ".xlsm" or ".pptm" =>
            throw new InvalidDataException("暂不允许导入含宏的 Office 文档。"),
        _ => throw new InvalidDataException("仅支持 Markdown、TeX、DrawIO、DOCX、XLSX、PPTX 和 PDF。"),
    };

    private static string ReadBoundedUtf8(byte[] bytes, List<string> warnings)
    {
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException error)
        {
            throw new InvalidDataException("文本文件必须使用有效的 UTF-8 编码。", error);
        }
        return BoundText(text, warnings);
    }

    private static string ExtractDrawIoText(byte[] bytes, List<string> warnings)
    {
        var document = LoadXml(bytes);
        if (!string.Equals(document.Root?.Name.LocalName, "mxfile", StringComparison.Ordinal))
        {
            throw new InvalidDataException("DrawIO 文件缺少 mxfile 根元素。");
        }
        var labels = document
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "mxCell", StringComparison.Ordinal))
            .Select(element => element.Attribute("value")?.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => RemoveInlineMarkup(WebUtility.HtmlDecode(value!)));
        return BoundText(string.Join(Environment.NewLine, labels), warnings);
    }

    private static string? ExtractOfficeText(
        byte[] bytes,
        DocumentArtifactFormat format,
        List<string> warnings)
    {
        ValidateZipSignature(bytes);
        using var memory = new MemoryStream(bytes, writable: false);
        using var archive = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: false);
        ValidatePackage(archive);
        RequireEntry(archive, "[Content_Types].xml");

        return format switch
        {
            DocumentArtifactFormat.WordOpenXml => ExtractWordText(archive, warnings),
            DocumentArtifactFormat.PowerPointOpenXml => ExtractPowerPointText(archive, warnings),
            DocumentArtifactFormat.ExcelOpenXml => ExtractExcelText(archive, warnings),
            _ => throw new InvalidDataException("Office 包格式无效。"),
        };
    }

    private static string ExtractWordText(ZipArchive archive, List<string> warnings)
    {
        var document = LoadXml(RequireEntry(archive, "word/document.xml"));
        var paragraphs = document
            .Descendants()
            .Where(element => element.Name.LocalName == "p")
            .Select(paragraph => string.Concat(
                paragraph.Descendants().Where(element => element.Name.LocalName == "t").Select(element => element.Value)))
            .Where(text => text.Length > 0);
        return BoundText(string.Join(Environment.NewLine, paragraphs), warnings);
    }

    private static string ExtractPowerPointText(ZipArchive archive, List<string> warnings)
    {
        var slides = archive.Entries
            .Where(entry => entry.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal) &&
                            entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => NaturalSlideNumber(entry.FullName))
            .Select((entry, index) =>
            {
                var document = LoadXml(entry);
                var text = string.Join(
                    Environment.NewLine,
                    document.Descendants()
                        .Where(element => element.Name.LocalName == "t")
                        .Select(element => element.Value)
                        .Where(value => value.Length > 0));
                return $"[Slide {index + 1}]{Environment.NewLine}{text}";
            });
        var result = string.Join(Environment.NewLine + Environment.NewLine, slides);
        if (result.Length == 0)
        {
            throw new InvalidDataException("PPTX 包中没有可读取的幻灯片。");
        }
        return BoundText(result, warnings);
    }

    private static string ExtractExcelText(ZipArchive archive, List<string> warnings)
    {
        RequireEntry(archive, "xl/workbook.xml");
        var sharedStringsEntry = archive.GetEntry("xl/sharedStrings.xml");
        var sharedStrings = sharedStringsEntry is null
            ? []
            : LoadXml(sharedStringsEntry)
                .Descendants()
                .Where(element => element.Name.LocalName == "si")
                .Select(item => string.Concat(
                    item.Descendants().Where(element => element.Name.LocalName == "t").Select(element => element.Value)))
                .ToArray();
        var worksheets = archive.Entries
            .Where(entry => entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal) &&
                            entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .OrderBy(entry => NaturalSlideNumber(entry.FullName))
            .ToArray();
        if (worksheets.Length == 0)
        {
            throw new InvalidDataException("XLSX 包中没有可读取的工作表。");
        }

        var builder = new StringBuilder();
        for (var sheetIndex = 0; sheetIndex < worksheets.Length; ++sheetIndex)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"[Sheet {sheetIndex + 1}]");
            var document = LoadXml(worksheets[sheetIndex]);
            foreach (var row in document.Descendants().Where(element => element.Name.LocalName == "row"))
            {
                var cells = row.Elements().Where(element => element.Name.LocalName == "c")
                    .Select(cell => ReadCell(cell, sharedStrings));
                builder.AppendLine(string.Join('\t', cells));
                if (builder.Length > MaximumExtractedCharacters)
                {
                    break;
                }
            }
            builder.AppendLine();
        }
        return BoundText(builder.ToString(), warnings);
    }

    private static string ReadCell(XElement cell, IReadOnlyList<string> sharedStrings)
    {
        if (string.Equals(cell.Attribute("t")?.Value, "inlineStr", StringComparison.Ordinal))
        {
            return string.Concat(
                cell.Descendants().Where(element => element.Name.LocalName == "t").Select(element => element.Value));
        }
        var value = cell.Elements().FirstOrDefault(element => element.Name.LocalName == "v")?.Value ?? string.Empty;
        if (!string.Equals(cell.Attribute("t")?.Value, "s", StringComparison.Ordinal))
        {
            return value;
        }
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) &&
               index >= 0 && index < sharedStrings.Count
            ? sharedStrings[index]
            : throw new InvalidDataException("XLSX 共享字符串索引无效。");
    }

    private static string? ValidatePdf(byte[] bytes, List<string> warnings)
    {
        if (bytes.Length < 8 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
        {
            throw new InvalidDataException("PDF 扩展名与文件签名不匹配。");
        }
        warnings.Add("PDF 已安全登记；当前版本尚未提取正文或执行 OCR。");
        return null;
    }

    private static void ValidatePackage(ZipArchive archive)
    {
        if (archive.Entries.Count is < 1 or > MaximumPackageEntries)
        {
            throw new InvalidDataException("Office 包内文件数量超出安全限制。");
        }
        long expandedBytes = 0;
        foreach (var entry in archive.Entries)
        {
            var normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal) ||
                normalized.Split('/').Any(segment => segment == ".."))
            {
                throw new InvalidDataException("Office 包包含越界路径。");
            }
            expandedBytes = checked(expandedBytes + entry.Length);
            if (expandedBytes > MaximumExpandedPackageBytes || entry.Length > MaximumInputBytes)
            {
                throw new InvalidDataException("Office 包解压后的内容超出安全限制。");
            }
            if (entry.Length > 1_048_576 &&
                (entry.CompressedLength == 0 || entry.Length / (double)entry.CompressedLength > 100))
            {
                throw new InvalidDataException("Office 包的压缩膨胀比异常。");
            }
        }
    }

    private static void ValidateZipSignature(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != (byte)'P' || bytes[1] != (byte)'K' ||
            bytes[2] is not (3 or 5 or 7) || bytes[3] is not (4 or 6 or 8))
        {
            throw new InvalidDataException("Office 扩展名与 ZIP 包签名不匹配。");
        }
    }

    private static ZipArchiveEntry RequireEntry(ZipArchive archive, string path) =>
        archive.GetEntry(path) ?? throw new InvalidDataException($"Office 包缺少必要部件：{path}。");

    private static XDocument LoadXml(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        return LoadXml(stream);
    }

    private static XDocument LoadXml(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        return LoadXml(stream);
    }

    private static XDocument LoadXml(Stream stream)
    {
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumExpandedPackageBytes,
            MaxCharactersFromEntities = 0,
        });
        try
        {
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException error)
        {
            throw new InvalidDataException("XML 文档结构无效或包含被禁止的实体。", error);
        }
    }

    private static string BoundText(string text, List<string> warnings)
    {
        if (text.Length <= MaximumExtractedCharacters)
        {
            return text;
        }
        warnings.Add($"规范化文本已截断为 {MaximumExtractedCharacters} 个字符。");
        return text[..MaximumExtractedCharacters];
    }

    private static string RemoveInlineMarkup(string value)
    {
        var builder = new StringBuilder(value.Length);
        var insideTag = false;
        foreach (var character in value)
        {
            if (character == '<')
            {
                insideTag = true;
            }
            else if (character == '>')
            {
                insideTag = false;
            }
            else if (!insideTag)
            {
                builder.Append(character);
            }
        }
        return builder.ToString().Trim();
    }

    private static int NaturalSlideNumber(string path)
    {
        var digits = new string(Path.GetFileNameWithoutExtension(path).Where(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            ? number
            : int.MaxValue;
    }

    private static string MediaType(DocumentArtifactFormat format) => format switch
    {
        DocumentArtifactFormat.Markdown => "text/markdown",
        DocumentArtifactFormat.Latex => "application/x-tex",
        DocumentArtifactFormat.DrawIo => "application/vnd.jgraph.mxfile",
        DocumentArtifactFormat.WordOpenXml =>
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        DocumentArtifactFormat.ExcelOpenXml =>
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        DocumentArtifactFormat.PowerPointOpenXml =>
            "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        DocumentArtifactFormat.Pdf => "application/pdf",
        _ => "application/octet-stream",
    };
}
