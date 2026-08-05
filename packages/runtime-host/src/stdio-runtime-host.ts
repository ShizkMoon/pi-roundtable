import { createInterface } from "node:readline";
import type { Readable, Writable } from "node:stream";

import type { RuntimeCapabilities } from "./runtime-adapter.js";
import {
  validateRoundtableSession,
  validateWorkspaceProfile,
} from "@pi-roundtable/protocol";
import {
  LOCAL_HOST_PROTOCOL_VERSION,
  LocalHostProtocolError,
  parseLocalHostInput,
  type LocalHostOutputFrame,
} from "./local-host-protocol.js";
import {
  LocalRoundtableHost,
  type LocalHostStopMode,
} from "./local-roundtable-host.js";

const HOST_CAPABILITIES: RuntimeCapabilities = {
  steering: true,
  followUp: true,
  cancellation: true,
  tools: false,
  subagents: false,
};

class OrderedFrameWriter {
  #tail: Promise<void> = Promise.resolve();
  #failure: unknown;

  constructor(private readonly output: Writable) {}

  write(frame: LocalHostOutputFrame): Promise<void> {
    const operation = this.#tail.then(async () => {
      if (this.#failure !== undefined) {
        throw this.#failure;
      }
      const data = `${JSON.stringify(frame)}\n`;
      await new Promise<void>((resolve, reject) => {
        this.output.write(data, (error) => {
          if (error !== null && error !== undefined) {
            reject(error);
          } else {
            resolve();
          }
        });
      });
    });
    this.#tail = operation.catch((error: unknown) => {
      this.#failure ??= error;
    });
    return operation;
  }
}

export class StdioRuntimeHost {
  constructor(private readonly host: LocalRoundtableHost) {}

