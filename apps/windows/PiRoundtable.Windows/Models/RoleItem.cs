using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PiRoundtable.Windows.Models;

public sealed class RoleItem : INotifyPropertyChanged
{
    private string _scope;
    private string _status = "未连接";
    private bool _isArchived;

    public RoleItem(string roleId, string displayName, string scope)
    {
        RoleId = roleId;
        DisplayName = displayName;
        _scope = scope;
    }

    public string RoleId { get; }

    public string DisplayName { get; }

    public string Scope
    {
        get => _scope;
        set => SetField(ref _scope, value);
    }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public bool IsArchived
    {
        get => _isArchived;
        set => SetField(ref _isArchived, value);
    }

    public string ScopeLabel => Scope == "long_term" ? "长期角色" : "临时角色";

    public string Summary => $"{ScopeLabel} · {Status}";

    public event PropertyChangedEventHandler? PropertyChanged;

    public override string ToString() => DisplayName;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName is nameof(Scope) or nameof(Status))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ScopeLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
        }
    }
}
