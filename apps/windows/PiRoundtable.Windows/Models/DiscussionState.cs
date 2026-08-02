namespace PiRoundtable.Windows.Models;

public sealed class DiscussionSchedulerStateConfiguration
{
    public bool Configured { get; set; }
    public string Mode { get; set; } = "agenda";
    public string ResumeMode { get; set; } = "agenda";
    public List<DiscussionAgendaItemConfiguration> AgendaItems { get; set; } = [];
    public string? ActiveAgendaItemId { get; set; }
    public int ParticipantCount { get; set; } = 1;
    public DiscussionLimitsConfiguration Limits { get; set; } = new();
    public DiscussionCountersConfiguration Counters { get; set; } = new();
    public List<DiscussionFloorRequestConfiguration> PendingRequests { get; set; } = [];
    public string? PauseReason { get; set; }
}

public sealed class DiscussionLimitsConfiguration
{
    public int SoftTurnLimit { get; set; } = 8;
    public int HardTurnLimit { get; set; } = 12;
    public int SoftRoundLimit { get; set; } = 2;
    public int HardRoundLimit { get; set; } = 3;
    public int MaxConsecutiveTurnsPerRole { get; set; } = 2;
    public int MaxInterruptionsPerSegment { get; set; } = 2;
    public int MaxInterruptionsPerRole { get; set; } = 1;
    public int NoProgressTurnLimit { get; set; } = 2;
    public int MaxObserverProbesPerSegment { get; set; } = 12;
}

public sealed class DiscussionCountersConfiguration
{
    public int PublicTurns { get; set; }
    public int Rounds { get; set; }
    public int NoProgressTurns { get; set; }
    public int Interruptions { get; set; }
    public int ObserverProbes { get; set; }
    public string? ConsecutiveRoleId { get; set; }
    public int ConsecutiveTurns { get; set; }
    public Dictionary<string, int> InterruptionsByRole { get; set; } = new(StringComparer.Ordinal);
}

public sealed class DiscussionAgendaItemConfiguration
{
    public string AgendaItemId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = "pending";
}

public sealed class DiscussionFloorRequestConfiguration
{
    public string RequestId { get; set; } = string.Empty;
    public string RoleId { get; set; } = string.Empty;
    public string Kind { get; set; } = "normal";
    public string Reason { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public ulong RequestedAtSequence { get; set; }
    public string? RespondsToRoleId { get; set; }
    public string? AgendaItemId { get; set; }
}