  async run(input: Readable, output: Writable): Promise<void> {
    const writer = new OrderedFrameWriter(output);
    const pendingInitializationFrames: LocalHostOutputFrame[] = [];
    let outboundReady = false;
    const queueEvent = (frame: LocalHostOutputFrame): void => {
      if (!outboundReady) {
        pendingInitializationFrames.push(frame);
        return;
      }
      void writer.write(frame).catch(() => {
        // The command loop observes the same poisoned writer and terminates.
      });
    };
    const unsubscribeEvents = this.host.subscribe((event) =>
      queueEvent({ type: "event", event }),
    );
    const unsubscribeDiagnostics = this.host.subscribeDiagnostics((errorCode, message) =>
      queueEvent({ type: "error", requestId: null, errorCode, message }),
    );

    const lines = createInterface({ input, crlfDelay: Infinity, terminal: false });
    let initialized = false;
    let shutdownRequestId: string | null = null;
    let shutdownMode: LocalHostStopMode = "suspend";
    let terminateAfterResponse = false;
    let hostOperationPending = false;
    let preemptiveStop: Promise<void> | undefined;
    const requestPreemptiveStop = (
      mode: LocalHostStopMode,
      requestId: string | null,
    ): void => {
      setImmediate(() => {
        // Give normally settling operations one turn to preserve FIFO behavior.
        // A genuinely stalled adapter/planner is then fenced even though the
        // sequential command loop has not yet consumed the shutdown/EOF.
        if (!hostOperationPending || preemptiveStop !== undefined) {
          return;
        }
        shutdownMode = mode;
        shutdownRequestId = requestId;
        preemptiveStop = this.host.stop(mode);
        void preemptiveStop.catch(() => undefined);
      });
    };
    const observeLineForShutdown = (line: string): void => {
      if (!hostOperationPending || line.length === 0) {
        return;
      }
      try {
        const frame = parseLocalHostInput(line);
        if (frame.type === "shutdown") {
          requestPreemptiveStop(frame.mode, frame.requestId);
        }
      } catch {
        // The ordered loop remains authoritative for parse errors.
      }
    };
    const observeInputClose = (): void => {
      if (hostOperationPending) {
        requestPreemptiveStop("suspend", null);
      }
    };
    lines.on("line", observeLineForShutdown);
    lines.once("close", observeInputClose);
    try {
      for await (const line of lines) {
        if (line.length === 0) {
          continue;
        }

        let response: LocalHostOutputFrame | undefined;
        try {
          const frame = parseLocalHostInput(line);
          if (!initialized) {
            if (frame.type === "shutdown") {
              shutdownRequestId = frame.requestId;
              shutdownMode = frame.mode;
              break;
            }
            if (frame.type !== "initialize") {
              response = {
                type: "error",
                requestId: null,
                errorCode: "initialization_required",
                message: "Initialize the Runtime Host before sending commands",
              };
            } else {
              try {
                const issues = validateWorkspaceProfile(frame.workspace);
                if (issues.length > 0) {
                  throw new LocalHostProtocolError(
                    "invalid_workspace",
                    "Runtime workspace configuration failed integrity validation",
                    frame.requestId,
                  );
                }
                const sessionIssues = validateRoundtableSession(frame.session, frame.workspace);
                if (sessionIssues.length > 0) {
                  throw new LocalHostProtocolError(
                    "invalid_session",
                    "Roundtable session failed integrity validation",
                    frame.requestId,
                  );
                }
                this.host.initializeRuntimeConfiguration(
                  frame.workspace,
                  frame.session,
                  frame.credentials,
                  frame.initialSequence,
                  frame.discussionState,
                  frame.roleMemoryRecall,
                  frame.recoveryContext,
                );
                this.host.start();
                if (frame.session.phase === "live") {
                  hostOperationPending = true;
                  try {
                    await this.host.restoreConfiguredRoles();
                  } finally {
                    hostOperationPending = false;
                  }
                }
                initialized = true;
                await writer.write({
                  type: "ready",
                  protocolVersion: LOCAL_HOST_PROTOCOL_VERSION,
                  meetingId: this.host.meetingId,
                  runtimeId: this.host.runtimeId,
                  runtimeGeneration: this.host.runtimeGeneration,
                  sequence: this.host.sequence,
                  capabilities: HOST_CAPABILITIES,
                });
                while (pendingInitializationFrames.length > 0) {
                  await writer.write(pendingInitializationFrames.shift()!);
                }
                outboundReady = true;
              } catch {
                pendingInitializationFrames.length = 0;
                // Initialization can already have created role adapters and
                // credential leases. Treat failure as terminal so the final
                // stop path closes them instead of retaining secrets while
                // waiting for another frame on the same process.
                terminateAfterResponse = true;
                response = {
                  type: "error",
                  requestId: frame.requestId,
                  errorCode: "initialization_failed",
                  message: "Runtime Host initialization failed",
                };
              }
            }
          } else if (frame.type === "initialize") {
            response = {
              type: "error",
              requestId: frame.requestId,
              errorCode: "already_initialized",
              message: "Runtime Host is already initialized",
            };
          } else if (frame.type === "shutdown") {
            shutdownRequestId = frame.requestId;
            shutdownMode = frame.mode;
            break;
          } else {
            hostOperationPending = true;
            try {
              response = { type: "receipt", receipt: await this.host.execute(frame.command) };
            } finally {
              hostOperationPending = false;
            }
          }
        } catch (error) {
          response =
            error instanceof LocalHostProtocolError
              ? {
                  type: "error",
                  requestId: error.requestId,
                  errorCode: error.code,
                  message: error.message,
                }
              : {
                  type: "error",
                  requestId: null,
                  errorCode: "host_error",
                  message: "Runtime Host could not process the input frame",
                };
        }

        if (response !== undefined) {
          await writer.write(response);
        }
        if (terminateAfterResponse) {
          break;
        }
      }
    } finally {
      lines.off("line", observeLineForShutdown);
      lines.off("close", observeInputClose);
      // A protocol shutdown is authoritative even when the parent keeps its
      // stdin pipe open. Release readline's input listeners so the Runtime Host
      // process can finish instead of waiting indefinitely for EOF.
      lines.close();
      let stopFailure: unknown;
      try {
        await (preemptiveStop ?? this.host.stop(shutdownMode));
      } catch (error) {
        stopFailure = error;
      } finally {
        unsubscribeEvents();
        unsubscribeDiagnostics();
      }
      await writer.write({ type: "stopped", requestId: shutdownRequestId });
      if (stopFailure !== undefined) {
        throw stopFailure;
      }
    }
  }
}
