# ADR 0009: Facilitated discussion scheduling

- Status: Accepted
- Date: 2026-08-03

## Context

A useful roundtable needs more than a fixed loop in which every role produces a long answer. The opening agenda benefits from deliberate ordering and may need several passes. Later discussion should allow shorter replies, requests for the floor, and visible interruptions. The direct host and the facilitator role must be able to understand and control progress, and the runtime must stop a discussion that is consuming turns without converging.

The public meeting record is ordered and replayable. Streaming two role answers into that record at the same time would make cancellation, recovery, attribution, and human reading ambiguous. Pi sessions can perform private work concurrently, but that does not make concurrent public speech safe.

## Decision

The meeting will use a **parallel deliberation, serial public floor** model:

- Roles may inspect context, perform private work, and request a turn concurrently.
- Exactly one role owns the public floor. Every public delta remains attributable to one role and one command.
- The direct host and facilitator can see the agenda, current mode, round/turn budget, speaker, pending requests, and convergence state. The direct host can pause, reorder, grant, reject, interrupt, or end the discussion.
- Inline public text is the targeting surface, not an ordering grammar. A message may express shared requirements, individual assignments, subset collaboration, dependencies, and preferred sequencing in any natural order. `@角色` determines the allowed participant set; it does not force speaking order.
- After submission and before public dispatch, an isolated hidden Pi planning session classifies the message into shared requirements, per-role tasks, subset group tasks, and a proposed speaker order. It has no tools, Skill, MCP, SubAgent, role session, or persistent Pi transcript. The public event retains the user's original text unchanged, and the internal plan is not a UI or public protocol surface.
- Planner task text must be an exact excerpt from the original message, every planned role must belong to the explicit target set, and every target appears exactly once in the initial speaker order. Invalid, unavailable, or timed-out planning falls back to the explicit mention set without dropping or rewriting the message.
- Each role receives the complete public message plus only the hidden routing excerpts that apply to that role. It is instructed to follow the original message as authority, perform shared and assigned work, and not reveal the routing structure or answer for another role.
- A normal request for the floor does not cancel the speaker. A critical interruption records the interruptor, interrupted speaker, and reason, then follows the authoritative cancel-before-handoff sequence.

The facilitator scheduler is deterministic state above Pi and below the Windows view:

```text
agenda -> free_discussion -> convergence -> completed
   |            |                |
   +------------+----------------+-> paused
```

`agenda` uses agenda-item order, explicit mentions, facilitator proposals, and bounded follow-up rounds. Exhausting the current floor queue does not silently advance an item: the direct host explicitly advances it after any required follow-up passes. `free_discussion` uses short-turn defaults and a priority queue. `convergence` asks for unresolved objections, records decisions and open questions, then ends or returns to a specifically named agenda item. `paused` accepts only host control, cancellation, and safe persistence commands until resumed.

Within a mode, the scheduler grants the next turn using the following stable precedence:

1. a direct-host grant or validated semantic plan; inline mention order is the deterministic fallback;
2. an accepted critical-correction interruption;
3. a facilitator request tied to the active agenda item;
4. a reply to the immediately previous speaker;
5. other requests, ordered by request sequence with a fairness penalty for recent speakers.

Ties use the authoritative meeting sequence and then role identifier. Model confidence or wall-clock races never decide event order.

## Loop and density limits

Each configured discussion segment will carry additive limits:

- maximum public turns and maximum rounds;
- maximum consecutive turns by one role;
- per-turn duration/output budget;
- maximum accepted interruptions per segment and per role;
- a host-visible unresolved-question list;
- a no-progress counter advanced when a turn adds neither a decision, objection, evidence request, nor new action.

Reaching a soft limit moves the scheduler to `convergence`; reaching a hard limit pauses automation and asks the direct host to continue, revise the agenda, or end. The facilitator cannot silently extend a hard limit. A human stop, pause, or cancellation always wins.

The first no-progress implementation must use explicit structured facilitator outputs and deterministic counters. Semantic similarity may later assist the facilitator, but it cannot be the only reason for ending or suppressing a role.

## Interruption contract

An interruption request contains the interruptor, active target, public reason/instruction, request sequence, and severity. Only a request classified as a critical factual, safety, requirement, or process correction can cancel an active turn automatically, and automatic cancellation remains subject to the segment interruption budget. Other requests queue behind the active speaker.

The public event order remains:

1. `interruption.requested`;
2. `speech.cancelled` for the interrupted role;
3. `speech.started` for the granted interruptor.

The Windows timeline presents this as a control event near the affected messages rather than disguising it as an ordinary role response.

## Implementation status

Implemented in the current Windows local slice:

- pure inline single/multi-role targeting without a second responder-selector state;
- automatic hidden semantic planning through an isolated Pi session, with exact-source-excerpt validation and no tools, Skill, MCP, or SubAgent access;
- arbitrary-order shared requirements, per-role assignments, subset group tasks, and semantics-derived initial speaker order, with deterministic mention-order fallback;
- the original public text preserved unchanged while each role receives only its applicable hidden routing excerpts;
- one public speaker at a time;
- host-initiated, visible cancel-before-handoff interruption with its public reason;
- bounded manual cancellation and pause/resume foundations.

Implemented in the Windows-local Runtime Host milestone:

- additive discussion-mode, agenda-item, floor-request, budget, and convergence commands/events;
- a deterministic priority/fairness scheduler with generation-fenced normalized events and a restorable snapshot;
- agenda, short-turn free-discussion, convergence, paused, and completed modes;
- isolated role observers with no tools, Skill, MCP, or SubAgent access, exact-evidence validation, bounded output/time/probe budgets, an abortable three-call concurrency gate, one deduplicated final probe, and critical-interruption downgrade after the relevant speech has ended;
- cancel-before-handoff critical interruptions and queued non-critical requests without allowing model confidence or response timing to decide floor order;
- deterministic no-progress, soft-turn/round, hard-turn/round, consecutive-speaker, interruption, and observer-probe limits;
- a compact adaptive Windows projection for mode, active agenda item, round/turn/interruption budget, pending request count, mode control, resume, and agenda advance.

Non-speaking roles do not keep an idle Pi session “listening.” The Runtime Host launches a separate bounded observation session only after enough public text is available or the speech completes, deduplicates it before charging the probe, and discards invalid, late, duplicate, over-budget, or out-of-mode decisions. A terminal probe still runs once when the completion frame adds no new text. Public speech remains serial while at most three isolated observer calls run concurrently; queued calls are cancelled during stop.

## Consequences

- Public playback, recovery, and attribution stay deterministic even when private work becomes concurrent.
- Inline `@` text remains portable across Windows, exports, and future clients; no WinUI-only selection state enters the protocol.
- Hidden semantic planning completes before `message.published`; a crash during planning leaves the command outcome interrupted/unknown rather than publishing a message with no dispatch plan. The plan itself does not enter the public or sync event payload.
- The scheduler can be tested without model calls by replaying floor requests and control events.
- Role observation increases provider calls during free discussion, so the segment probe budget is visible, persisted, and deliberately small. A failed observer has no effect on the public floor.
- oh-my-pi remains a source of architectural ideas only. No compatibility layer, session import, or alternate runtime abstraction is introduced; Pi remains the only runtime integration.
