using System.ComponentModel;
using PiRoundtable.Windows.Services;

namespace PiRoundtable.Windows.Models;

public sealed class RoleMemoryItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public RoleMemoryItem(
        string memoryId,
        string kind,
        int revision,
        string content,
        string authority,
        DateTimeOffset updatedAt,
        bool isActive)
    {
        MemoryId = memoryId;
        Kind = kind;
        Revision = revision;
        Content = content;
        Authority = authority;
        UpdatedAt = updatedAt;
        IsActive = isActive;
    }

    public string MemoryId { get; }
    public string Kind { get; }
    public int Revision { get; }
    public string Content { get; }
    public string Authority { get; }
    public DateTimeOffset UpdatedAt { get; }
    public bool IsActive { get; }
    public string RevisionLabel => $"{Kind} · r{Revision} · {UpdatedAt.ToLocalTime():MM-dd HH:mm}";
    public string ProvenanceLabel => $"{Authority} · {MemoryId}";
    public string StatusLabel => IsActive ? "启用" : "已停用";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed class DocumentAttachmentItem
{
    internal DocumentAttachmentItem(DocumentArtifactPreflight preflight)
    {
        Preflight = preflight;
        ArtifactId = preflight.Descriptor.ArtifactId;
        FileName = preflight.Descriptor.FileName;
        SupportLabel = preflight.Descriptor.Support switch
        {
            DocumentArtifactSupport.SourceText => "源码文本",
            DocumentArtifactSupport.ExtractedText => "已提取文本",
            _ => "仅元数据",
        };
        Summary = $"{SupportLabel} · {preflight.Descriptor.ByteLength:N0} B";
        WarningSummary = preflight.Descriptor.Warnings.Count == 0
            ? string.Empty
            : string.Join("；", preflight.Descriptor.Warnings);
    }

    internal DocumentArtifactPreflight Preflight { get; }
    public string ArtifactId { get; }
    public string FileName { get; }
    public string SupportLabel { get; }
    public string Summary { get; }
    public string WarningSummary { get; }
}

public sealed class RoleMemoryCandidateItem
{
    internal RoleMemoryCandidateItem(RoleMemoryCandidate candidate)
    {
        CandidateId = candidate.CandidateId;
        Kind = candidate.Kind.ToString();
        Content = candidate.Content;
        Status = candidate.Status.ToString();
        DecisionRevision = candidate.DecisionRevision;
        SourceMeetingId = candidate.SourceMeetingId;
        UpdatedAt = candidate.UpdatedAt;
    }

    public string CandidateId { get; }
    public string Kind { get; }
    public string Content { get; }
    public string Status { get; }
    public int DecisionRevision { get; }
    public string SourceMeetingId { get; }
    public DateTimeOffset UpdatedAt { get; }
    public string AuditLabel => $"{Status} · d{DecisionRevision} · {UpdatedAt.ToLocalTime():MM-dd HH:mm}";
    public string SourceLabel => $"来源会议 · {SourceMeetingId}";
    public bool IsPending => Status == nameof(RoleMemoryCandidateStatus.Pending);
}
