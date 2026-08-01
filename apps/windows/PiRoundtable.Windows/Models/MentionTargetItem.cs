using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PiRoundtable.Windows.Models;

public sealed class MentionTargetItem : INotifyPropertyChanged
{
    private bool _isMentioned;

    public MentionTargetItem(RoleItem role)
    {
        Role = role;
    }

    public RoleItem Role { get; }

    public string RoleId => Role.RoleId;

    public string DisplayName => Role.DisplayName;

    public bool IsMentioned
    {
        get => _isMentioned;
        set
        {
            if (_isMentioned == value)
            {
                return;
            }
            _isMentioned = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsMentioned)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
