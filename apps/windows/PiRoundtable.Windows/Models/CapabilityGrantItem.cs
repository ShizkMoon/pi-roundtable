using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PiRoundtable.Windows.Models;

public sealed class CapabilityGrantItem : INotifyPropertyChanged
{
    private bool _isGranted;

    public CapabilityGrantItem(string capabilityId, string displayName, string kind, bool isGranted)
    {
        CapabilityId = capabilityId;
        DisplayName = displayName;
        Kind = kind;
        _isGranted = isGranted;
    }

    public string CapabilityId { get; }
    public string DisplayName { get; }
    public string Kind { get; }

    public bool IsGranted
    {
        get => _isGranted;
        set
        {
            if (_isGranted == value)
            {
                return;
            }
            _isGranted = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsGranted)));
        }
    }

    public string Summary => $"{Kind} · 显式授权";

    public event PropertyChangedEventHandler? PropertyChanged;
}
