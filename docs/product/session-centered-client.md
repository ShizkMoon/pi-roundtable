# Session-centered client model

## Product job

Pi Roundtable is a native Windows deliberation workspace for one human host who conducts durable, multi-role Agent sessions. The primary task is to reopen a session, understand who is present and what each participant may do, then continue the discussion without rebuilding runtime configuration. The current milestone restores the session definition and participant manifests, replays DPAPI-protected normalized public/private events through the deterministic core, then resumes the local Runtime Host under the next `runtimeGeneration`.

## Information architecture

```text
┌─ Session and page rail ┬─ Public meeting record ─────────────┬─ Private context ───────────┐
│ group: folder/project  │ title · phase · participants        │ private chat with one role   │
│ + New session          │                                     │ or                           │
│ grouped sessions       │ public role and host messages       │ role status / prompt /       │
│                        │                                     │ safe activity summary        │
│ Role management        │ @ one or more roles                 │                              │
│                        │ host composer visible to all roles  │ back to private chat         │
│ Skill                  │                                     │                              │
│ MCP                    │                                     │                              │
│ Settings               │                                     │                              │
└────────────────────────┴─────────────────────────────────────┴──────────────────────────────┘
```

The session rail replaces the original role-first navigation. Sessions may be filtered by project-style or folder-style groups. Role management, Skill, MCP, and Settings are separate workspace pages rather than right-column tabs. The center column belongs only to the public meeting record and host composer. The right column normally carries a private one-role thread; selecting a speaker in the public record temporarily replaces it with a safe role inspector and provides an explicit back path.

## Role editor

The role editor follows the useful part of the supplied reference image: one stable identity page with separate capability domains. It does not copy the product's issue-management navigation.

- **Overview**: identity, responsibilities, ownership, and lifecycle.
- **Prompt**: versioned system prompt and proposed revisions.
- **Model route**: primary provider/model, fallback order, thinking level, and budgets.
- **Skills**: references selected from the workspace Skill catalog.
- **MCP / tools**: server grants, exact tool allowlists, approval mode, and execution mode.
- **Memory**: session working set, long-term write policy, and visible retention decision.

The current Windows milestone edits provider/model routes, prompts, Skill references, MCP server attachments, exact per-tool allowlists, invitation provenance, delegation mode, and retention. Imported MCP servers remain disabled until explicit catalog approval, then a role attachment authorizes that server for the participant snapshot. The Runtime Host executes only allowlisted discovered tools with bounded input/output and transport checks; an empty allowlist exposes no tool. `always` and first-use policies pause the tool before side effects and show the host only sanitized server/tool identity; arguments and results do not enter approval events. Approvals expire and restore focus without widening the grant. Isolated Pi SubAgents are implemented without MCP tools or recursive delegation, with at most two concurrent children per parent role and private result return.

Creating a temporary role uses the same editor as a required review sheet. “Invite to session” stays disabled until purpose, prompt, model route, and retention are complete. Skills/MCP may be empty, but absence must be explicit.

## Visual direction

The client remains native and quiet, with one distinctive element: a **capability grant strip** under every participant header. Compact tokens show model, Skill count, MCP tool count, approval mode, and whether network work is delegated. This makes authority visible without turning the transcript into an administration screen.

| Token | Value | Use |
| --- | --- | --- |
| Mica fog | `#F3F3F3` | window background |
| Paper | `#FFFFFF` | active surfaces |
| Ink | `#1B1B1F` | primary text |
| Session blue | `#0F6CBD` | primary actions and active session |
| Delegate teal | `#0B6E69` | SubAgent-routed capabilities |
| Review amber | `#9A6700` | approval-required grants |

Segoe UI Variable carries interface text; Cascadia Mono is reserved for model IDs, runtime generations, and capability identifiers. Native theme resources map these tokens in production so dark mode and contrast settings remain correct.

## Interaction rules

- A session can be drafted before a Runtime Host starts.
- Provider/model changes are saved explicitly and reused; secrets are never displayed after storage.
- Editing a long-term role affects future participant bindings, not frozen manifests in an active or closed session.
- Grant changes during a live session are commands and normalized events, not silent local edits.
- Network-heavy capabilities may carry a `subagent_preferred` or `subagent_required` policy marker. Isolated SubAgent execution is implemented; automatic citation validation and artifact-return contracts remain a later milestone.
- Empty and error states name the missing configuration and link directly to the relevant editor.
- A host message may mention one or more roles, but it remains public and is eventually available to every participant's public context.
- Only mentioned roles enter that response queue. Each target receives an identity-bound turn prompt and answers only as itself; it may cite another public statement but must not draft another mentioned role's answer.
- A private host message is visible only to the host and its selected role. It is never injected into another role's context.
- The role inspector reports normalized status and activity summaries. Provider chain-of-thought and hidden reasoning are not a client surface.
- At wide widths all three meeting columns are visible. Medium widths keep the session rail and move private context to an overlay; compact widths move both side panes to overlays.

