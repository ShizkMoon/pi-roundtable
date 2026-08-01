#include "pi_roundtable/c_api.h"
#include "pi_roundtable/core/meeting_state.hpp"

#include <iostream>
#include <stdexcept>
#include <string>

using pi_roundtable::core::ApplyError;
using pi_roundtable::core::EventKind;
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
        std::cout << "pi_roundtable_core_tests: passed\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "pi_roundtable_core_tests: " << error.what() << '\n';
        return 1;
    }
}
