namespace PiRoundtable.Windows.Models;

public sealed class SessionGroupItem
{
    public SessionGroupItem(string groupId, string displayName, string kind, int sortOrder = 0)
    {
        GroupId = groupId;
        DisplayName = displayName;
        Kind = kind;
        SortOrder = sortOrder;
    }

    public string GroupId { get; }

    public string DisplayName { get; }

    public string Kind { get; }

    public int SortOrder { get; }

    public string KindLabel => Kind == "project" ? "项目" : "文件夹";

    public override string ToString() => DisplayName;
}
