import { Buffer } from "node:buffer";
import { TextDecoder } from "node:util";

import type { RpcRecord } from "./rpc-types.js";

const DEFAULT_MAX_FRAME_BYTES = 1_048_576;
const DEFAULT_MAX_REASSEMBLED_FRAME_BYTES = 67_108_864;
const MAX_CHUNK_COUNT = 1_000_000;
const BASE64_PATTERN = /^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/;

interface RpcChunkFrame extends RpcRecord {
  type: "rpc_chunk";
  chunkId: string;
  index: number;
  count: number;
  byteLength: number;
  data: string;
}

interface ActiveChunkSequence {
  chunkId: string;
  count: number;
  byteLength: number;
  nextIndex: number;
  receivedBytes: number;
  parts: Buffer[];
}

function asRecord(value: unknown): RpcRecord {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    throw new Error("RPC frame must be a JSON object");
  }
  return value as RpcRecord;
}

function isChunkFrame(frame: RpcRecord): frame is RpcChunkFrame {
  return frame.type === "rpc_chunk";
}

function decodeBase64(value: unknown): Buffer {
  if (typeof value !== "string" || !BASE64_PATTERN.test(value)) {
    throw new Error("rpc_chunk.data must be canonical base64");
  }
  const decoded = Buffer.from(value, "base64");
  if (decoded.toString("base64") !== value) {
    throw new Error("rpc_chunk.data is not canonical base64");
  }
  return decoded;
}

export class RpcFrameDecoder {
  #maxFrameBytes: number;
  #maxReassembledFrameBytes: number;
  #active: ActiveChunkSequence | undefined;

  constructor(
    maxFrameBytes = DEFAULT_MAX_FRAME_BYTES,
    maxReassembledFrameBytes = DEFAULT_MAX_REASSEMBLED_FRAME_BYTES,
  ) {
    this.#maxFrameBytes = maxFrameBytes;
    this.#maxReassembledFrameBytes = maxReassembledFrameBytes;
    this.#validateLimits();
  }

  configureLimits(maxFrameBytes: number, maxReassembledFrameBytes: number): void {
    if (this.#active !== undefined) {
      throw new Error("cannot change RPC limits during chunk reassembly");
    }
    this.#maxFrameBytes = maxFrameBytes;
    this.#maxReassembledFrameBytes = maxReassembledFrameBytes;
    this.#validateLimits();
  }

  reset(): void {
    this.#active = undefined;
  }

  pushLine(line: string): RpcRecord[] {
    if (Buffer.byteLength(line, "utf8") + 1 > this.#maxFrameBytes) {
      this.reset();
      throw new Error("RPC physical frame exceeds advertised maxFrameBytes");
    }

    let frame: RpcRecord;
    try {
      frame = asRecord(JSON.parse(line));
    } catch (error) {
      this.reset();
      throw new Error("invalid RPC JSON frame", { cause: error });
    }

    if (!isChunkFrame(frame)) {
      if (this.#active !== undefined) {
        this.reset();
        throw new Error("rpc_chunk sequence was interrupted by another frame");
      }
      return [frame];
    }

    return this.#acceptChunk(frame);
  }

  #validateLimits(): void {
    if (!Number.isSafeInteger(this.#maxFrameBytes) || this.#maxFrameBytes <= 0) {
      throw new Error("maxFrameBytes must be a positive safe integer");
    }
    if (
      !Number.isSafeInteger(this.#maxReassembledFrameBytes) ||
      this.#maxReassembledFrameBytes <= 0
    ) {
      throw new Error("maxReassembledFrameBytes must be a positive safe integer");
    }
  }

  #acceptChunk(frame: RpcChunkFrame): RpcRecord[] {
    try {
      this.#validateChunkMetadata(frame);
      const bytes = decodeBase64(frame.data);

      if (this.#active === undefined) {
        if (frame.index !== 0) {
          throw new Error("rpc_chunk sequence must start at index 0");
        }
        this.#active = {
          chunkId: frame.chunkId,
          count: frame.count,
          byteLength: frame.byteLength,
          nextIndex: 0,
          receivedBytes: 0,
          parts: [],
        };
      }

      const active = this.#active;
      if (
        frame.chunkId !== active.chunkId ||
        frame.count !== active.count ||
        frame.byteLength !== active.byteLength ||
        frame.index !== active.nextIndex
      ) {
        throw new Error("rpc_chunk sequence is interleaved, reordered, or inconsistent");
      }

      active.parts.push(bytes);
      active.receivedBytes += bytes.length;
      active.nextIndex += 1;

      if (active.receivedBytes > active.byteLength) {
        throw new Error("rpc_chunk data exceeds declared byteLength");
      }
      if (active.nextIndex < active.count) {
        return [];
      }
      if (active.receivedBytes !== active.byteLength) {
        throw new Error("rpc_chunk sequence does not match declared byteLength");
      }

      const payload = Buffer.concat(active.parts, active.receivedBytes);
      this.#active = undefined;
      const text = new TextDecoder("utf-8", { fatal: true }).decode(payload);
      const logicalFrame = asRecord(JSON.parse(text));
      if (logicalFrame.type === "rpc_chunk") {
        throw new Error("nested rpc_chunk logical frames are not allowed");
      }
      return [logicalFrame];
    } catch (error) {
      this.reset();
      throw error;
    }
  }

  #validateChunkMetadata(frame: RpcChunkFrame): void {
    if (typeof frame.chunkId !== "string" || frame.chunkId.length === 0) {
      throw new Error("rpc_chunk.chunkId must be a non-empty string");
    }
    if (!Number.isSafeInteger(frame.index) || frame.index < 0) {
      throw new Error("rpc_chunk.index must be a non-negative safe integer");
    }
    if (
      !Number.isSafeInteger(frame.count) ||
      frame.count <= 0 ||
      frame.count > MAX_CHUNK_COUNT ||
      frame.index >= frame.count
    ) {
      throw new Error("rpc_chunk.count/index is invalid");
    }
    if (
      !Number.isSafeInteger(frame.byteLength) ||
      frame.byteLength <= 0 ||
      frame.byteLength > this.#maxReassembledFrameBytes
    ) {
      throw new Error("rpc_chunk.byteLength exceeds the reassembly limit");
    }
  }
}
