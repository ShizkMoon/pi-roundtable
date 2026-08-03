export interface RuntimeGenerationOwnerOptions {
  runtimeId: string;
  runtimeGeneration: number;
}

/**
 * Owns the process-local lifecycle for one externally assigned runtime
 * generation. The sync owner remains responsible for allocating generations;
 * this class only fences one Runtime Host instance to the value it received.
 */
export class RuntimeGenerationOwner {
  readonly runtimeId: string;
  readonly runtimeGeneration: number;
  #configurationInitialized = false;
  #leaseActive = false;
  #stopRequested = false;
  #stopped = false;
  readonly #stopController = new AbortController();
  readonly #stopRequestPromise: Promise<void>;
  #resolveStopRequest: (() => void) | undefined;

  constructor(options: RuntimeGenerationOwnerOptions) {
    if (!Number.isSafeInteger(options.runtimeGeneration) || options.runtimeGeneration < 1) {
      throw new RangeError("runtimeGeneration must be a positive safe integer");
    }
    this.runtimeId = options.runtimeId;
    this.runtimeGeneration = options.runtimeGeneration;
    this.#stopRequestPromise = new Promise<void>((resolve) => {
      this.#resolveStopRequest = resolve;
    });
  }

  get configurationInitialized(): boolean {
    return this.#configurationInitialized;
  }

  get leaseActive(): boolean {
    return this.#leaseActive;
  }

  get stopRequested(): boolean {
    return this.#stopRequested;
  }

  get stopped(): boolean {
    return this.#stopped;
  }

  get stopSignal(): AbortSignal {
    return this.#stopController.signal;
  }

  /**
   * Checks lifecycle state before the host mutates its larger configuration.
   * The matching commit method repeats this check so future asynchronous
   * preparation cannot accidentally publish configuration after a stop.
   */
  assertCanInitializeConfiguration(): void {
    if (
      this.#leaseActive ||
      this.#stopRequested ||
      this.#stopped ||
      this.#configurationInitialized
    ) {
      throw new Error("Runtime configuration is already initialized");
    }
  }

  markConfigurationInitialized(): void {
    this.assertCanInitializeConfiguration();
    this.#configurationInitialized = true;
  }

  acquireLease(allowUninitializedConfiguration: boolean): void {
    if (this.#leaseActive || this.#stopRequested || this.#stopped) {
      throw new Error("Local Roundtable Host cannot be started again");
    }
    if (!allowUninitializedConfiguration && !this.#configurationInitialized) {
      throw new Error("Runtime configuration is not initialized");
    }
    this.#leaseActive = true;
  }

  /**
   * Closes synchronous lifecycle entry points immediately. Authoritative
   * stopped state is committed later by the host's serialized operation queue.
   */
  requestStop(): void {
    if (this.#stopRequested) {
      return;
    }
    this.#stopRequested = true;
    this.#stopController.abort();
    this.#resolveStopRequest?.();
    this.#resolveStopRequest = undefined;
  }

  /** Resolves once, as soon as a caller requests serialized host cleanup. */
  waitForStopRequest(): Promise<void> {
    return this.#stopRequestPromise;
  }

  /** Returns false when an earlier serialized stop already owns cleanup. */
  beginStop(): boolean {
    this.requestStop();
    if (this.#stopped) {
      return false;
    }
    this.#stopped = true;
    return true;
  }

  /** Returns whether an active lease was released by this call. */
  releaseLease(): boolean {
    if (!this.#leaseActive) {
      return false;
    }
    this.#leaseActive = false;
    return true;
  }

  clearConfiguration(): void {
    this.#configurationInitialized = false;
  }

  matchesGeneration(runtimeGeneration: unknown): boolean {
    return runtimeGeneration === this.runtimeGeneration;
  }
}
