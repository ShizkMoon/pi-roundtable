interface PendingPermit {
  readonly signal: AbortSignal;
  readonly resolve: () => void;
  readonly reject: (reason: unknown) => void;
  readonly abort: () => void;
}

/**
 * A small FIFO concurrency gate for expensive model calls.
 *
 * Waiting work remains abortable, which matters during meeting shutdown: a
 * queued observer must not keep the Runtime Host alive after its turn ended.
 */
export class AsyncWorkLimiter {
  readonly #limit: number;
  readonly #waiters: PendingPermit[] = [];
  #active = 0;

  constructor(limit: number) {
    if (!Number.isSafeInteger(limit) || limit < 1) {
      throw new RangeError("AsyncWorkLimiter limit must be a positive integer");
    }
    this.#limit = limit;
  }

  get activeCount(): number {
    return this.#active;
  }

  get waitingCount(): number {
    return this.#waiters.length;
  }

  async run<T>(signal: AbortSignal, work: () => Promise<T>): Promise<T> {
    await this.#acquire(signal);
    try {
      signal.throwIfAborted();
      return await work();
    } finally {
      this.#release();
    }
  }

  #acquire(signal: AbortSignal): Promise<void> {
    if (signal.aborted) {
      return Promise.reject(signal.reason);
    }
    if (this.#active < this.#limit) {
      this.#active += 1;
      return Promise.resolve();
    }

    return new Promise<void>((resolve, reject) => {
      const waiter: PendingPermit = {
        signal,
        resolve,
        reject,
        abort: () => {
          const index = this.#waiters.indexOf(waiter);
          if (index >= 0) {
            this.#waiters.splice(index, 1);
          }
          reject(signal.reason);
        },
      };
      this.#waiters.push(waiter);
      signal.addEventListener("abort", waiter.abort, { once: true });
    });
  }

  #release(): void {
    this.#active -= 1;
    while (this.#waiters.length > 0) {
      const waiter = this.#waiters.shift();
      if (waiter === undefined) {
        return;
      }
      waiter.signal.removeEventListener("abort", waiter.abort);
      if (waiter.signal.aborted) {
        waiter.reject(waiter.signal.reason);
        continue;
      }
      this.#active += 1;
      waiter.resolve();
      return;
    }
  }
}
