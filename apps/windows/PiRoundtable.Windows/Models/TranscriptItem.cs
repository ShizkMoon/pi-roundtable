using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PiRoundtable.Windows.Models;

public sealed class TranscriptItem : INotifyPropertyChanged
{
    private string _text;
    private string _state;

    public TranscriptItem(string roleId, string speaker, string text, string state)
    {
        RoleId = roleId;
        Speaker = speaker;
        _text = text;
        _state = state;
        OccurredAt = DateTimeOffset.Now;
    }

    public string RoleId { get; }

    public string Speaker { get; }

    public DateTimeOffset OccurredAt { get; }

    public string TimeLabel => OccurredAt.ToLocalTime().ToString("HH:mm");

    public string Text
    {
        get => _text;
        set => SetField(ref _text, value);
    }

    public string State
    {
        get => _state;
        set => SetField(ref _state, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
