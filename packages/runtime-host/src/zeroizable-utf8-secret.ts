/**
 * One owned UTF-8 copy of a secret that can be overwritten deterministically.
 *
 * JavaScript strings are immutable and therefore cannot be physically wiped.
 * Callers should keep values returned by reveal() at narrow SDK/process
 * boundaries only. close() guarantees only that this class's owned Buffer is
 * overwritten; it deliberately makes no claim about the caller's input string
 * or transient strings already handed to another library.
 */
export class ZeroizableUtf8Secret {
  readonly #bytes: Buffer;
  #closed = false;

  constructor(value: string) {
    const byteLength = Buffer.byteLength(value, "utf8");
    // Buffer.alloc avoids retaining a slice of Node's shared allocation pool.
    this.#bytes = Buffer.alloc(byteLength);
    this.#bytes.write(value, "utf8");
  }

  get byteLength(): number {
    return this.#bytes.byteLength;
  }

  get closed(): boolean {
    return this.#closed;
  }

  get isZeroized(): boolean {
    return this.#closed && this.#bytes.every((value) => value === 0);
  }

  reveal(): string {
    if (this.#closed) {
      throw new Error("Secret is closed");
    }
    return this.#bytes.toString("utf8");
  }

  close(): void {
    if (this.#closed) {
      return;
    }
    this.#bytes.fill(0);
    this.#closed = true;
  }
}
