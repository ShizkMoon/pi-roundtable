#pragma once

#include <cstdint>
#include <string>

namespace pi_roundtable::core {

enum class EventKind : std::uint8_t {
    RuntimeLeaseAcquired = 0,
    RuntimeLeaseReleased,
    MeetingOpened,
    MeetingClosed,
    RoleRegistered,
    RoleLeft,
    SpeechStarted,
    SpeechDelta,
    SpeechCompleted,
    SpeechCancelled,
    InterruptionRequested,
    ToolStarted,
    ToolCompleted,
    ToolFailed,
    SubagentSpawned,
    SubagentProgress,
    SubagentCompleted,
    SubagentFailed,
    RoleTemporaryRegistered,
    RolePromoted,
    RoleArchived,
};

struct MeetingEvent {
    std::uint64_t sequence{};
    std::uint64_t runtime_generation{};
    EventKind kind{};
    std::string actor_id;
    std::string target_id;
};

}  // namespace pi_roundtable::core
