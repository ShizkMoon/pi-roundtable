#include "pi_roundtable/c_api.h"
#include "pi_roundtable/core/meeting_state.hpp"

#include <iostream>
#include <stdexcept>
#include <string>

using pi_roundtable::core::ApplyError;
using pi_roundtable::core::EventKind;
using pi_roundtable::core::EventVisibility;
using pi_roundtable::core::MeetingEvent;
using pi_roundtable::core::MeetingPhase;
using pi_roundtable::core::MeetingState;
using pi_roundtable::core::RoleScope;

namespace {

void check(bool condition, const std::string& message) {
    if (!condition) {
        throw std::runtime_error(message);
    }
}

void apply_ok(MeetingState& state, MeetingEvent event) {
    const auto result = state.apply(event);
    check(result.accepted(), "expected accepted event at sequence " + std::to_string(event.sequence));
}

void interruption_handoff_is_explicit() {
    MeetingState state;
    apply_ok(state, {1, 1, EventKind::RuntimeLeaseAcquired, "runtime.windows", ""});
    apply_ok(state, {2, 1, EventKind::RoleRegistered, "role.system", ""});
    apply_ok(state, {3, 1, EventKind::RoleRegistered, "role.numerics", ""});
    apply_ok(state, {4, 1, EventKind::MeetingOpened, "runtime.windows", ""});
    apply_ok(state, {5, 1, EventKind::SpeechStarted, "role.numerics", ""});
    apply_ok(state, {6, 1, EventKind::InterruptionRequested, "role.system", "role.numerics"});

    check(state.active_speaker_id() == "role.numerics", "interrupt request must not fake cancellation");
    check(state.pending_interruptor_id() == "role.system", "interruptor should be recorded");

    const auto forged_cancel = state.apply({7, 1, EventKind::SpeechCancelled, "role.unknown", "role.numerics"});
    check(forged_cancel.error == ApplyError::InvalidActor, "only the interruptor or runtime owner may cancel");
    apply_ok(state, {7, 1, EventKind::SpeechCancelled, "role.system", "role.numerics"});
    const auto bypass = state.apply({8, 1, EventKind::SpeechStarted, "role.numerics", ""});
    check(bypass.error == ApplyError::InvalidActor, "pending handoff must fence the previous speaker");
    check(state.last_sequence() == 7, "rejected event must not consume a sequence");

    apply_ok(state, {8, 1, EventKind::SpeechStarted, "role.system", ""});
    apply_ok(state, {9, 1, EventKind::SpeechCompleted, "role.system", ""});
    check(!state.active_speaker_id().has_value(), "completed speech should release the floor");
}

void runtime_generation_fences_old_owner() {
    MeetingState state;
    apply_ok(state, {1, 1, EventKind::RuntimeLeaseAcquired, "runtime.a", ""});
    apply_ok(state, {2, 1, EventKind::RoleRegistered, "role.host", ""});
    apply_ok(state, {3, 1, EventKind::MeetingOpened, "runtime.a", ""});
    const auto forged_release = state.apply({4, 1, EventKind::RuntimeLeaseReleased, "runtime.b", ""});
    check(forged_release.error == ApplyError::InvalidActor, "only the runtime owner may release its lease");
    apply_ok(state, {4, 1, EventKind::RuntimeLeaseReleased, "runtime.a", ""});

    const auto without_lease = state.apply({5, 1, EventKind::SpeechStarted, "role.host", ""});
    check(without_lease.error == ApplyError::LeaseRequired, "released owner must not keep writing");

    apply_ok(state, {5, 2, EventKind::RuntimeLeaseAcquired, "runtime.b", ""});
    const auto stale = state.apply({6, 1, EventKind::SpeechStarted, "role.host", ""});
    check(stale.error == ApplyError::StaleRuntimeGeneration, "old generation should be fenced");
    apply_ok(state, {6, 2, EventKind::SpeechStarted, "role.host", ""});
}

void c_api_exposes_stable_boundary() {
    pr_meeting* meeting = pr_meeting_create();
    check(meeting != nullptr, "C API should allocate a meeting");

    const pr_event lease{1, 1, PR_EVENT_RUNTIME_LEASE_ACQUIRED, "runtime.windows", nullptr};
    const auto result = pr_meeting_apply(meeting, &lease);
    check(result.error == PR_APPLY_OK, "C API lease should apply");
    check(pr_meeting_runtime_generation(meeting) == 1, "C API should expose generation");
    check(pr_meeting_has_active_lease(meeting) == 1, "C API should expose lease state");
    check(std::string{pr_meeting_runtime_owner(meeting)} == "runtime.windows", "C API should expose owner");
    const pr_event oversized_audience{
        2,
        1,
        PR_EVENT_MESSAGE_DIRECT_SENT,
        "user.direct_host",
        "role.secretary",
        PR_EVENT_VISIBILITY_PRIVATE,
        nullptr,
        4'097,
    };
    check(pr_meeting_apply(meeting, &oversized_audience).error == PR_APPLY_INVALID_AUDIENCE,
        "C API should reject unbounded audience arrays before allocation");
    pr_meeting_destroy(meeting);
}

void created_meeting_cannot_close_without_opening() {
    MeetingState state;
    apply_ok(state, {1, 1, EventKind::RuntimeLeaseAcquired, "runtime.a", ""});
    const auto result = state.apply({2, 1, EventKind::MeetingClosed, "runtime.a", ""});
    check(result.error == ApplyError::InvalidTransition, "created meeting must open before it closes");
}

void higher_generation_supersedes_an_expired_owner() {
    MeetingState state;
    apply_ok(state, {1, 1, EventKind::RuntimeLeaseAcquired, "runtime.a", ""});
    apply_ok(state, {2, 1, EventKind::RoleRegistered, "role.host", ""});
    apply_ok(state, {3, 1, EventKind::MeetingOpened, "runtime.a", ""});
    apply_ok(state, {4, 2, EventKind::RuntimeLeaseAcquired, "runtime.b", ""});
    check(state.runtime_owner_id() == "runtime.b", "new generation should replace the expired owner");
    check(state.runtime_generation() == 2, "new generation should fence the previous owner");
}

void role_lifecycle_is_deterministic() {
    MeetingState state;
    apply_ok(state, {1, 1, EventKind::RuntimeLeaseAcquired, "runtime.windows", ""});
    const auto future_generation = state.apply(
        {2, 2, EventKind::RoleTemporaryRegistered, "role.future", ""});
    check(future_generation.error == ApplyError::InvalidRuntimeGeneration,
        "role lifecycle events must carry the active runtime generation");
    check(state.last_sequence() == 1, "generation rejection must not consume a sequence");
    apply_ok(state, {2, 1, EventKind::RoleRegistered, "role.facilitator", ""});
    apply_ok(state, {3, 1, EventKind::RoleTemporaryRegistered, "role.researcher", ""});

    check(state.role_count() == 2, "both active roles should count as participants");
    check(state.role_scope("role.facilitator") == RoleScope::LongTerm,
        "registered role should be long-term");
    check(state.role_scope("role.researcher") == RoleScope::Temporary,
        "temporary registration should retain its scope");

    apply_ok(state, {4, 1, EventKind::RolePromoted, "role.researcher", ""});
    check(state.role_scope("role.researcher") == RoleScope::LongTerm,
        "promotion should be a one-way scope transition");
    const auto second_promotion = state.apply(
        {5, 1, EventKind::RolePromoted, "role.researcher", ""});
    check(second_promotion.error == ApplyError::RoleNotTemporary,
        "a long-term role must not be promoted again");
    check(state.last_sequence() == 4, "rejected promotion must not consume a sequence");

    apply_ok(state, {5, 1, EventKind::RoleTemporaryRegistered, "role.critic", ""});
    apply_ok(state, {6, 1, EventKind::MeetingOpened, "runtime.windows", ""});
    apply_ok(state, {7, 1, EventKind::SpeechStarted, "role.critic", ""});
    apply_ok(state, {8, 1, EventKind::RoleArchived, "role.critic", ""});
    check(!state.has_role("role.critic"), "archived role should leave the active roster");
    check(state.is_role_archived("role.critic"), "archived role metadata should be retained");
    check(state.role_count() == 2, "archived role should not count as active");
    check(!state.active_speaker_id().has_value(), "archiving the speaker should release the floor");

    const auto archived_speech = state.apply(
        {9, 1, EventKind::SpeechStarted, "role.critic", ""});
    check(archived_speech.error == ApplyError::RoleArchived,
        "archived role must not take the floor");
    apply_ok(state, {9, 1, EventKind::RoleLeft, "role.critic", ""});
    check(!state.role_scope("role.critic").has_value(),
        "leaving should remove the retained meeting role record");
    apply_ok(state, {10, 1, EventKind::RoleTemporaryRegistered, "role.critic", ""});
    check(state.has_role("role.critic"), "a removed role id may join again explicitly");
}

void c_api_exposes_role_lifecycle() {
    pr_meeting* meeting = pr_meeting_create();
    check(meeting != nullptr, "C API should allocate a meeting");

    const pr_event lease{1, 1, PR_EVENT_RUNTIME_LEASE_ACQUIRED, "runtime.windows", nullptr};
    const pr_event temporary{
        2,
        1,
        PR_EVENT_ROLE_TEMPORARY_REGISTERED,
        "role.researcher",
        nullptr,
    };
    const pr_event promoted{3, 1, PR_EVENT_ROLE_PROMOTED, "role.researcher", nullptr};
    const pr_event archived{4, 1, PR_EVENT_ROLE_ARCHIVED, "role.researcher", nullptr};

    check(pr_meeting_apply(meeting, &lease).error == PR_APPLY_OK, "lease should apply");
    check(pr_meeting_apply(meeting, &temporary).error == PR_APPLY_OK,
        "temporary role should register through C API");
    check(pr_meeting_has_role(meeting, "role.researcher") == 1,
        "C API should expose active role membership");
    check(pr_meeting_role_scope(meeting, "role.researcher") == PR_ROLE_SCOPE_TEMPORARY,
        "C API should expose temporary scope");
    check(pr_meeting_apply(meeting, &promoted).error == PR_APPLY_OK,
        "C API should promote a temporary role");
    check(pr_meeting_role_scope(meeting, "role.researcher") == PR_ROLE_SCOPE_LONG_TERM,
        "C API should expose promoted scope");
    check(pr_meeting_apply(meeting, &archived).error == PR_APPLY_OK,
        "C API should archive a role");
    check(pr_meeting_has_role(meeting, "role.researcher") == 0,
        "archived role should not remain active");
    check(pr_meeting_role_is_archived(meeting, "role.researcher") == 1,
        "C API should expose archived state");
    pr_meeting_destroy(meeting);
}

void runtime_owner_controls_meeting_phase() {
    MeetingState state;
    apply_ok(state, {1, 1, EventKind::RuntimeLeaseAcquired, "runtime.windows", ""});
    const auto forged_open = state.apply(
        {2, 1, EventKind::MeetingOpened, "role.host", ""});
    check(forged_open.error == ApplyError::InvalidActor,
        "only the active runtime owner may open a meeting");
    apply_ok(state, {2, 1, EventKind::MeetingOpened, "runtime.windows", ""});
    const auto forged_close = state.apply(
        {3, 1, EventKind::MeetingClosed, "runtime.other", ""});
    check(forged_close.error == ApplyError::InvalidActor,
        "only the active runtime owner may close a meeting");
    apply_ok(state, {3, 1, EventKind::MeetingClosed, "runtime.windows", ""});
}

void complete_local_roundtable_loop_is_deterministic() {
    MeetingState state;
    apply_ok(state, {1, 1, EventKind::RuntimeLeaseAcquired, "runtime.windows", ""});
    apply_ok(state, {2, 1, EventKind::RoleRegistered, "role.host", ""});
    apply_ok(state, {3, 1, EventKind::RoleTemporaryRegistered, "role.critic", ""});
    apply_ok(state, {4, 1, EventKind::MeetingOpened, "runtime.windows", ""});
    apply_ok(state, {5, 1, EventKind::SpeechStarted, "role.host", ""});
    apply_ok(state, {6, 1, EventKind::SpeechDelta, "role.host", ""});
    apply_ok(state, {7, 1, EventKind::InterruptionRequested, "role.critic", "role.host"});
    check(state.pending_interrupt_target_id() == "role.host",
        "core should retain the interruption target until cancellation");
    apply_ok(state, {8, 1, EventKind::SpeechCancelled, "runtime.windows", "role.host"});
    apply_ok(state, {9, 1, EventKind::SpeechStarted, "role.critic", ""});
    apply_ok(state, {10, 1, EventKind::SpeechCompleted, "role.critic", ""});
    apply_ok(state, {11, 1, EventKind::RolePromoted, "role.critic", ""});
    apply_ok(state, {12, 1, EventKind::MeetingClosed, "runtime.windows", ""});
    apply_ok(state, {13, 1, EventKind::RuntimeLeaseReleased, "runtime.windows", ""});

    check(state.phase() == MeetingPhase::Closed, "meeting should finish closed");
    check(!state.lease_active(), "closed local loop should release its runtime lease");
    check(state.role_scope("role.critic") == RoleScope::LongTerm,
        "retained temporary role should finish promoted");
    check(state.last_sequence() == 13, "every accepted local event should be ordered");
}

void c_api_exposes_interruption_projection_and_errors() {
    pr_meeting* meeting = pr_meeting_create();
    check(meeting != nullptr, "C API should allocate a meeting");
    const pr_event events[] = {
        {1, 1, PR_EVENT_RUNTIME_LEASE_ACQUIRED, "runtime.windows", nullptr},
        {2, 1, PR_EVENT_ROLE_REGISTERED, "role.host", nullptr},
        {3, 1, PR_EVENT_ROLE_TEMPORARY_REGISTERED, "role.critic", nullptr},
        {4, 1, PR_EVENT_MEETING_OPENED, "runtime.windows", nullptr},
        {5, 1, PR_EVENT_SPEECH_STARTED, "role.host", nullptr},
        {6, 1, PR_EVENT_INTERRUPTION_REQUESTED, "role.critic", "role.host"},
    };
    for (const auto& event : events) {
        check(pr_meeting_apply(meeting, &event).error == PR_APPLY_OK,
            "C API local-loop event should apply");
    }
    check(pr_meeting_role_count(meeting) == 2, "C API should expose active role count");
    check(std::string{pr_meeting_pending_interrupt_target(meeting)} == "role.host",
        "C API should expose interruption target");
    check(std::string{pr_apply_error_message(PR_APPLY_FLOOR_BUSY)} == "floor_busy",
        "C API should expose stable machine-readable error text");
    pr_meeting_destroy(meeting);
}

void private_events_advance_sequence_without_taking_public_floor() {
    MeetingState state;
    apply_ok(state, {1, 1, EventKind::RuntimeLeaseAcquired, "runtime.windows", ""});
    apply_ok(state, {2, 1, EventKind::RoleRegistered, "role.secretary", ""});
    apply_ok(state, {3, 1, EventKind::MeetingOpened, "runtime.windows", ""});
    apply_ok(state, {4, 1, EventKind::MessagePublished, "user.direct_host", ""});
    apply_ok(state, {
        5,
        1,
        EventKind::MessageDirectSent,
        "user.direct_host",
        "role.secretary",
        EventVisibility::Private,
        {"user.direct_host", "role.secretary"},
    });
    apply_ok(state, {
        6,
        1,
        EventKind::SpeechStarted,
        "role.secretary",
        "user.direct_host",
        EventVisibility::Private,
        {"user.direct_host", "role.secretary"},
    });
    check(!state.active_speaker_id().has_value(),
        "private speech must not claim the public meeting floor");
    check(state.last_sequence() == 6, "private events must preserve authoritative sequence order");
}

void private_events_are_restricted_to_direct_conversation_kinds() {
    MeetingState state;
    apply_ok(state, {1, 1, EventKind::RuntimeLeaseAcquired, "runtime.windows", ""});
    apply_ok(state, {2, 1, EventKind::RoleRegistered, "role.secretary", ""});
    apply_ok(state, {3, 1, EventKind::MeetingOpened, "runtime.windows", ""});

    const auto private_close = state.apply({
        4,
        1,
        EventKind::MeetingClosed,
        "runtime.windows",
        "",
        EventVisibility::Private,
        {"runtime.windows"},
    });
    check(private_close.error == ApplyError::InvalidTransition,
        "private lifecycle events must not bypass the public meeting state machine");
    check(state.last_sequence() == 3, "rejected private lifecycle events must not consume sequence");

    const auto wrong_direct_actor = state.apply({
        4,
        1,
        EventKind::MessageDirectSent,
        "role.secretary",
        "role.secretary",
        EventVisibility::Private,
        {"role.secretary"},
    });
    check(wrong_direct_actor.error == ApplyError::InvalidActor,
        "only the direct host may initiate a private message");

    const auto wrong_private_target = state.apply({
        4,
        1,
        EventKind::SpeechStarted,
        "role.secretary",
        "role.unknown",
        EventVisibility::Private,
        {"role.secretary", "role.unknown"},
    });
    check(wrong_private_target.error == ApplyError::InvalidActor,
        "private role activity must target the direct host");
    check(state.last_sequence() == 3, "rejected private events must preserve authoritative sequence");
}

void tool_approval_activity_is_private_and_unknown_kinds_fail_closed() {
    MeetingState state;
    apply_ok(state, {1, 1, EventKind::RuntimeLeaseAcquired, "runtime.windows", ""});
    apply_ok(state, {2, 1, EventKind::RoleRegistered, "role.secretary", ""});
    apply_ok(state, {3, 1, EventKind::MeetingOpened, "runtime.windows", ""});
    apply_ok(state, {
        4,
        1,
        EventKind::ToolApprovalRequested,
        "role.secretary",
        "user.direct_host",
        EventVisibility::Private,
        {"user.direct_host", "role.secretary"},
    });
    apply_ok(state, {
        5,
        1,
        EventKind::ToolApprovalResolved,
        "role.secretary",
        "user.direct_host",
        EventVisibility::Private,
        {"user.direct_host", "role.secretary"},
    });
    apply_ok(state, {
        6,
        1,
        EventKind::SubagentSpawned,
        "role.secretary",
        "role.secretary",
        EventVisibility::Private,
        {"user.direct_host", "role.secretary"},
    });

    const auto unknown = state.apply({
        7,
        1,
        static_cast<EventKind>(255),
        "role.secretary",
        "",
    });
    check(unknown.error == ApplyError::InvalidTransition, "unknown event kinds must fail closed");
    check(state.last_sequence() == 6, "an unknown event kind must not consume sequence");
}

void private_event_audiences_are_required_and_cannot_hide_routing_principals() {
    MeetingState state;
    apply_ok(state, {1, 1, EventKind::RuntimeLeaseAcquired, "runtime.windows", ""});
    apply_ok(state, {2, 1, EventKind::RoleRegistered, "role.secretary", ""});
    apply_ok(state, {3, 1, EventKind::MeetingOpened, "runtime.windows", ""});

    const auto missing = state.apply({
        4,
        1,
        EventKind::MessageDirectSent,
        "user.direct_host",
        "role.secretary",
        EventVisibility::Private,
    });
    check(missing.error == ApplyError::InvalidAudience,
        "private events must carry the protocol audience");

    const auto hidden_target = state.apply({
        4,
        1,
        EventKind::MessageDirectSent,
        "user.direct_host",
        "role.secretary",
        EventVisibility::Private,
        {"user.direct_host"},
    });
    check(hidden_target.error == ApplyError::InvalidAudience,
        "private audiences must include the routed target");

    const auto public_audience = state.apply({
        4,
        1,
        EventKind::MessagePublished,
        "user.direct_host",
        "",
        EventVisibility::Public,
        {"user.direct_host"},
    });
    check(public_audience.error == ApplyError::InvalidAudience,
        "public events must not carry a private audience");
}

void facilitated_discussion_control_events_preserve_core_invariants() {
    MeetingState state;
    apply_ok(state, {1, 1, EventKind::RuntimeLeaseAcquired, "runtime.windows", ""});
    apply_ok(state, {2, 1, EventKind::RoleRegistered, "role.facilitator", ""});
    apply_ok(state, {3, 1, EventKind::RoleRegistered, "role.critic", ""});
    apply_ok(state, {4, 1, EventKind::MeetingOpened, "runtime.windows", ""});
    apply_ok(state, {5, 1, EventKind::DiscussionConfigured, "user.direct_host", ""});
    apply_ok(state, {6, 1, EventKind::AgendaItemChanged, "runtime.windows", ""});
    apply_ok(state, {7, 1, EventKind::FloorRequested, "role.critic", "role.facilitator"});
    apply_ok(state, {8, 1, EventKind::FloorGranted, "runtime.windows", "role.critic"});
    apply_ok(state, {9, 1, EventKind::SpeechStarted, "role.critic", ""});
    apply_ok(state, {10, 1, EventKind::DiscussionBudgetUpdated, "runtime.windows", "role.critic"});
    apply_ok(state, {11, 1, EventKind::SpeechCompleted, "role.critic", ""});
    apply_ok(state, {12, 1, EventKind::DiscussionModeChanged, "runtime.windows", ""});
    apply_ok(state, {13, 1, EventKind::ConvergenceRecorded, "role.facilitator", ""});

    check(!state.active_speaker_id().has_value(),
        "scheduler control events must not claim or retain the public floor");
    check(state.last_sequence() == 13,
        "scheduler control events must consume the same authoritative sequence");

    const auto forged_grant = state.apply(
        {14, 1, EventKind::FloorGranted, "role.critic", "role.facilitator"});
    check(forged_grant.error == ApplyError::InvalidActor,
        "a role cannot forge a scheduler floor grant");
    const auto stale_budget = state.apply(
        {14, 0, EventKind::DiscussionBudgetUpdated, "runtime.windows", "role.critic"});
    check(stale_budget.error == ApplyError::StaleRuntimeGeneration,
        "scheduler events must retain runtime-generation fencing");
    check(state.last_sequence() == 13,
        "rejected scheduler events must not consume sequence");
}

}  // namespace

int main() {
    try {
        interruption_handoff_is_explicit();
        runtime_generation_fences_old_owner();
        c_api_exposes_stable_boundary();
        created_meeting_cannot_close_without_opening();
        higher_generation_supersedes_an_expired_owner();
        role_lifecycle_is_deterministic();
        c_api_exposes_role_lifecycle();
        runtime_owner_controls_meeting_phase();
        complete_local_roundtable_loop_is_deterministic();
        c_api_exposes_interruption_projection_and_errors();
        private_events_advance_sequence_without_taking_public_floor();
        private_events_are_restricted_to_direct_conversation_kinds();
        tool_approval_activity_is_private_and_unknown_kinds_fail_closed();
        private_event_audiences_are_required_and_cannot_hide_routing_principals();
        facilitated_discussion_control_events_preserve_core_invariants();
        std::cout << "pi_roundtable_core_tests: passed\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "pi_roundtable_core_tests: " << error.what() << '\n';
        return 1;
    }
}
