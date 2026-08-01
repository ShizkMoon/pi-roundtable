import type { RpcReadyFrame, RpcRecord, RpcResponse, StreamingBehavior, SubagentSubscription } from "./rpc-types.js";

export type RpcFrameListener = (frame: RpcRecord) => void;

export interface RuntimeAdapter {
  start(): Promise<RpcReadyFrame>;
  stop(): Promise<void>;
  subscribe(listener: RpcFrameListener): () => void;
  prompt(message: string, streamingBehavior?: StreamingBehavior): Promise<RpcResponse>;
  abort(): Promise<RpcResponse>;
  abortAndPrompt(message: string): Promise<RpcResponse>;
  setSubagentSubscription(level: SubagentSubscription): Promise<RpcResponse>;
}
