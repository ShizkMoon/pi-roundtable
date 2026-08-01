#pragma once

#include <stdint.h>

#if defined(_WIN32)
#if defined(PR_CORE_EXPORTS)
#define PR_API __declspec(dllexport)
#else
#define PR_API __declspec(dllimport)
#endif
#else
#define PR_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct pr_meeting pr_meeting;

typedef enum pr_event_kind {
    PR_EVENT_RUNTIME_LEASE_ACQUIRED = 0,
    PR_EVENT_RUNTIME_LEASE_RELEASED = 1,
    PR_EVENT_MEETING_OPENED = 2,
    PR_EVENT_MEETING_CLOSED = 3,
    PR_EVENT_ROLE_REGISTERED = 4,
    PR_EVENT_ROLE_LEFT = 5,
    PR_EVENT_SPEECH_STARTED = 6,
    PR_EVENT_SPEECH_DELTA = 7,
    PR_EVENT_SPEECH_COMPLETED = 8,
    PR_EVENT_SPEECH_CANCELLED = 9,
    PR_EVENT_INTERRUPTION_REQUESTED = 10,
    PR_EVENT_TOOL_STARTED = 11,
    PR_EVENT_TOOL_COMPLETED = 12,
    PR_EVENT_TOOL_FAILED = 13,
    PR_EVENT_SUBAGENT_SPAWNED = 14,
    PR_EVENT_SUBAGENT_PROGRESS = 15,
    PR_EVENT_SUBAGENT_COMPLETED = 16,
    PR_EVENT_SUBAGENT_FAILED = 17,
    PR_EVENT_ROLE_TEMPORARY_REGISTERED = 18,
    PR_EVENT_ROLE_PROMOTED = 19,
    PR_EVENT_ROLE_ARCHIVED = 20
} pr_event_kind;

typedef enum pr_apply_error {
    PR_APPLY_OK = 0,
    PR_APPLY_OUT_OF_ORDER_SEQUENCE = 1,
    PR_APPLY_LEASE_REQUIRED = 2,
    PR_APPLY_STALE_RUNTIME_GENERATION = 3,
    PR_APPLY_INVALID_RUNTIME_GENERATION = 4,
    PR_APPLY_INVALID_TRANSITION = 5,
    PR_APPLY_INVALID_ACTOR = 6,
    PR_APPLY_UNKNOWN_ROLE = 7,
    PR_APPLY_DUPLICATE_ROLE = 8,
    PR_APPLY_FLOOR_BUSY = 9,
    PR_APPLY_NO_ACTIVE_SPEAKER = 10,
    PR_APPLY_INTERRUPTION_PENDING = 11,
    PR_APPLY_ROLE_NOT_TEMPORARY = 12,
    PR_APPLY_ROLE_ARCHIVED = 13
} pr_apply_error;

typedef enum pr_role_scope {
    PR_ROLE_SCOPE_UNKNOWN = -1,
    PR_ROLE_SCOPE_LONG_TERM = 0,
    PR_ROLE_SCOPE_TEMPORARY = 1
} pr_role_scope;

typedef enum pr_meeting_phase {
    PR_PHASE_CREATED = 0,
    PR_PHASE_LIVE = 1,
    PR_PHASE_CLOSED = 2
} pr_meeting_phase;

typedef struct pr_event {
    uint64_t sequence;
    uint64_t runtime_generation;
    pr_event_kind kind;
    const char* actor_id;
    const char* target_id;
} pr_event;

typedef struct pr_apply_result {
    pr_apply_error error;
    uint64_t expected_sequence;
} pr_apply_result;

PR_API pr_meeting* pr_meeting_create(void);
PR_API void pr_meeting_destroy(pr_meeting* meeting);
PR_API pr_apply_result pr_meeting_apply(pr_meeting* meeting, const pr_event* event);
PR_API uint64_t pr_meeting_last_sequence(const pr_meeting* meeting);
PR_API uint64_t pr_meeting_runtime_generation(const pr_meeting* meeting);
PR_API int pr_meeting_has_active_lease(const pr_meeting* meeting);
PR_API pr_meeting_phase pr_meeting_get_phase(const pr_meeting* meeting);
PR_API uint64_t pr_meeting_role_count(const pr_meeting* meeting);
PR_API int pr_meeting_has_role(const pr_meeting* meeting, const char* role_id);
PR_API pr_role_scope pr_meeting_role_scope(const pr_meeting* meeting, const char* role_id);
PR_API int pr_meeting_role_is_archived(const pr_meeting* meeting, const char* role_id);

// Borrowed pointers remain valid until the next successful apply or destroy.
PR_API const char* pr_meeting_runtime_owner(const pr_meeting* meeting);
PR_API const char* pr_meeting_active_speaker(const pr_meeting* meeting);
PR_API const char* pr_meeting_pending_interruptor(const pr_meeting* meeting);
PR_API const char* pr_meeting_pending_interrupt_target(const pr_meeting* meeting);
PR_API const char* pr_apply_error_message(pr_apply_error error);

#ifdef __cplusplus
}
#endif
