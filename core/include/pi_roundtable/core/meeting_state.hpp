#pragma once

#include "pi_roundtable/core/event.hpp"

#include <cstdint>
#include <optional>
#include <string>
#include <unordered_set>

namespace pi_roundtable::core {

enum class MeetingPhase : std::uint8_t {
    Created = 0,
    Live,
    Closed,
};

enum class ApplyError : std::uint8_t {
    None = 0,
    OutOfOrderSequence,
    LeaseRequired,
    StaleRuntimeGeneration,
    InvalidRuntimeGeneration,
    InvalidTransition,
    InvalidActor,
    UnknownRole,
    DuplicateRole,
    FloorBusy,
    NoActiveSpeaker,
    InterruptionPending,
};

struct ApplyResult {
    ApplyError error{ApplyError::None};
    std::uint64_t expected_sequence{1};

    [[nodiscard]] constexpr bool accepted() const noexcept {
        return error == ApplyError::None;
    }
};

class MeetingState {
public:
    [[nodiscard]] ApplyResult apply(const MeetingEvent& event);

    [[nodiscard]] std::uint64_t last_sequence() const noexcept { return last_sequence_; }
    [[nodiscard]] std::uint64_t runtime_generation() const noexcept { return runtime_generation_; }
    [[nodiscard]] bool lease_active() const noexcept { return lease_active_; }
    [[nodiscard]] const std::optional<std::string>& runtime_owner_id() const noexcept {
        return runtime_owner_id_;
    }
    [[nodiscard]] MeetingPhase phase() const noexcept { return phase_; }
    [[nodiscard]] const std::optional<std::string>& active_speaker_id() const noexcept {
        return active_speaker_id_;
    }
    [[nodiscard]] const std::optional<std::string>& pending_interruptor_id() const noexcept {
        return pending_interruptor_id_;
    }
    [[nodiscard]] bool has_role(const std::string& role_id) const;
    [[nodiscard]] std::size_t role_count() const noexcept { return roles_.size(); }

private:
    [[nodiscard]] ApplyResult reject(ApplyError error) const noexcept;
    [[nodiscard]] ApplyResult accept(std::uint64_t sequence) noexcept;
    [[nodiscard]] bool is_known_role(const std::string& role_id) const;

    std::uint64_t last_sequence_{0};
    std::uint64_t runtime_generation_{0};
    bool lease_active_{false};
    std::optional<std::string> runtime_owner_id_;
    MeetingPhase phase_{MeetingPhase::Created};
    std::unordered_set<std::string> roles_;
    std::optional<std::string> active_speaker_id_;
    std::optional<std::string> pending_interruptor_id_;
    std::optional<std::string> pending_interrupt_target_id_;
};

}  // namespace pi_roundtable::core
