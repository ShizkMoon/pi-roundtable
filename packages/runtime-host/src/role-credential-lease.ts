import type { ResolvedMcpServerRuntimeConfiguration } from "./mcp-client-manager.js";

export interface RoleCredentialLeaseOptions {
  roleId: string;
  runtimeGeneration: number;
  providerId: string;
  apiKey: string;
  mcpServers: readonly ResolvedMcpServerRuntimeConfiguration[];
}

/**
 * Bounds resolved credential references to one role and runtime generation.
 * close() drops the lease's references and blocks later materialization. RUN-006
 * will replace the retained JavaScript strings with explicitly zeroizable
 * storage; this class intentionally makes no premature zeroization claim.
 */
export class RoleCredentialLease {
  readonly roleId: string;
  readonly runtimeGeneration: number;
  readonly providerId: string;
  #apiKey: string | undefined;
  #mcpServers: readonly ResolvedMcpServerRuntimeConfiguration[] | undefined;

  constructor(options: RoleCredentialLeaseOptions) {
    if (options.roleId.length === 0) {
      throw new Error("Role credential lease requires a role identity");
    }
    if (!Number.isSafeInteger(options.runtimeGeneration) || options.runtimeGeneration < 1) {
      throw new RangeError("runtimeGeneration must be a positive safe integer");
    }
    if (options.providerId.length === 0 || options.apiKey.length === 0) {
      throw new Error("Role credential lease requires a provider credential");
    }
    this.roleId = options.roleId;
    this.runtimeGeneration = options.runtimeGeneration;
    this.providerId = options.providerId;
    this.#apiKey = options.apiKey;
    this.#mcpServers = structuredClone(options.mcpServers);
  }

  get closed(): boolean {
    return this.#apiKey === undefined;
  }

  resolveApiKey(providerId: string): string | undefined {
    return providerId === this.providerId ? this.#apiKey : undefined;
  }

  materializeMcpServers(): ResolvedMcpServerRuntimeConfiguration[] {
    return this.#mcpServers === undefined ? [] : [...structuredClone(this.#mcpServers)];
  }

  close(): void {
    this.#apiKey = undefined;
    this.#mcpServers = undefined;
  }
}
