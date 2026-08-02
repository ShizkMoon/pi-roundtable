using System.ComponentModel;

namespace PiRoundtable.Windows.Models;

public sealed class ToolApprovalItem(
    string approvalId,
    string roleId,
    string roleName,
    string serverName,
    string toolName,
    DateTimeOffset requestedAt) : INotifyPropertyChanged
{
    private bool _isResolving;

    public string ApprovalId { get; } = approvalId;

    public string RoleId { get; } = roleId;

    public string RoleName { get; } = roleName;

    public string ServerName { get; } = serverName;

    public string ToolName { get; } = toolName;

    public DateTimeOffset RequestedAt { get; } = requestedAt;

    public string RequestedAtLabel => RequestedAt.ToLocalTime().ToString("HH:mm:ss");

    public bool IsResolving
    {
        get => _isResolving;
        set
        {
            if (_isResolving == value)
            {
                return;
            }
            _isResolving = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsResolving)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanResolve)));
        }
    }

    public bool CanResolve => !IsResolving;

    public event PropertyChangedEventHandler? PropertyChanged;
}
