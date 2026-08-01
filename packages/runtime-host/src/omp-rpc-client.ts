import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { createInterface, type Interface as ReadLineInterface } from "node:readline";

import { RpcFrameDecoder } from "./rpc-frame-decoder.js";
import {
  isRpcReadyFrame,
  isRpcResponse,
  type InterruptMode,
  type RpcFrameListener,
  type RpcReadyFrame,
  type RpcRecord,
  type RpcResponse,
  type StreamingBehavior,
  type SubagentSubscription,
} from "./rpc-types.js";

interface PendingRequest {
  resolve: (response: RpcResponse) => void;
  reject: (error: Error) => void;
  timeout: NodeJS.Timeout;
}

export interface OmpRpcClientOptions {
  command?: string;
  args?: string[];
  launchArgs?: string[];
  cwd?: string;
  environment?: NodeJS.ProcessEnv;
  preferProtocolV2?: boolean;
  startupTimeoutMs?: number;
  requestTimeoutMs?: number;
}

export class OmpRpcError extends Error {
  constructor(
    message: string,
    readonly response?: RpcResponse,
  ) {
    super(message);
    this.name = "OmpRpcError";
  }
}

export class OmpRpcClient {
  readonly #options: OmpRpcClientOptions;
  readonly #decoder = new RpcFrameDecoder();
  readonly #listeners = new Set<RpcFrameListener>();
  readonly #pending = new Map<string, PendingRequest>();
  #child: ChildProcessWithoutNullStreams | undefined;
  #lineReader: ReadLineInterface | undefined;
  #requestCounter = 0;
  #readyResolve: ((frame: RpcReadyFrame) => void) | undefined;
  #readyReject: ((error: Error) => void) | undefined;

  constructor(options: OmpRpcClientOptions = {}) {
    this.#options = options;
  }

  async start(): Promise<RpcReadyFrame> {
    if (this.#child !== undefined) {
      throw new OmpRpcError("OMP RPC client is already started");
    }

    const readyPromise = new Promise<RpcReadyFrame>((resolve, reject) => {
      this.#readyResolve = resolve;
      this.#readyReject = reject;
    });

    const command = this.#options.command ?? "omp";
    const args = this.#options.launchArgs ?? ["--mode", "rpc", ...(this.#options.args ?? [])];
    const spawnOptions: Parameters<typeof spawn>[2] = {
      env: { ...process.env, ...this.#options.environment },
      stdio: ["pipe", "pipe", "pipe"],
      windowsHide: true,
    };
    if (this.#options.cwd !== undefined) {
      spawnOptions.cwd = this.#options.cwd;
    }

    const child = spawn(command, args, spawnOptions) as ChildProcessWithoutNullStreams;
    this.#child = child;
    this.#lineReader = createInterface({ input: child.stdout, crlfDelay: Infinity });
    this.#lineReader.on("line", (line) => this.#onLine(line, child));
    child.once("error", (error) => this.#failTransport(error, child));
    child.once("exit", (code, signal) => {
      if (this.#child === child) {
        this.#failTransport(
          new OmpRpcError(`OMP RPC process exited (code=${String(code)}, signal=${String(signal)})`),
          child,
        );
      }
    });
    child.stderr.setEncoding("utf8");
    child.stderr.on("data", (chunk: string) => {
      this.#emit({ type: "runtime_stderr", text: chunk });
    });

    const startupTimeoutMs = this.#options.startupTimeoutMs ?? 10_000;
    let ready: RpcReadyFrame;
    try {
      ready = await this.#withTimeout(
        readyPromise,
        startupTimeoutMs,
        "timed out waiting for OMP ready frame",
      );
    } catch (error) {
      const failure = error instanceof Error ? error : new Error(String(error));
      this.#failTransport(failure, child);
      child.kill();
      throw failure;
    } finally {
      this.#readyResolve = undefined;
      this.#readyReject = undefined;
    }

    try {
      this.#decoder.configureLimits(ready.maxFrameBytes, ready.maxReassembledFrameBytes);
      if (
        this.#options.preferProtocolV2 !== false &&
        ready.supportedProtocolVersions.includes(2)
      ) {
        await this.request("negotiate_protocol", { protocolVersion: 2 });
      }
      return ready;
    } catch (error) {
      const failure = error instanceof Error ? error : new Error(String(error));
      this.#failTransport(failure, child);
      if (!child.killed) {
        child.kill();
      }
      throw failure;
    }
  }

  async stop(): Promise<void> {
    const child = this.#child;
    if (child === undefined) {
      return;
    }

    this.#child = undefined;
    this.#lineReader?.close();
    this.#lineReader = undefined;
    child.stdin.end();

    await Promise.race([
      new Promise<void>((resolve) => child.once("exit", () => resolve())),
      new Promise<void>((resolve) => {
        setTimeout(() => {
          if (!child.killed) {
            child.kill();
          }
          resolve();
        }, 2_000).unref();
      }),
    ]);
    this.#rejectPending(new OmpRpcError("OMP RPC client stopped"));
  }

