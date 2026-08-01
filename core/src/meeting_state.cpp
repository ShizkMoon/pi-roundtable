#include "pi_roundtable/core/meeting_state.hpp"

namespace pi_roundtable::core {

namespace {

bool is_runtime_activity(EventKind kind) {
    switch (kind) {
        case EventKind::ToolStarted:
        case EventKind::ToolCompleted:
        case EventKind::ToolFailed:
        case EventKind::SubagentSpawned:
        case EventKind::SubagentProgress:
        case EventKind::SubagentCompleted:
        case EventKind::SubagentFailed:
            return true;
        default:
            return false;
    }
}

}  // namespace

ApplyResult MeetingState::reject(ApplyError error) const noexcept {
    return ApplyResult{error, last_sequence_ + 1};
}

ApplyResult MeetingState::accept(std::uint64_t sequence) noexcept {
    last_sequence_ = sequence;
    return ApplyResult{ApplyError::None, last_sequence_ + 1};
}

bool MeetingState::is_known_role(const std::string& role_id) const {
    return !role_id.empty() && roles_.contains(role_id);
}

bool MeetingState::has_role(const std::string& role_id) const {
    return is_known_role(role_id);
}

ApplyResult MeetingState::apply(const MeetingEvent& event) {
    if (event.sequence != last_sequence_ + 1) {
        return reject(ApplyError::OutOfOrderSequence);
    }

    if (event.kind == EventKind::RuntimeLeaseAcquired) {
        if (phase_ == MeetingPhase::Closed) {
            return reject(ApplyError::InvalidTransition);
        }
        if (event.actor_id.empty()) {
            return reject(ApplyError::InvalidActor);
        }
        if (event.runtime_generation <= runtime_generation_) {
            return reject(ApplyError::StaleRuntimeGeneration);
        }
        if (event.runtime_generation != runtime_generation_ + 1) {
            return reject(ApplyError::InvalidRuntimeGeneration);
        }

        runtime_generation_ = event.runtime_generation;
        lease_active_ = true;
        runtime_owner_id_ = event.actor_id;
        active_speaker_id_.reset();
        pending_interruptor_id_.reset();
        pending_interrupt_target_id_.reset();
        return accept(event.sequence);
    }

    if (!lease_active_) {
        return reject(ApplyError::LeaseRequired);
    }
    if (event.runtime_generation != runtime_generation_) {
        return reject(event.runtime_generation < runtime_generation_
            ? ApplyError::StaleRuntimeGeneration
            : ApplyError::InvalidRuntimeGeneration);
    }

    if (event.kind == EventKind::RuntimeLeaseReleased) {
        if (!runtime_owner_id_.has_value() || runtime_owner_id_ != event.actor_id) {
            return reject(ApplyError::InvalidActor);
        }
        lease_active_ = false;
        runtime_owner_id_.reset();
        active_speaker_id_.reset();
        pending_interruptor_id_.reset();
        pending_interrupt_target_id_.reset();
        return accept(event.sequence);
    }

    if (phase_ == MeetingPhase::Closed) {
        return reject(ApplyError::InvalidTransition);
    }

    switch (event.kind) {
        case EventKind::MeetingOpened:
            if (phase_ != MeetingPhase::Created) {
                return reject(ApplyError::InvalidTransition);
            }
            phase_ = MeetingPhase::Live;
            break;

        case EventKind::MeetingClosed:
            if (phase_ != MeetingPhase::Live) {
                return reject(ApplyError::InvalidTransition);
            }
            phase_ = MeetingPhase::Closed;
            active_speaker_id_.reset();
            pending_interruptor_id_.reset();
            pending_interrupt_target_id_.reset();
            break;

        case EventKind::RoleRegistered:
            if (event.actor_id.empty()) {
                return reject(ApplyError::InvalidActor);
            }
            if (roles_.contains(event.actor_id)) {
                return reject(ApplyError::DuplicateRole);
            }
            roles_.insert(event.actor_id);
            break;

        case EventKind::RoleLeft:
            if (!is_known_role(event.actor_id)) {
                return reject(ApplyError::UnknownRole);
            }
            if (active_speaker_id_ == event.actor_id) {
                active_speaker_id_.reset();
            }
            if (pending_interruptor_id_ == event.actor_id ||
                pending_interrupt_target_id_ == event.actor_id) {
                pending_interruptor_id_.reset();
                pending_interrupt_target_id_.reset();
            }
            roles_.erase(event.actor_id);
            break;

        case EventKind::SpeechStarted:
            if (phase_ != MeetingPhase::Live) {
                return reject(ApplyError::InvalidTransition);
            }
            if (!is_known_role(event.actor_id)) {
                return reject(ApplyError::UnknownRole);
            }
            if (active_speaker_id_.has_value()) {
                return reject(ApplyError::FloorBusy);
            }
            if (pending_interruptor_id_.has_value() && pending_interruptor_id_ != event.actor_id) {
                return reject(ApplyError::InvalidActor);
            }
            active_speaker_id_ = event.actor_id;
            pending_interruptor_id_.reset();
            pending_interrupt_target_id_.reset();
            break;

        case EventKind::SpeechDelta:
        case EventKind::SpeechCompleted:
            if (!active_speaker_id_.has_value()) {
                return reject(ApplyError::NoActiveSpeaker);
            }
            if (active_speaker_id_ != event.actor_id) {
                return reject(ApplyError::InvalidActor);
            }
            if (event.kind == EventKind::SpeechCompleted) {
                active_speaker_id_.reset();
                pending_interruptor_id_.reset();
                pending_interrupt_target_id_.reset();
            }
            break;

        case EventKind::InterruptionRequested:
            if (phase_ != MeetingPhase::Live) {
                return reject(ApplyError::InvalidTransition);
            }
            if (!is_known_role(event.actor_id) || !is_known_role(event.target_id)) {
                return reject(ApplyError::UnknownRole);
            }
            if (event.actor_id == event.target_id) {
                return reject(ApplyError::InvalidActor);
            }
            if (!active_speaker_id_.has_value() || active_speaker_id_ != event.target_id) {
                return reject(ApplyError::NoActiveSpeaker);
            }
            if (pending_interruptor_id_.has_value()) {
                return reject(ApplyError::InterruptionPending);
            }
            pending_interruptor_id_ = event.actor_id;
            pending_interrupt_target_id_ = event.target_id;
            break;

        case EventKind::SpeechCancelled:
            if (!active_speaker_id_.has_value()) {
                return reject(ApplyError::NoActiveSpeaker);
            }
            if (event.target_id.empty() || active_speaker_id_ != event.target_id) {
                return reject(ApplyError::InvalidActor);
            }
            if (pending_interrupt_target_id_.has_value() &&
                pending_interrupt_target_id_ != event.target_id) {
                return reject(ApplyError::InvalidActor);
            }
            if (pending_interruptor_id_.has_value()) {
                if (event.actor_id != *pending_interruptor_id_ && event.actor_id != runtime_owner_id_) {
                    return reject(ApplyError::InvalidActor);
                }
            } else if (event.actor_id != event.target_id && event.actor_id != runtime_owner_id_) {
                return reject(ApplyError::InvalidActor);
            }
            active_speaker_id_.reset();
            if (!pending_interruptor_id_.has_value()) {
                pending_interrupt_target_id_.reset();
            }
            break;

        case EventKind::ToolStarted:
        case EventKind::ToolCompleted:
        case EventKind::ToolFailed:
        case EventKind::SubagentSpawned:
        case EventKind::SubagentProgress:
        case EventKind::SubagentCompleted:
        case EventKind::SubagentFailed:
            if (phase_ != MeetingPhase::Live) {
                return reject(ApplyError::InvalidTransition);
            }
            if (!is_runtime_activity(event.kind) || !is_known_role(event.actor_id)) {
                return reject(ApplyError::UnknownRole);
            }
            break;

        case EventKind::RuntimeLeaseAcquired:
        case EventKind::RuntimeLeaseReleased:
            break;
    }

    return accept(event.sequence);
}

}  // namespace pi_roundtable::core
