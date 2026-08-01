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

bool is_private_role_activity(EventKind kind) {
    switch (kind) {
        case EventKind::SpeechStarted:
        case EventKind::SpeechDelta:
        case EventKind::SpeechCompleted:
        case EventKind::SpeechCancelled:
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
    const auto role = roles_.find(role_id);
    return !role_id.empty() && role != roles_.end() && !role->second.archived;
}

bool MeetingState::has_role(const std::string& role_id) const {
    return is_known_role(role_id);
}

std::size_t MeetingState::role_count() const noexcept {
    std::size_t count = 0;
    for (const auto& entry : roles_) {
        if (!entry.second.archived) {
            ++count;
        }
    }
    return count;
}

std::optional<RoleScope> MeetingState::role_scope(const std::string& role_id) const {
    const auto role = roles_.find(role_id);
    return role == roles_.end() ? std::nullopt : std::optional{role->second.scope};
}

bool MeetingState::is_role_archived(const std::string& role_id) const {
    const auto role = roles_.find(role_id);
    return role != roles_.end() && role->second.archived;
}

void MeetingState::clear_role_activity(const std::string& role_id) {
    if (active_speaker_id_ == role_id) {
        active_speaker_id_.reset();
    }
    if (pending_interruptor_id_ == role_id || pending_interrupt_target_id_ == role_id) {
        pending_interruptor_id_.reset();
        pending_interrupt_target_id_.reset();
    }
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

    if (event.visibility == EventVisibility::Private) {
        if (phase_ != MeetingPhase::Live) {
            return reject(ApplyError::InvalidTransition);
        }
        if (event.kind == EventKind::MessageDirectSent) {
            if (event.actor_id != "user.direct_host") {
                return reject(ApplyError::InvalidActor);
            }
            if (!is_known_role(event.target_id)) {
                return reject(is_role_archived(event.target_id)
                    ? ApplyError::RoleArchived
                    : ApplyError::UnknownRole);
            }
            return accept(event.sequence);
        }
        if (!is_private_role_activity(event.kind)) {
            return reject(ApplyError::InvalidTransition);
        }
        if (!is_known_role(event.actor_id)) {
            return reject(is_role_archived(event.actor_id)
                ? ApplyError::RoleArchived
                : ApplyError::UnknownRole);
        }
        if (event.target_id != "user.direct_host") {
            return reject(ApplyError::InvalidActor);
        }
        return accept(event.sequence);
    }

    if (phase_ == MeetingPhase::Closed) {
        return reject(ApplyError::InvalidTransition);
    }

    if ((event.kind == EventKind::MeetingOpened || event.kind == EventKind::MeetingClosed) &&
        runtime_owner_id_ != event.actor_id) {
        return reject(ApplyError::InvalidActor);
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

        case EventKind::MessagePublished:
            if (phase_ != MeetingPhase::Live) {
                return reject(ApplyError::InvalidTransition);
            }
            if (event.actor_id != "user.direct_host") {
                return reject(ApplyError::InvalidActor);
            }
            break;

        case EventKind::MessageDirectSent:
            return reject(ApplyError::InvalidTransition);

        case EventKind::RoleRegistered:
        case EventKind::RoleTemporaryRegistered:
            if (event.actor_id.empty()) {
                return reject(ApplyError::InvalidActor);
            }
            if (roles_.contains(event.actor_id)) {
                return reject(ApplyError::DuplicateRole);
            }
            roles_.emplace(
                event.actor_id,
                RoleState{
                    event.kind == EventKind::RoleRegistered
                        ? RoleScope::LongTerm
                        : RoleScope::Temporary,
                    false,
                });
            break;

        case EventKind::RolePromoted: {
            const auto role = roles_.find(event.actor_id);
            if (role == roles_.end()) {
                return reject(ApplyError::UnknownRole);
            }
            if (role->second.archived) {
                return reject(ApplyError::RoleArchived);
            }
            if (role->second.scope != RoleScope::Temporary) {
                return reject(ApplyError::RoleNotTemporary);
            }
            role->second.scope = RoleScope::LongTerm;
            break;
        }

        case EventKind::RoleArchived: {
            const auto role = roles_.find(event.actor_id);
            if (role == roles_.end()) {
                return reject(ApplyError::UnknownRole);
            }
            if (role->second.archived) {
                return reject(ApplyError::RoleArchived);
            }
            role->second.archived = true;
            clear_role_activity(event.actor_id);
            break;
        }

        case EventKind::RoleLeft:
            if (!roles_.contains(event.actor_id)) {
                return reject(ApplyError::UnknownRole);
            }
            clear_role_activity(event.actor_id);
            roles_.erase(event.actor_id);
            break;

        case EventKind::SpeechStarted:
            if (phase_ != MeetingPhase::Live) {
                return reject(ApplyError::InvalidTransition);
            }
            if (!is_known_role(event.actor_id)) {
                return reject(is_role_archived(event.actor_id)
                    ? ApplyError::RoleArchived
                    : ApplyError::UnknownRole);
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
                return reject(is_role_archived(event.actor_id) || is_role_archived(event.target_id)
                    ? ApplyError::RoleArchived
                    : ApplyError::UnknownRole);
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
                return reject(is_role_archived(event.actor_id)
                    ? ApplyError::RoleArchived
                    : ApplyError::UnknownRole);
            }
            break;

        case EventKind::RuntimeLeaseAcquired:
        case EventKind::RuntimeLeaseReleased:
            break;
    }

    return accept(event.sequence);
}

}  // namespace pi_roundtable::core
