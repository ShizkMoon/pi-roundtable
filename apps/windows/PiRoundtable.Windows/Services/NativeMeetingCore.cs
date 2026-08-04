using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace PiRoundtable.Windows.Services;

internal enum NativeEventKind
{
    RuntimeLeaseAcquired = 0,
    RuntimeLeaseReleased = 1,
    MeetingOpened = 2,
    MeetingClosed = 3,
    RoleRegistered = 4,
    RoleLeft = 5,
    SpeechStarted = 6,
    SpeechDelta = 7,
    SpeechCompleted = 8,
    SpeechCancelled = 9,
    InterruptionRequested = 10,
    ToolStarted = 11,
    ToolCompleted = 12,
    ToolFailed = 13,
    SubagentSpawned = 14,
    SubagentProgress = 15,
    SubagentCompleted = 16,
    SubagentFailed = 17,
    RoleTemporaryRegistered = 18,
    RolePromoted = 19,
    RoleArchived = 20,
    MessagePublished = 21,
    MessageDirectSent = 22,
    ToolApprovalRequested = 23,
    ToolApprovalResolved = 24,
    DiscussionConfigured = 25,
    DiscussionModeChanged = 26,
    AgendaItemChanged = 27,
    FloorRequested = 28,
    FloorGranted = 29,
    FloorRejected = 30,
    DiscussionBudgetUpdated = 31,
    ConvergenceRecorded = 32,
}

