using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PiRoundtable.Windows.Models;

public sealed class TranscriptItem : INotifyPropertyChanged
{
    private string _text;
    private string _state;

    public TranscriptItem(
        string roleId,
        string speaker,
        string text,
        string state,
        string kind = "role",
        string visibility = "public",
        IEnumerable<string>? audienceRoleIds = null,
        string? messageId = null,
        DateTimeOffset? occurredAt = null)
    {
        MessageId = messageId ?? $"message.{Guid.NewGuid():N}";
        RoleId = roleId;
        Speaker = speaker;
        Kind = kind;
        Visibility = visibility;
        AudienceRoleIds = audienceRoleIds?.Distinct(StringComparer.Ordinal).ToArray() ?? [];
        _text = text;
        _state = state;
        OccurredAt = occurredAt ?? DateTimeOffset.Now;
    }

    public string MessageId { get; }

    public string RoleId { get; }

    public string Speaker { get; }

    public string Kind { get; }

    public string Visibility { get; }

    public IReadOnlyList<string> AudienceRoleIds { get; }

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
