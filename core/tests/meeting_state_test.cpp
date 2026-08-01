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

}  // namespace

int main() {
    try {
        interruption_handoff_is_explicit();
        runtime_generation_fences_old_owner();
        c_api_exposes_stable_boundary();
        created_meeting_cannot_close_without_opening();
        higher_generation_supersedes_an_expired_owner();
        std::cout << "pi_roundtable_core_tests: passed\n";
        return 0;
    } catch (const std::exception& error) {
        std::cerr << "pi_roundtable_core_tests: " << error.what() << '\n';
        return 1;
    }
}
