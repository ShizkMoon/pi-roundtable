using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;

namespace PiRoundtable.Windows.Models;

public sealed class SessionItem : INotifyPropertyChanged
{
    private string _title;
    private string _phase = "draft";
    private DateTimeOffset _updatedAt = DateTimeOffset.Now;
    private string _groupId = "group.general";

    public SessionItem(string sessionId, string title)
    {
        SessionId = sessionId;
        _title = title;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    public string SessionId { get; }

    public List<RoleItem> TemporaryRoles { get; } = [];

    public ObservableCollection<TranscriptItem> Transcript { get; } = [];

    public Dictionary<string, ObservableCollection<TranscriptItem>> PrivateThreads { get; } = new(StringComparer.Ordinal);

    public DateTimeOffset CreatedAt { get; set; }

    public string Title
    {
        get => _title;
        set => SetField(ref _title, value);
    }

    public string Phase
    {
        get => _phase;
        set => SetField(ref _phase, value);
    }

    public string GroupId
    {
        get => _groupId;
        set => SetField(ref _groupId, value);
    }

    public ObservableCollection<TranscriptItem> GetPrivateThread(string roleId)
    {
        if (!PrivateThreads.TryGetValue(roleId, out var thread))
        {
            thread = [];
            PrivateThreads.Add(roleId, thread);
        }
        return thread;
    }

    public DateTimeOffset UpdatedAt
    {
        get => _updatedAt;
        set => SetField(ref _updatedAt, value);
    }

    public string PhaseLabel => Phase switch
    {
        "live" => "进行中",
        "closed" => "已结束",
        _ => "草稿",
    };

    public string Summary => $"{PhaseLabel} · {UpdatedAt:MM-dd HH:mm}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName is nameof(Phase) or nameof(UpdatedAt))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PhaseLabel)));
        }
    }
}