internal enum NativeEventVisibility
{
    Public = 0,
    Private = 1,
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativeEvent(
    ulong sequence,
    ulong runtimeGeneration,
    NativeEventKind kind,
    nint actorId,
    nint targetId,
    NativeEventVisibility visibility,
    nint audienceIds,
    ulong audienceCount)
{
    public readonly ulong Sequence = sequence;
    public readonly ulong RuntimeGeneration = runtimeGeneration;
    public readonly NativeEventKind Kind = kind;
    public readonly nint ActorId = actorId;
    public readonly nint TargetId = targetId;
    public readonly NativeEventVisibility Visibility = visibility;
    public readonly nint AudienceIds = audienceIds;
    public readonly ulong AudienceCount = audienceCount;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct NativeApplyResult(int error, ulong expectedSequence)
{
    public readonly int Error = error;
    public readonly ulong ExpectedSequence = expectedSequence;
}

internal sealed class NativeMeetingHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal NativeMeetingHandle(nint handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle()
    {
        NativeMeetingCore.Destroy(handle);
        return true;
    }
}

internal static partial class NativeMeetingCore
{
    private const string LibraryName = "pi_roundtable_core";

    [LibraryImport(LibraryName, EntryPoint = "pr_meeting_create")]
    internal static partial nint Create();

    [LibraryImport(LibraryName, EntryPoint = "pr_meeting_destroy")]
    internal static partial void Destroy(nint meeting);

    [LibraryImport(LibraryName, EntryPoint = "pr_meeting_apply")]
    internal static partial NativeApplyResult ApplyRaw(nint meeting, in NativeEvent meetingEvent);

    [LibraryImport(LibraryName, EntryPoint = "pr_meeting_last_sequence")]
    internal static partial ulong LastSequence(nint meeting);

    [LibraryImport(LibraryName, EntryPoint = "pr_meeting_runtime_generation")]
    internal static partial ulong RuntimeGeneration(nint meeting);

    [LibraryImport(LibraryName, EntryPoint = "pr_apply_error_message")]
    internal static partial nint ApplyErrorMessage(int error);
}

internal sealed class MeetingCoreSession : IMeetingCoreSession
{
    private sealed class Utf8StringArray : IDisposable
    {
        private readonly nint[] _strings;

        public Utf8StringArray(IReadOnlyList<string> values)
        {
            _strings = new nint[values.Count];
            if (values.Count == 0)
            {
                return;
            }

            Pointer = Marshal.AllocCoTaskMem(checked(values.Count * nint.Size));
            try
            {
                for (var index = 0; index < values.Count; index++)
                {
                    _strings[index] = StringToUtf8(values[index]);
                    Marshal.WriteIntPtr(Pointer, checked(index * nint.Size), _strings[index]);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public nint Pointer { get; private set; }

        public ulong Count => checked((ulong)_strings.Length);

        public void Dispose()
        {
            foreach (var value in _strings)
            {
                Marshal.FreeCoTaskMem(value);
            }
            Marshal.FreeCoTaskMem(Pointer);
            Pointer = nint.Zero;
        }
    }

    private readonly NativeMeetingHandle _handle;

    public MeetingCoreSession()
    {
        var handle = NativeMeetingCore.Create();
        if (handle == nint.Zero)
        {
            throw new InvalidOperationException("C++ Meeting Core allocation failed.");
        }
        _handle = new NativeMeetingHandle(handle);
    }

    public void Apply(RuntimeMeetingEvent meetingEvent)
    {
        var kind = MapEventKind(meetingEvent.Kind);
        var visibility = meetingEvent.Visibility switch
        {
            "public" => NativeEventVisibility.Public,
            "private" => NativeEventVisibility.Private,
            _ => throw new InvalidOperationException("Meeting event visibility must be public or private."),
        };
        var actor = StringToUtf8(meetingEvent.ActorId);
        var target = StringToUtf8(meetingEvent.TargetId);
        using var audience = new Utf8StringArray(meetingEvent.Audience);
        try
        {
            var nativeEvent = new NativeEvent(
                meetingEvent.Sequence,
                meetingEvent.RuntimeGeneration,
                kind,
                actor,
                target,
                visibility,
                audience.Pointer,
                audience.Count);
            var result = NativeMeetingCore.ApplyRaw(
                _handle.DangerousGetHandle(),
                in nativeEvent);
            if (result.Error != 0)
            {
                var errorName = Marshal.PtrToStringUTF8(
                    NativeMeetingCore.ApplyErrorMessage(result.Error)) ?? "unknown_error";
                throw new InvalidOperationException(
                    $"Meeting Core rejected {meetingEvent.Kind}: {errorName}; expected sequence {result.ExpectedSequence}.");
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(actor);
            Marshal.FreeCoTaskMem(target);
        }
    }

    public void Dispose()
    {
        _handle.Dispose();
    }

    public bool SupportsEventKind(string kind) => IsSupportedEventKind(kind);

    internal static bool IsSupportedEventKind(string kind) => TryMapEventKind(kind, out _);

    private static nint StringToUtf8(string? value)
    {
        return string.IsNullOrEmpty(value) ? nint.Zero : Marshal.StringToCoTaskMemUTF8(value);
    }

    private static NativeEventKind MapEventKind(string kind)
    {
        return TryMapEventKind(kind, out var mapped)
            ? mapped
            : throw new NotSupportedException($"Unsupported meeting event kind: {kind}");
    }

    private static bool TryMapEventKind(string kind, out NativeEventKind mapped)
    {
        var candidate = kind switch
        {
            "runtime.lease_acquired" => NativeEventKind.RuntimeLeaseAcquired,
            "runtime.lease_released" => NativeEventKind.RuntimeLeaseReleased,
            "meeting.opened" => NativeEventKind.MeetingOpened,
            "meeting.closed" => NativeEventKind.MeetingClosed,
            "message.published" => NativeEventKind.MessagePublished,
            "message.direct_sent" => NativeEventKind.MessageDirectSent,
            "role.registered" => NativeEventKind.RoleRegistered,
            "role.temporary_registered" => NativeEventKind.RoleTemporaryRegistered,
            "role.promoted" => NativeEventKind.RolePromoted,
            "role.archived" => NativeEventKind.RoleArchived,
            "role.left" => NativeEventKind.RoleLeft,
            "speech.started" => NativeEventKind.SpeechStarted,
            "speech.delta" => NativeEventKind.SpeechDelta,
            "speech.completed" => NativeEventKind.SpeechCompleted,
            "speech.cancelled" => NativeEventKind.SpeechCancelled,
            "interruption.requested" => NativeEventKind.InterruptionRequested,
            "tool.started" => NativeEventKind.ToolStarted,
            "tool.approval_requested" => NativeEventKind.ToolApprovalRequested,
            "tool.approval_resolved" => NativeEventKind.ToolApprovalResolved,
            "tool.completed" => NativeEventKind.ToolCompleted,
            "tool.failed" => NativeEventKind.ToolFailed,
            "subagent.spawned" => NativeEventKind.SubagentSpawned,
            "subagent.progress" => NativeEventKind.SubagentProgress,
            "subagent.completed" => NativeEventKind.SubagentCompleted,
            "subagent.failed" => NativeEventKind.SubagentFailed,
            "discussion.configured" => NativeEventKind.DiscussionConfigured,
            "discussion.mode_changed" => NativeEventKind.DiscussionModeChanged,
            "agenda.item_changed" => NativeEventKind.AgendaItemChanged,
            "floor.requested" => NativeEventKind.FloorRequested,
            "floor.granted" => NativeEventKind.FloorGranted,
            "floor.rejected" => NativeEventKind.FloorRejected,
            "discussion.budget_updated" => NativeEventKind.DiscussionBudgetUpdated,
            "convergence.recorded" => NativeEventKind.ConvergenceRecorded,
            _ => (NativeEventKind?)null,
        };
        mapped = candidate.GetValueOrDefault();
        return candidate.HasValue;
    }
}
