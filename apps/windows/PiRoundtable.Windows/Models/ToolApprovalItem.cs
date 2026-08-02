using System.ComponentModel;

namespace PiRoundtable.Windows.Models;

public sealed class ToolApprovalItem(
    string approvalId,
    string roleId,
    string roleName,
    string serverName,
    string toolName,
    DateTimeOffset requestedAt,
    DateTimeOffset expiresAt) : INotifyPropertyChanged
{
    private bool _isResolving;
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public string ApprovalId { get; } = approvalId;

    public string RoleId { get; } = roleId;

    public string RoleName { get; } = roleName;

    public string ServerName { get; } = serverName;

    public string ToolName { get; } = toolName;

    public DateTimeOffset RequestedAt { get; } = requestedAt;

    public DateTimeOffset ExpiresAt { get; } = expiresAt;

    public string RequestedAtLabel => RequestedAt.ToLocalTime().ToString("HH:mm:ss");

    public bool IsExpired => _now >= ExpiresAt;

    public string ExpiryLabel
    {
        get
        {
            var remaining = ExpiresAt - _now;
            if (remaining <= TimeSpan.Zero)
            {
                return "已到期 · Runtime 自动拒绝";
            }
            var seconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
            return $"剩余 {seconds} 秒 · 到期自动拒绝";
        }
    }

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

    public bool CanResolve => !IsResolving && !IsExpired;

    public void RefreshDeadline(DateTimeOffset now)
    {
        var wasExpired = IsExpired;
        _now = now;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpiryLabel)));
        if (wasExpired != IsExpired)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpired)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanResolve)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
