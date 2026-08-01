# Runtime Host

This package is the only Oh My Pi compatibility boundary. `OmpRpcClient` starts `omp --mode rpc`, validates JSONL frames, negotiates protocol v2, reassembles bounded chunk sequences, and correlates command responses.

It deliberately does not render a terminal UI. Native applications host this process in the background and translate emitted frames into the normalized meeting protocol.

The adapter targets Oh My Pi release `v17.2.2` and must be exercised against the pinned runtime before production use. Prompt acknowledgement is not turn completion: callers must observe `agent_end`, `prompt_result`, or `data.agentInvoked: false`.
