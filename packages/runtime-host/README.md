# Runtime Host

This package is the only Pi/Oh My Pi compatibility boundary. `RuntimeAdapter` is the domain-neutral contract that meeting orchestration consumes.

`PiRuntimeAdapter` is the implemented direct Pi SDK boundary. It creates one in-memory Pi session for one role, requires an exact provider/model plus a runtime-supplied API key, disables Pi's default tools unless the caller provides an explicit allowlist, normalizes Pi events, serializes lifecycle transitions, and keeps a bounded in-process command cache for idempotent retries. It does not persist credentials, expose raw Pi session records, or claim built-in subagent support.

`OmpRpcClient` is the implemented low-level optional compatibility client. It starts `omp --mode rpc`, validates JSONL frames, negotiates protocol v2, reassembles bounded chunk sequences, and correlates command responses. Raw OMP frames must be normalized by a future `OmpRuntimeAdapter` before they reach clients or the meeting core.

It deliberately does not render a terminal UI. Native applications host this process in the background and translate emitted frames into the normalized meeting protocol.

The compatibility transport targets Oh My Pi release `v17.2.2` and must be exercised against the pinned runtime before production use. Prompt acknowledgement is not turn completion: callers must observe `agent_end`, `prompt_result`, or `data.agentInvoked: false`.
