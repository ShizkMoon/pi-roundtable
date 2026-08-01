using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PiRoundtable.Windows.Models;

public sealed class ProviderModelCandidate : INotifyPropertyChanged
{
    private bool _isSelected;

    public required string ModelId { get; init; }
    public required string DisplayName { get; init; }
    public int? ContextWindow { get; init; }
    public List<string> Capabilities { get; init; } = ["text"];
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

    public string MetadataSummary => ContextWindow is > 0
        ? $"{ModelId} · 上下文 {ContextWindow:N0}"
        : ModelId;

    public event PropertyChangedEventHandler? PropertyChanged;
}
