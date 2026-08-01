#include "pi_roundtable/c_api.h"

#include "pi_roundtable/core/meeting_state.hpp"

#include <new>
#include <string>

struct pr_meeting {
    pi_roundtable::core::MeetingState state;
};

namespace {

using pi_roundtable::core::ApplyError;
using pi_roundtable::core::EventKind;
using pi_roundtable::core::MeetingEvent;
using pi_roundtable::core::MeetingPhase;
using pi_roundtable::core::RoleScope;

pr_apply_result invalid_result(const pr_meeting* meeting) {
    const auto expected = meeting == nullptr ? 1 : meeting->state.last_sequence() + 1;
    return {PR_APPLY_INVALID_TRANSITION, expected};
}

}  // namespace

pr_meeting* pr_meeting_create(void) {
    return new (std::nothrow) pr_meeting{};
}

void pr_meeting_destroy(pr_meeting* meeting) {
    delete meeting;
}

pr_apply_result pr_meeting_apply(pr_meeting* meeting, const pr_event* event) {
    if (meeting == nullptr || event == nullptr ||
        event->kind < PR_EVENT_RUNTIME_LEASE_ACQUIRED ||
        event->kind > PR_EVENT_ROLE_ARCHIVED) {
        return invalid_result(meeting);
    }

    MeetingEvent native_event{
        event->sequence,
        event->runtime_generation,
        static_cast<EventKind>(event->kind),
        event->actor_id == nullptr ? std::string{} : std::string{event->actor_id},
        event->target_id == nullptr ? std::string{} : std::string{event->target_id},
    };
    const auto result = meeting->state.apply(native_event);
    return {static_cast<pr_apply_error>(result.error), result.expected_sequence};
}

uint64_t pr_meeting_last_sequence(const pr_meeting* meeting) {
    return meeting == nullptr ? 0 : meeting->state.last_sequence();
}

uint64_t pr_meeting_runtime_generation(const pr_meeting* meeting) {
    return meeting == nullptr ? 0 : meeting->state.runtime_generation();
}

int pr_meeting_has_active_lease(const pr_meeting* meeting) {
    return meeting != nullptr && meeting->state.lease_active() ? 1 : 0;
}

pr_meeting_phase pr_meeting_get_phase(const pr_meeting* meeting) {
    if (meeting == nullptr) {
        return PR_PHASE_CREATED;
    }
    return static_cast<pr_meeting_phase>(meeting->state.phase());
}

int pr_meeting_has_role(const pr_meeting* meeting, const char* role_id) {
    return meeting != nullptr && role_id != nullptr && meeting->state.has_role(role_id) ? 1 : 0;
}

pr_role_scope pr_meeting_role_scope(const pr_meeting* meeting, const char* role_id) {
    if (meeting == nullptr || role_id == nullptr) {
        return PR_ROLE_SCOPE_UNKNOWN;
    }
    const auto scope = meeting->state.role_scope(role_id);
    if (!scope.has_value()) {
        return PR_ROLE_SCOPE_UNKNOWN;
    }
    return *scope == RoleScope::LongTerm
        ? PR_ROLE_SCOPE_LONG_TERM
        : PR_ROLE_SCOPE_TEMPORARY;
}

int pr_meeting_role_is_archived(const pr_meeting* meeting, const char* role_id) {
    return meeting != nullptr && role_id != nullptr && meeting->state.is_role_archived(role_id) ? 1 : 0;
}

const char* pr_meeting_runtime_owner(const pr_meeting* meeting) {
    if (meeting == nullptr || !meeting->state.runtime_owner_id().has_value()) {
        return nullptr;
    }
    return meeting->state.runtime_owner_id()->c_str();
}

const char* pr_meeting_active_speaker(const pr_meeting* meeting) {
    if (meeting == nullptr || !meeting->state.active_speaker_id().has_value()) {
        return nullptr;
    }
    return meeting->state.active_speaker_id()->c_str();
}

const char* pr_meeting_pending_interruptor(const pr_meeting* meeting) {
    if (meeting == nullptr || !meeting->state.pending_interruptor_id().has_value()) {
        return nullptr;
    }
    return meeting->state.pending_interruptor_id()->c_str();
}