  subscribe(listener: RpcFrameListener): () => void {
    this.#listeners.add(listener);
    return () => this.#listeners.delete(listener);
  }

  prompt(message: string, streamingBehavior?: StreamingBehavior): Promise<RpcResponse> {
    const fields: Record<string, unknown> = { message };
    if (streamingBehavior !== undefined) {
      fields.streamingBehavior = streamingBehavior;
    }
    return this.request("prompt", fields);
  }

  abort(): Promise<RpcResponse> {
    return this.request("abort");
  }

  abortAndPrompt(message: string): Promise<RpcResponse> {
    return this.request("abort_and_prompt", { message });
  }

  steer(message: string): Promise<RpcResponse> {
    return this.request("steer", { message });
  }

  followUp(message: string): Promise<RpcResponse> {
    return this.request("follow_up", { message });
  }

  setInterruptMode(mode: InterruptMode): Promise<RpcResponse> {
    return this.request("set_interrupt_mode", { mode });
  }

  setSubagentSubscription(level: SubagentSubscription): Promise<RpcResponse> {
    return this.request("set_subagent_subscription", { level });
  }

  request(command: string, fields: Record<string, unknown> = {}): Promise<RpcResponse> {
    const child = this.#child;
    if (child === undefined || !child.stdin.writable) {
      return Promise.reject(new OmpRpcError("OMP RPC client is not running"));
    }

    const id = `pi-rt-${++this.#requestCounter}`;
    const timeoutMs = this.#options.requestTimeoutMs ?? 30_000;
    return new Promise<RpcResponse>((resolve, reject) => {
      const timeout = setTimeout(() => {
        this.#pending.delete(id);
        reject(new OmpRpcError(`OMP RPC request timed out: ${command}`));
      }, timeoutMs);
      timeout.unref();
      this.#pending.set(id, { resolve, reject, timeout });

      const line = `${JSON.stringify({ ...fields, id, type: command })}\n`;
      child.stdin.write(line, "utf8", (error) => {
        if (error !== null && error !== undefined) {
          const pending = this.#pending.get(id);
          if (pending !== undefined) {
            clearTimeout(pending.timeout);
            this.#pending.delete(id);
            pending.reject(error);
          }
        }
      });
    });
  }

  #onLine(line: string, child: ChildProcessWithoutNullStreams): void {
    try {
      for (const frame of this.#decoder.pushLine(line)) {
        this.#handleFrame(frame);
      }
    } catch (error) {
      this.#failTransport(error instanceof Error ? error : new Error(String(error)), child);
      if (!child.killed) {
        child.kill();
      }
    }
  }

  #handleFrame(frame: RpcRecord): void {
    if (frame.type === "ready") {
      if (!isRpcReadyFrame(frame)) {
        throw new OmpRpcError("OMP emitted an invalid ready frame");
      }
      this.#readyResolve?.(frame);
      this.#emit(frame);
      return;
    }

    if (isRpcResponse(frame) && typeof frame.id === "string") {
      const pending = this.#pending.get(frame.id);
      if (pending !== undefined) {
        clearTimeout(pending.timeout);
        this.#pending.delete(frame.id);
        if (frame.success) {
          pending.resolve(frame);
        } else {
          pending.reject(new OmpRpcError(frame.error ?? `OMP command failed: ${frame.command}`, frame));
        }
        return;
      }
    }

    this.#emit(frame);
  }

  #emit(frame: RpcRecord): void {
    for (const listener of this.#listeners) {
      listener(frame);
    }
  }

  #failTransport(error: Error, sourceChild: ChildProcessWithoutNullStreams): void {
    if (this.#child !== sourceChild) {
      return;
    }
    this.#child = undefined;
    this.#lineReader?.close();
    this.#lineReader = undefined;
    this.#readyReject?.(error);
    this.#rejectPending(error);
    this.#decoder.reset();
    this.#emit({ type: "runtime_error", message: error.message });
  }

  #rejectPending(error: Error): void {
    for (const pending of this.#pending.values()) {
      clearTimeout(pending.timeout);
      pending.reject(error);
    }
    this.#pending.clear();
  }

  async #withTimeout<T>(promise: Promise<T>, timeoutMs: number, message: string): Promise<T> {
    let timeout: NodeJS.Timeout | undefined;
    try {
      return await Promise.race([
        promise,
        new Promise<T>((_resolve, reject) => {
          timeout = setTimeout(() => reject(new OmpRpcError(message)), timeoutMs);
          timeout.unref();
        }),
      ]);
    } finally {
      if (timeout !== undefined) {
        clearTimeout(timeout);
      }
    }
  }
}
