# Session-centered client model

## Product job

Pi Roundtable is a native Windows deliberation workspace for one human host who conducts durable, multi-role Agent sessions. The primary task is to reopen a session, understand who is present and what each participant may do, then continue the discussion without rebuilding runtime configuration. The current milestone restores the session definition and participant manifests; normalized transcript/event replay remains planned and must not yet be described as implemented.

## Information architecture

```text
┌─ Workspace rail ─────┬─ Active session ─────────────────────┬─ Context inspector ─────────┐
│ + New session        │ title · phase · runtime owner        │ Participant                  │
│ Recent sessions      │ agenda and participant strip         │  Overview                    │
│  Architecture review │                                      │  Prompt                      │
│  Product planning    │ normalized transcript                │  Model route                 │
│                      │                                      │  Skills                      │
│ Roles                │                                      │  MCP / tools                 │
│ Capability library   │ composer · target · interrupt        │  Memory / retention          │
│ Providers & models   │                                      │                              │
└──────────────────────┴──────────────────────────────────────┴──────────────────────────────┘
```

The session rail replaces the original role-first navigation. Roles remain visible inside the active session and in a reusable role library. Selecting a participant opens one inspector with the same vocabulary for long-term and temporary roles; temporary roles additionally show inviter, session-only scope, and end-of-session retention.

## Role editor

The role editor follows the useful part of the supplied reference image: one stable identity page with separate capability domains. It does not copy the product's issue-management navigation.

- **Overview**: identity, responsibilities, ownership, and lifecycle.
- **Prompt**: versioned system prompt and proposed revisions.
- **Model route**: primary provider/model, fallback order, thinking level, and budgets.
- **Skills**: references selected from the workspace Skill catalog.
- **MCP / tools**: server grants, exact tool allowlists, approval mode, and execution mode.
- **Memory**: session working set, long-term write policy, and visible retention decision.

The current Windows milestone edits provider/model routes, prompts, Skill references, MCP server attachments, invitation provenance, delegation mode, and retention. MCP attachments persist a deny-all tool allowlist with approval-required, SubAgent-preferred defaults; per-tool allowlist/approval/execution editing and the executor remain planned.

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
- Network-heavy capabilities default to isolated SubAgent execution with summary-and-citation return.
- Empty and error states name the missing configuration and link directly to the relevant editor.

## Reference boundaries

PromptX contributes layered role identity and separated working/long-term memory. Cherry Studio contributes global provider/MCP catalogs plus per-Agent attachment. Proma contributes workspace-scoped sessions and runtime selection. Multica contributes the distinction between reusable Agent configuration and an individual task/run, plus leader-mediated delegation. Pi Roundtable retains its own meeting lease, event ordering, interruption, and participant lifecycle instead of copying any product's internal types or orchestration model.

Official reference surfaces reviewed on 2026-08-01:

- [PromptX roles](https://promptx.deepractice.ai/docs/roles), [memory](https://promptx.deepractice.ai/docs/memory), and [ToolX](https://promptx.deepractice.ai/docs/toolx)
- [Cherry Studio Agent](https://docs.cherry-ai.com/docs/en-us/cherry-studio/preview/agent), [providers](https://docs.cherry-ai.com/docs/en-us/pre-basic/providers.md), and [MCP configuration](https://docs.cherry-ai.com/docs/en-us/advanced-basic/mcp/config)
- [Proma repository and product overview](https://github.com/proma-ai/Proma/blob/main/README.en.md)
- [Multica Agents](https://multica.ai/docs/agents), [providers](https://multica.ai/docs/providers), [workspaces](https://multica.ai/docs/workspaces), and [squads](https://multica.ai/docs/squads)
