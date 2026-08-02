import type {
  DiscussionMode,
  DiscussionProgressKind,
  FloorRequestKind,
} from "@pi-roundtable/protocol";

const MAX_AGENDA_ITEMS = 32;
const MAX_PENDING_REQUESTS = 64;
const MAX_TEXT_LENGTH = 4_096;

export interface DiscussionLimits {
  softTurnLimit: number;
  hardTurnLimit: number;
  softRoundLimit: number;
  hardRoundLimit: number;
  maxConsecutiveTurnsPerRole: number;
  maxInterruptionsPerSegment: number;
  maxInterruptionsPerRole: number;
  noProgressTurnLimit: number;
  maxObserverProbesPerSegment: number;
}

export const DEFAULT_DISCUSSION_LIMITS: Readonly<DiscussionLimits> = Object.freeze({
  softTurnLimit: 8,
  hardTurnLimit: 12,
  softRoundLimit: 2,
  hardRoundLimit: 3,
  maxConsecutiveTurnsPerRole: 2,
  maxInterruptionsPerSegment: 2,
  maxInterruptionsPerRole: 1,
  noProgressTurnLimit: 2,
  maxObserverProbesPerSegment: 12,
});

export type AgendaItemStatus = "pending" | "active" | "completed";

export interface DiscussionAgendaItem {
  agendaItemId: string;
  title: string;
  status: AgendaItemStatus;
}

export interface DiscussionFloorRequest {
  requestId: string;
  roleId: string;
  kind: FloorRequestKind;
  reason: string;
  prompt: string;
  requestedAtSequence: number;
  respondsToRoleId?: string;
  agendaItemId?: string;
}

export interface DiscussionCounters {
  publicTurns: number;
  rounds: number;
  noProgressTurns: number;
  interruptions: number;
  observerProbes: number;
  consecutiveRoleId?: string;
  consecutiveTurns: number;
  interruptionsByRole: Record<string, number>;
}

export interface DiscussionSchedulerSnapshot {
  configured: boolean;
  mode: DiscussionMode;
  resumeMode: Exclude<DiscussionMode, "paused" | "completed">;
  agendaItems: DiscussionAgendaItem[];
  activeAgendaItemId?: string;
  participantCount: number;
  limits: DiscussionLimits;
  counters: DiscussionCounters;
  pendingRequests: DiscussionFloorRequest[];
  pauseReason?: string;
}

export interface DiscussionTransition {
  previousMode: DiscussionMode;
  mode: DiscussionMode;
  reason: string;
}

export interface FloorRequestResult {
  accepted: boolean;
  errorCode?: "discussion_inactive" | "duplicate_request" | "request_queue_full";
  request?: DiscussionFloorRequest;
  downgradedFromCritical: boolean;
}

export interface TurnBudgetResult {
  transition?: DiscussionTransition;
  counters: DiscussionCounters;
}

export class FacilitatedDiscussionScheduler {
  #configured = false;
  #mode: DiscussionMode = "agenda";
  #resumeMode: Exclude<DiscussionMode, "paused" | "completed"> = "agenda";
  #agendaItems: DiscussionAgendaItem[] = [];
  #activeAgendaItemId: string | undefined;
  #participantCount = 1;
  #limits: DiscussionLimits = { ...DEFAULT_DISCUSSION_LIMITS };
  #counters: DiscussionCounters = createEmptyCounters();
  #pendingRequests: DiscussionFloorRequest[] = [];
  #pauseReason: string | undefined;

  constructor(snapshot?: DiscussionSchedulerSnapshot) {
    if (snapshot !== undefined) {
      this.restore(snapshot);
    }
  }

  get configured(): boolean {
    return this.#configured;
  }

  get mode(): DiscussionMode {
    return this.#mode;
  }

  get activeAgendaItemId(): string | undefined {
    return this.#activeAgendaItemId;
  }

  get pendingRequestCount(): number {
    return this.#pendingRequests.length;
  }

