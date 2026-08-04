import type { ResolvedMcpServerRuntimeConfiguration } from "./mcp-client-manager.js";
import { ZeroizableUtf8Secret } from "./zeroizable-utf8-secret.js";

export interface RoleCredentialLeaseOptions {
  roleId: string;
  runtimeGeneration: number;
  providerId: string;
  apiKey: string;
  mcpServers: readonly ResolvedMcpServerRuntimeConfiguration[];
}

type NonSecretMcpConfiguration = Omit<
  ResolvedMcpServerRuntimeConfiguration,
  "environment" | "headers"
>;

interface LeasedMcpConfiguration {
  configuration: NonSecretMcpConfiguration;
  environment?: ReadonlyArray<readonly [string, ZeroizableUtf8Secret]>;
  headers?: ReadonlyArray<readonly [string, ZeroizableUtf8Secret]>;
}

/** Bounds independently owned credential buffers to one role generation. */
export class RoleCredentialLease {
  readonly roleId: string;
  readonly runtimeGeneration: number;
  readonly providerId: string;
  readonly #apiKey: ZeroizableUtf8Secret;
  readonly #mcpServers: readonly LeasedMcpConfiguration[];
  readonly #ownedSecrets: ZeroizableUtf8Secret[] = [];
  #closed = false;

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
    this.#apiKey = new ZeroizableUtf8Secret(options.apiKey);
    this.#ownedSecrets.push(this.#apiKey);
    try {
      this.#mcpServers = options.mcpServers.map((server) => {
        const { environment, headers, ...configuration } = server;
        return {
          configuration: structuredClone(configuration),
          ...(environment === undefined
            ? {}
            : { environment: this.#leaseRecord(environment) }),
          ...(headers === undefined ? {} : { headers: this.#leaseRecord(headers) }),
        };
      });
    } catch (error) {
      this.close();
      throw error;
    }
  }

  get closed(): boolean {
    return this.#closed;
  }

  get ownedSecretCount(): number {
    return this.#ownedSecrets.length;
  }

  get ownedByteLength(): number {
    return this.#ownedSecrets.reduce((total, secret) => total + secret.byteLength, 0);
  }

  get zeroizedSecretCount(): number {
    return this.#ownedSecrets.filter((secret) => secret.isZeroized).length;
  }

  get zeroizedByteLength(): number {
    return this.#ownedSecrets.reduce(
      (total, secret) => total + (secret.isZeroized ? secret.byteLength : 0),
      0,
    );
  }

  resolveApiKey(providerId: string): string | undefined {
    return !this.#closed && providerId === this.providerId
      ? this.#apiKey.reveal()
      : undefined;
  }

  materializeMcpServers(): ResolvedMcpServerRuntimeConfiguration[] {
    if (this.#closed) {
      return [];
    }
    return this.#mcpServers.map(({ configuration, environment, headers }) => ({
      ...structuredClone(configuration),
      ...(environment === undefined
        ? {}
        : { environment: this.#revealRecord(environment) }),
      ...(headers === undefined ? {} : { headers: this.#revealRecord(headers) }),
    }));
  }

  close(): void {
    if (this.#closed) {
      return;
    }
    this.#closed = true;
    for (const secret of this.#ownedSecrets) {
      secret.close();
    }
  }

  #leaseRecord(
    values: Readonly<Record<string, string>>,
  ): ReadonlyArray<readonly [string, ZeroizableUtf8Secret]> {
    return Object.entries(values).map(([name, value]) => {
      const secret = new ZeroizableUtf8Secret(value);
      this.#ownedSecrets.push(secret);
      return [name, secret] as const;
    });
  }

  #revealRecord(
    values: ReadonlyArray<readonly [string, ZeroizableUtf8Secret]>,
  ): Record<string, string> {
    return Object.fromEntries(values.map(([name, secret]) => [name, secret.reveal()]));
  }
}
