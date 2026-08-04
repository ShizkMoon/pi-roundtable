import { ZeroizableUtf8Secret } from "./zeroizable-utf8-secret.js";

export type RuntimeCredentialVaultFactory = (
  credentials: Readonly<Record<string, string>>,
) => RuntimeCredentialVault;

/**
 * Meeting-generation owner for credential material received during local-host
 * initialization. Values are copied into independently allocated mutable
 * buffers and are never exposed through snapshots or enumeration APIs.
 */
export class RuntimeCredentialVault {
  readonly #credentials = new Map<string, ZeroizableUtf8Secret>();
  readonly #ownedSecrets: ZeroizableUtf8Secret[] = [];
  #closed = false;

  constructor(credentials: Readonly<Record<string, string>>) {
    try {
      for (const [reference, value] of Object.entries(credentials)) {
        const secret = new ZeroizableUtf8Secret(value);
        this.#credentials.set(reference, secret);
        this.#ownedSecrets.push(secret);
      }
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

  resolve(reference: string): string | undefined {
    if (this.#closed) {
      return undefined;
    }
    return this.#credentials.get(reference)?.reveal();
  }

  close(): void {
    if (this.#closed) {
      return;
    }
    this.#closed = true;
    for (const secret of this.#ownedSecrets) {
      secret.close();
    }
    this.#credentials.clear();
  }
}