## Milestone status

- **Implemented:** session groups and session-click navigation; separate Role/Skill/MCP/Settings pages; compact virtualized public transcript with read-only participant list; strict inline public single/multi-mention, isolated hidden Pi semantic planning for arbitrary-order shared/per-role/subset tasks and initial speaker order, exact-source-excerpt validation with deterministic fallback, unchanged public source text, identity-bound role-exclusive responses, visible host-initiated interruption handoff, and one-role private commands whose audience survives the Windows/C ABI/C++ boundary; deterministic agenda/free-discussion/convergence/paused/completed scheduling, explicit host agenda advance, priority/fairness floor requests, bounded three-way isolated role observation and critical抢答, short-turn prompts, no-progress/soft/hard loop limits, automatic convergence, and an adaptive Windows mode/agenda/budget/queue strip; native safe Markdown, LaTeX-source fallback/copy, code copy and throttled streaming render; latest-follow with user-scroll escape; safe role-detail/back flow; adaptive side panes and named keyboard-accessible controls; durable provider/model/theme/sync settings; provider model-list discovery/import; Windows Credential Manager; bounded Git checkout, redacted LLM repository review, isolated atomic Skill/MCP install, catalog approval lifecycle; exact deny-by-default MCP tool allowlists; approved MCP discovery/execution and expiring private approval; bounded non-recursive Pi SubAgents with activity UI; DPAPI-protected SQLite events plus durable command receipts, deterministic replay, pause/resume, checkpoint/event-tail validation, turn-boundary recovery, and restored discussion scheduler snapshots; Markdown/JSON session export and non-destructive import preview; self-contained unsigned x64 MSI; pinned-key signed update manifests and update helper.
- **Implemented backend / client integration pending:** signed device-token remote authentication, private audience authorization, cursor replay/SSE, and optional transactional PostgreSQL storage. The Windows/Android clients do not yet claim an end-to-end production remote-sync experience.
- **Planned or pending:** editable multi-item agenda authoring beyond the current session subject/objectives, richer host review/editing of convergence records, and calibrated observer policies from longer real-meeting trials; full mathematical typesetting beyond the safe LaTeX-source fallback; client E2EE and key-envelope management; TLS/rate limits/retention/multi-replica notifications; durable long-term memory and prompt-revision execution; citation/artifact validation for delegated work; Authenticode-signed MSI, complete install/uninstall/upgrade/repair matrix; ARM64 packaging.

## Reference boundaries

PromptX contributes layered role identity and separated working/long-term memory. Cherry Studio contributes global provider/MCP catalogs plus per-Agent attachment. Proma contributes workspace-scoped sessions and runtime selection. Multica contributes the distinction between reusable Agent configuration and an individual task/run, plus leader-mediated delegation. ReasoniX contributes append-only/checkpoint and anti-storm review ideas. Historical Oh My Pi work contributes only process supervision, framed streaming, cancellation, and adapter-boundary lessons. Pi Roundtable keeps Pi as its sole runtime and retains its own meeting lease, event ordering, interruption, participant lifecycle, protocol, sessions, and commands; it does not implement Oh My Pi compatibility or copy any product's internal types/orchestration model.

Official reference surfaces reviewed on 2026-08-01:

- [PromptX roles](https://promptx.deepractice.ai/docs/roles), [memory](https://promptx.deepractice.ai/docs/memory), and [ToolX](https://promptx.deepractice.ai/docs/toolx)
- [Cherry Studio Agent](https://docs.cherry-ai.com/docs/en-us/cherry-studio/preview/agent), [providers](https://docs.cherry-ai.com/docs/en-us/pre-basic/providers.md), and [MCP configuration](https://docs.cherry-ai.com/docs/en-us/advanced-basic/mcp/config)
- [Proma repository and product overview](https://github.com/proma-ai/Proma/blob/main/README.en.md)
- [Multica Agents](https://multica.ai/docs/agents), [providers](https://multica.ai/docs/providers), [workspaces](https://multica.ai/docs/workspaces), and [squads](https://multica.ai/docs/squads)