  configure(
    agendaTitles: readonly string[],
    participantCount: number,
    limits: Partial<DiscussionLimits> = {},
  ): DiscussionSchedulerSnapshot {
    this.#participantCount = readBoundedInteger(participantCount, 1, 64, "participantCount");
    this.#limits = validateLimits({ ...DEFAULT_DISCUSSION_LIMITS, ...limits });
    const normalizedTitles = agendaTitles
      .map((title) => readText(title, "agenda title"))
      .filter((title, index, all) => all.indexOf(title) === index);
    if (normalizedTitles.length > MAX_AGENDA_ITEMS) {
      throw new RangeError(`A discussion may contain at most ${MAX_AGENDA_ITEMS} agenda items`);
    }
    const titles = normalizedTitles.length === 0 ? ["开放议题"] : normalizedTitles;
    this.#agendaItems = titles.map((title, index) => ({
      agendaItemId: `agenda.${index + 1}`,
      title,
      status: index === 0 ? "active" : "pending",
    }));
    this.#activeAgendaItemId = this.#agendaItems[0]?.agendaItemId;
    this.#configured = true;
    this.#mode = "agenda";
    this.#resumeMode = "agenda";
    this.#counters = createEmptyCounters();
    this.#pendingRequests = [];
    this.#pauseReason = undefined;
    return this.snapshot();
  }

  restore(snapshot: DiscussionSchedulerSnapshot): void {
    if (typeof snapshot.configured !== "boolean") {
      throw new TypeError("Discussion snapshot configured flag is invalid");
    }
    const limits = validateLimits(snapshot.limits);
    const participantCount = readBoundedInteger(snapshot.participantCount, 1, 64, "participantCount");
    const agendaItems = validateAgendaItems(snapshot.agendaItems);
    const counters = validateCounters(snapshot.counters, limits);
    const pendingRequests = snapshot.pendingRequests.map((request) => validateFloorRequest(request));
    if (pendingRequests.length > MAX_PENDING_REQUESTS) {
      throw new RangeError("Discussion snapshot contains too many pending requests");
    }
    if (new Set(pendingRequests.map((request) => request.requestId)).size !== pendingRequests.length) {
      throw new Error("Discussion snapshot contains duplicate request identifiers");
    }
    if (!isDiscussionMode(snapshot.mode)) {
      throw new TypeError("Discussion snapshot mode is invalid");
    }
    if (!isResumeMode(snapshot.resumeMode)) {
      throw new TypeError("Discussion snapshot resume mode is invalid");
    }
    if (
      snapshot.activeAgendaItemId !== undefined &&
      !agendaItems.some((item) => item.agendaItemId === snapshot.activeAgendaItemId)
    ) {
      throw new Error("Discussion snapshot active agenda item is unknown");
    }
    this.#configured = snapshot.configured;
    this.#mode = snapshot.mode;
    this.#resumeMode = snapshot.resumeMode;
    this.#agendaItems = agendaItems;
    this.#activeAgendaItemId = snapshot.activeAgendaItemId;
    this.#participantCount = participantCount;
    this.#limits = limits;
    this.#counters = counters;
    this.#pendingRequests = pendingRequests;
    this.#pauseReason = snapshot.pauseReason === undefined
      ? undefined
      : readText(snapshot.pauseReason, "pause reason");
  }

  snapshot(): DiscussionSchedulerSnapshot {
    return {
      configured: this.#configured,
      mode: this.#mode,
      resumeMode: this.#resumeMode,
      agendaItems: this.#agendaItems.map((item) => ({ ...item })),
      ...(this.#activeAgendaItemId === undefined
        ? {}
        : { activeAgendaItemId: this.#activeAgendaItemId }),
      participantCount: this.#participantCount,
      limits: { ...this.#limits },
      counters: cloneCounters(this.#counters),
      pendingRequests: this.#pendingRequests.map((request) => ({ ...request })),
      ...(this.#pauseReason === undefined ? {} : { pauseReason: this.#pauseReason }),
    };
  }

  setMode(mode: DiscussionMode, reason: string): DiscussionTransition | undefined {
    if (!this.#configured) {
      throw new Error("Discussion is not configured");
    }
    if (!isDiscussionMode(mode)) {
      throw new TypeError("Discussion mode is invalid");
    }
    if (this.#mode === "completed" || this.#mode === mode) {
      return undefined;
    }
    if (mode === "paused") {
      return this.pause(reason);
    }
    const previousMode = this.#mode;
    this.#mode = mode;
    this.#pauseReason = undefined;
    if (mode !== "completed") {
      this.#resumeMode = mode;
    }
    if (mode === "completed") {
      this.#pendingRequests = [];
    }
    return { previousMode, mode, reason: readText(reason, "mode reason") };
  }

  pause(reason: string): DiscussionTransition | undefined {
    if (!this.#configured || this.#mode === "paused" || this.#mode === "completed") {
      return undefined;
    }
    const previousMode = this.#mode;
    this.#resumeMode = previousMode;
    this.#mode = "paused";
    this.#pauseReason = readText(reason, "pause reason");
    return { previousMode, mode: "paused", reason: this.#pauseReason };
  }

  resume(reason: string): DiscussionTransition | undefined {
    if (!this.#configured || this.#mode !== "paused") {
      return undefined;
    }
    const mode = this.#resumeMode;
    this.#mode = mode;
    this.#pauseReason = undefined;
    return { previousMode: "paused", mode, reason: readText(reason, "resume reason") };
  }

  advanceAgenda(reason: string): {
    completed?: DiscussionAgendaItem;
    active?: DiscussionAgendaItem;
    transition?: DiscussionTransition;
    reason: string;
  } {
    if (!this.#configured || this.#mode !== "agenda") {
      throw new Error("Agenda can advance only in agenda mode");
    }
    const normalizedReason = readText(reason, "agenda reason");
    const activeIndex = this.#agendaItems.findIndex(
      (item) => item.agendaItemId === this.#activeAgendaItemId,
    );
    if (activeIndex < 0) {
      const transition = this.setMode("free_discussion", normalizedReason);
      return { ...(transition === undefined ? {} : { transition }), reason: normalizedReason };
    }
    const completed = { ...this.#agendaItems[activeIndex]!, status: "completed" as const };
    this.#agendaItems[activeIndex] = completed;
    const next = this.#agendaItems.slice(activeIndex + 1).find((item) => item.status === "pending");
    if (next === undefined) {
      this.#activeAgendaItemId = undefined;
      const transition = this.setMode("free_discussion", normalizedReason);
      return {
        completed: { ...completed },
        ...(transition === undefined ? {} : { transition }),
        reason: normalizedReason,
      };
    }
    next.status = "active";
    this.#activeAgendaItemId = next.agendaItemId;
    return { completed: { ...completed }, active: { ...next }, reason: normalizedReason };
  }

  requestFloor(input: DiscussionFloorRequest): FloorRequestResult {
    if (!this.#configured || this.#mode === "paused" || this.#mode === "completed") {
      return { accepted: false, errorCode: "discussion_inactive", downgradedFromCritical: false };
    }
    if (this.#pendingRequests.length >= MAX_PENDING_REQUESTS) {
      return { accepted: false, errorCode: "request_queue_full", downgradedFromCritical: false };
    }
    const request = validateFloorRequest(input);
    if (
      this.#pendingRequests.some(
        (pending) => pending.requestId === request.requestId || pending.roleId === request.roleId,
      )
    ) {
      return { accepted: false, errorCode: "duplicate_request", downgradedFromCritical: false };
    }
    const downgradedFromCritical = request.kind === "critical" &&
      !this.canAcceptInterruption(request.roleId);
    const acceptedRequest = downgradedFromCritical
      ? { ...request, kind: "normal" as const }
      : request;
    this.#pendingRequests.push(acceptedRequest);
    return { accepted: true, request: { ...acceptedRequest }, downgradedFromCritical };
  }

  rejectFloor(requestId: string): DiscussionFloorRequest | undefined {
    const index = this.#pendingRequests.findIndex((request) => request.requestId === requestId);
    if (index < 0) {
      return undefined;
    }
    const [request] = this.#pendingRequests.splice(index, 1);
    return request === undefined ? undefined : { ...request };
  }

  takeNextFloor(
    activeRoleIds: ReadonlySet<string>,
    requestId?: string,
  ): DiscussionFloorRequest | undefined {
    if (!this.#configured || this.#mode === "paused" || this.#mode === "completed") {
      return undefined;
    }
    this.#pendingRequests = this.#pendingRequests.filter((request) => activeRoleIds.has(request.roleId));
    if (requestId !== undefined) {
      const index = this.#pendingRequests.findIndex((request) => request.requestId === requestId);
      if (index < 0) {
        return undefined;
      }
      const [granted] = this.#pendingRequests.splice(index, 1);
      return granted === undefined ? undefined : { ...granted };
    }
    const ordered = [...this.#pendingRequests].sort((left, right) => {
      const priority = requestPriority(left.kind) - requestPriority(right.kind);
      if (priority !== 0) {
        return priority;
      }
      const fairness = this.#fairnessPenalty(left) - this.#fairnessPenalty(right);
      if (fairness !== 0) {
        return fairness;
      }
      return left.requestedAtSequence - right.requestedAtSequence ||
        left.roleId.localeCompare(right.roleId);
    });
    const granted = ordered[0];
    if (granted === undefined) {
      return undefined;
    }
    if (
      granted.kind !== "host" &&
      this.#counters.consecutiveRoleId === granted.roleId &&
      this.#counters.consecutiveTurns >= this.#limits.maxConsecutiveTurnsPerRole &&
      !ordered.some((request) => request.roleId !== granted.roleId)
    ) {
      return undefined;
    }
    return this.rejectFloor(granted.requestId);
  }

  removeRole(roleId: string): DiscussionFloorRequest[] {
    const removed = this.#pendingRequests.filter((request) => request.roleId === roleId);
    this.#pendingRequests = this.#pendingRequests.filter((request) => request.roleId !== roleId);
    return removed.map((request) => ({ ...request }));
  }

  canAcceptInterruption(roleId: string): boolean {
    return this.#counters.interruptions < this.#limits.maxInterruptionsPerSegment &&
      (this.#counters.interruptionsByRole[roleId] ?? 0) < this.#limits.maxInterruptionsPerRole;
  }

  acceptInterruption(roleId: string): boolean {
    if (!this.canAcceptInterruption(roleId)) {
      return false;
    }
    this.#counters.interruptions += 1;
    this.#counters.interruptionsByRole[roleId] =
      (this.#counters.interruptionsByRole[roleId] ?? 0) + 1;
    return true;
  }

  acceptObserverProbe(): boolean {
    if (
      !this.#configured ||
      this.#mode !== "free_discussion" ||
      this.#counters.observerProbes >= this.#limits.maxObserverProbesPerSegment
    ) {
      return false;
    }
    this.#counters.observerProbes += 1;
    return true;
  }

  beginSegment(): DiscussionCounters {
    if (!this.#configured || this.#mode === "paused" || this.#mode === "completed") {
      return cloneCounters(this.#counters);
    }
    this.#counters.publicTurns = 0;
    this.#counters.rounds = 0;
    this.#counters.noProgressTurns = 0;
    this.#counters.interruptions = 0;
    this.#counters.observerProbes = 0;
    delete this.#counters.consecutiveRoleId;
    this.#counters.consecutiveTurns = 0;
    this.#counters.interruptionsByRole = {};
    return cloneCounters(this.#counters);
  }

  recordTurn(
    roleId: string,
    progressKinds: readonly DiscussionProgressKind[],
  ): TurnBudgetResult {
    if (!this.#configured || this.#mode === "paused" || this.#mode === "completed") {
      return { counters: cloneCounters(this.#counters) };
    }
    this.#counters.publicTurns += 1;
    this.#counters.rounds = Math.ceil(this.#counters.publicTurns / this.#participantCount);
    if (this.#counters.consecutiveRoleId === roleId) {
      this.#counters.consecutiveTurns += 1;
    } else {
      this.#counters.consecutiveRoleId = roleId;
      this.#counters.consecutiveTurns = 1;
    }
    if (this.#mode === "free_discussion") {
      if (progressKinds.length === 0) {
        this.#counters.noProgressTurns += 1;
      } else {
        this.#counters.noProgressTurns = 0;
      }
    } else if (this.#mode === "agenda") {
      this.#counters.noProgressTurns = 0;
    }

    let transition: DiscussionTransition | undefined;
    if (
      this.#counters.publicTurns >= this.#limits.hardTurnLimit ||
      this.#counters.rounds >= this.#limits.hardRoundLimit
    ) {
      transition = this.pause("hard_limit");
    } else if (
      (this.#mode === "agenda" || this.#mode === "free_discussion") &&
      (this.#counters.noProgressTurns >= this.#limits.noProgressTurnLimit ||
        this.#counters.publicTurns >= this.#limits.softTurnLimit ||
        this.#counters.rounds >= this.#limits.softRoundLimit)
    ) {
      transition = this.setMode(
        "convergence",
        this.#counters.noProgressTurns >= this.#limits.noProgressTurnLimit
          ? "no_progress"
          : "soft_limit",
      );
    }
    return {
      ...(transition === undefined ? {} : { transition }),
      counters: cloneCounters(this.#counters),
    };
  }

  #fairnessPenalty(request: DiscussionFloorRequest): number {
    if (request.kind === "host") {
      return 0;
    }
    return this.#counters.consecutiveRoleId === request.roleId
      ? this.#counters.consecutiveTurns
      : 0;
  }
}

function createEmptyCounters(): DiscussionCounters {
  return {
    publicTurns: 0,
    rounds: 0,
    noProgressTurns: 0,
    interruptions: 0,
    observerProbes: 0,
    consecutiveTurns: 0,
    interruptionsByRole: {},
  };
}

function cloneCounters(counters: DiscussionCounters): DiscussionCounters {
  return {
    publicTurns: counters.publicTurns,
    rounds: counters.rounds,
    noProgressTurns: counters.noProgressTurns,
    interruptions: counters.interruptions,
    observerProbes: counters.observerProbes,
    ...(counters.consecutiveRoleId === undefined
      ? {}
      : { consecutiveRoleId: counters.consecutiveRoleId }),
    consecutiveTurns: counters.consecutiveTurns,
    interruptionsByRole: { ...counters.interruptionsByRole },
  };
}

function validateLimits(limits: DiscussionLimits): DiscussionLimits {
  const normalized = {
    softTurnLimit: readBoundedInteger(limits.softTurnLimit, 1, 1_000, "softTurnLimit"),
    hardTurnLimit: readBoundedInteger(limits.hardTurnLimit, 1, 1_000, "hardTurnLimit"),
    softRoundLimit: readBoundedInteger(limits.softRoundLimit, 1, 100, "softRoundLimit"),
    hardRoundLimit: readBoundedInteger(limits.hardRoundLimit, 1, 100, "hardRoundLimit"),
    maxConsecutiveTurnsPerRole: readBoundedInteger(
      limits.maxConsecutiveTurnsPerRole,
      1,
      10,
      "maxConsecutiveTurnsPerRole",
    ),
    maxInterruptionsPerSegment: readBoundedInteger(
      limits.maxInterruptionsPerSegment,
      0,
      100,
      "maxInterruptionsPerSegment",
    ),
    maxInterruptionsPerRole: readBoundedInteger(
      limits.maxInterruptionsPerRole,
      0,
      20,
      "maxInterruptionsPerRole",
    ),
    noProgressTurnLimit: readBoundedInteger(
      limits.noProgressTurnLimit,
      1,
      20,
      "noProgressTurnLimit",
    ),
    maxObserverProbesPerSegment: readBoundedInteger(
      limits.maxObserverProbesPerSegment,
      0,
      1_000,
      "maxObserverProbesPerSegment",
    ),
  };
  if (normalized.softTurnLimit >= normalized.hardTurnLimit) {
    throw new RangeError("softTurnLimit must be lower than hardTurnLimit");
  }
  if (normalized.softRoundLimit >= normalized.hardRoundLimit) {
    throw new RangeError("softRoundLimit must be lower than hardRoundLimit");
  }
  if (normalized.maxInterruptionsPerRole > normalized.maxInterruptionsPerSegment) {
    throw new RangeError("Per-role interruption limit cannot exceed the segment limit");
  }
  return normalized;
}

function validateAgendaItems(items: readonly DiscussionAgendaItem[]): DiscussionAgendaItem[] {
  if (!Array.isArray(items) || items.length > MAX_AGENDA_ITEMS) {
    throw new TypeError("Discussion agenda is invalid");
  }
  const normalized = items.map((item) => {
    if (item.status !== "pending" && item.status !== "active" && item.status !== "completed") {
      throw new TypeError("Discussion agenda status is invalid");
    }
    return {
      agendaItemId: readText(item.agendaItemId, "agenda item id"),
      title: readText(item.title, "agenda title"),
      status: item.status,
    };
  });
  if (new Set(normalized.map((item) => item.agendaItemId)).size !== normalized.length) {
    throw new Error("Discussion agenda contains duplicate identifiers");
  }
  if (normalized.filter((item) => item.status === "active").length > 1) {
    throw new Error("Discussion agenda may contain only one active item");
  }
  return normalized;
}

function validateCounters(counters: DiscussionCounters, limits: DiscussionLimits): DiscussionCounters {
  const normalized = {
    publicTurns: readBoundedInteger(counters.publicTurns, 0, limits.hardTurnLimit, "publicTurns"),
    rounds: readBoundedInteger(counters.rounds, 0, limits.hardRoundLimit, "rounds"),
    noProgressTurns: readBoundedInteger(
      counters.noProgressTurns,
      0,
      limits.hardTurnLimit,
      "noProgressTurns",
    ),
    interruptions: readBoundedInteger(
      counters.interruptions,
      0,
      limits.maxInterruptionsPerSegment,
      "interruptions",
    ),
    observerProbes: readBoundedInteger(
      counters.observerProbes,
      0,
      limits.maxObserverProbesPerSegment,
      "observerProbes",
    ),
    ...(counters.consecutiveRoleId === undefined
      ? {}
      : { consecutiveRoleId: readText(counters.consecutiveRoleId, "consecutive role id") }),
    consecutiveTurns: readBoundedInteger(
      counters.consecutiveTurns,
      0,
      limits.hardTurnLimit,
      "consecutiveTurns",
    ),
    interruptionsByRole: Object.fromEntries(
      Object.entries(counters.interruptionsByRole).map(([roleId, count]) => [
        readText(roleId, "interruption role id"),
        readBoundedInteger(count, 0, limits.maxInterruptionsPerRole, "role interruptions"),
      ]),
    ),
  };
  return normalized;
}

function validateFloorRequest(input: DiscussionFloorRequest): DiscussionFloorRequest {
  if (!isFloorRequestKind(input.kind)) {
    throw new TypeError("Floor request kind is invalid");
  }
  const requestedAtSequence = readBoundedInteger(
    input.requestedAtSequence,
    0,
    Number.MAX_SAFE_INTEGER,
    "requestedAtSequence",
  );
  return {
    requestId: readText(input.requestId, "request id"),
    roleId: readText(input.roleId, "request role id"),
    kind: input.kind,
    reason: readText(input.reason, "request reason"),
    prompt: readText(input.prompt, "request prompt"),
    requestedAtSequence,
    ...(input.respondsToRoleId === undefined
      ? {}
      : { respondsToRoleId: readText(input.respondsToRoleId, "reply role id") }),
    ...(input.agendaItemId === undefined
      ? {}
      : { agendaItemId: readText(input.agendaItemId, "agenda item id") }),
  };
}

function isDiscussionMode(value: unknown): value is DiscussionMode {
  return value === "agenda" || value === "free_discussion" || value === "convergence" ||
    value === "paused" || value === "completed";
}

function isResumeMode(
  value: unknown,
): value is Exclude<DiscussionMode, "paused" | "completed"> {
  return value === "agenda" || value === "free_discussion" || value === "convergence";
}

function isFloorRequestKind(value: unknown): value is FloorRequestKind {
  return value === "host" || value === "critical" || value === "facilitator" ||
    value === "reply" || value === "normal";
}

function requestPriority(kind: FloorRequestKind): number {
  switch (kind) {
    case "host": return 0;
    case "critical": return 1;
    case "facilitator": return 2;
    case "reply": return 3;
    case "normal": return 4;
  }
}

function readText(value: string, name: string): string {
  if (typeof value !== "string") {
    throw new TypeError(`${name} must be a string`);
  }
  const text = value.trim();
  if (text.length === 0 || text.length > MAX_TEXT_LENGTH) {
    throw new RangeError(`${name} must contain between 1 and ${MAX_TEXT_LENGTH} characters`);
  }
  return text;
}

function readBoundedInteger(value: number, min: number, max: number, name: string): number {
  if (!Number.isSafeInteger(value) || value < min || value > max) {
    throw new RangeError(`${name} must be an integer between ${min} and ${max}`);
  }
  return value;
}
