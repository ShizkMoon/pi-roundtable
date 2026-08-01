# Runtime Host

This package is the only Pi/Oh My Pi compatibility boundary. `RuntimeAdapter` is the domain-neutral contract that meeting orchestration consumes.

`PiRuntimeAdapter` is the implemented direct Pi SDK boundary. It creates one in-memory Pi session for one role, requires an exact provider/model plus a runtime-supplied API key, overrides the role System Prompt, disables default Skill discovery and loads only explicitly granted Skill paths, disables Pi's default tools unless the caller provides an explicit allowlist, normalizes Pi events, serializes lifecycle transitions, and keeps a bounded in-process command cache for idempotent retries. It does not persist credentials, expose raw Pi session records, execute MCP grants yet, or claim built-in subagent support.

`LocalRoundtableHost` is the implemented in-memory meeting orchestrator. It owns the local generation and sequence, creates one adapter per role, accepts public meeting commands, emits normalized meeting events, and performs the basic interruption handoff. `StdioRuntimeHost` exposes that orchestrator to the Windows application through bounded JSONL frames; stdout is protocol-only and diagnostics use explicit error frames or stderr.

Run `npm run build` at the repository root, then start the process with `npm run dev:runtime` after setting `PI_ROUNDTABLE_MEETING_ID`; `PI_ROUNDTABLE_RUNTIME_ID`, generation, and working directory remain optional. The supervisor must send one local-protocol-v2 initialize frame containing the validated non-secret workspace profile plus a `credentialRef`-keyed credential map before commands. Role commands carry a normalized frozen participant manifest; the Host resolves private Pi provider/model/prompt/Skill inputs from those two documents. Credential values stay in process memory and are never included in environment variables, events, receipts, or diagnostics.

`OmpRpcClient` is the implemented low-level optional compatibility client. It starts `omp --mode rpc`, validates JSONL frames, negotiates protocol v2, reassembles bounded chunk sequences, and correlates command responses. Raw OMP frames must be normalized by a future `OmpRuntimeAdapter` before they reach clients or the meeting core.

It deliberately does not render a terminal UI. Native applications host this process in the background and translate emitted frames into the normalized meeting protocol.

The compatibility transport targets Oh My Pi release `v17.2.2` and must be exercised against the pinned runtime before production use. Prompt acknowledgement is not turn completion: callers must observe `agent_end`, `prompt_result`, or `data.agentInvoked: false`.
