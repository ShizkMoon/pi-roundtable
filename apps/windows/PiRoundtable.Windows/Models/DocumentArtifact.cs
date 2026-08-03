namespace PiRoundtable.Windows.Models;

internal enum DocumentArtifactFormat
{
    Markdown,
    Latex,
    DrawIo,
    WordOpenXml,
    ExcelOpenXml,
    PowerPointOpenXml,
    Pdf,
}

internal enum DocumentArtifactSupport
{
    SourceText,
    ExtractedText,
    MetadataOnly,
}

internal sealed record DocumentArtifactDescriptor(
    string ArtifactId,
    string FileName,
    DocumentArtifactFormat Format,
    string MediaType,
    long ByteLength,
    DocumentArtifactSupport Support,
    IReadOnlyList<string> Warnings);

internal sealed record DocumentArtifactPreflight(
    DocumentArtifactDescriptor Descriptor,
    string? NormalizedText);
