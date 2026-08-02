using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PiRoundtable.Windows.Models;

public sealed class McpToolGrantItem : INotifyPropertyChanged
{
    private bool _isGranted;

    public McpToolGrantItem(
        string serverId,
        string serverDisplayName,
        string toolName,
        string displayName,
        string? description,
        bool isGranted)
    {
        ServerId = serverId;
        ServerDisplayName = serverDisplayName;
        ToolName = toolName;
        DisplayName = displayName;
        Description = description;
        _isGranted = isGranted;
    }

    public string ServerId { get; }

    public string ServerDisplayName { get; }

    public string ToolName { get; }

    public string DisplayName { get; }

    public string? Description { get; }

    public string Summary => string.IsNullOrWhiteSpace(Description)
        ? $"{ServerDisplayName} · {ToolName}"
        : $"{ServerDisplayName} · {ToolName} · {Description}";

    public bool IsGranted
    {
        get => _isGranted;
        set => SetField(ref _isGranted, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
