using System.ComponentModel;

namespace PiRoundtable.Windows.Models;

public sealed class SubagentRunItem(
    string subagentId,
    string parentRoleId,
    string parentRoleName,
    DateTimeOffset startedAt) : INotifyPropertyChanged
{
    private string _status = "运行中";
    private int _updateCount;

    public string SubagentId { get; } = subagentId;

    public string ParentRoleId { get; } = parentRoleId;

    public string ParentRoleName { get; } = parentRoleName;

    public DateTimeOffset StartedAt { get; } = startedAt;

    public string StartedAtLabel => StartedAt.ToLocalTime().ToString("HH:mm:ss");

    public string ShortId => SubagentId.Length <= 18 ? SubagentId : SubagentId[^12..];

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value)
            {
                return;
            }
            _status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
        }
    }

    public int UpdateCount
    {
        get => _updateCount;
        set
        {
            if (_updateCount == value)
            {
                return;
            }
            _updateCount = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UpdateCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
        }
    }

    public bool IsActive => Status == "运行中";

    public string Summary => UpdateCount == 0
        ? $"{ParentRoleName} · {Status}"
        : $"{ParentRoleName} · {Status} · {UpdateCount} 次进度更新";

    public event PropertyChangedEventHandler? PropertyChanged;
}
