import type { DiscussionMode, DiscussionProgressKind } from "@pi-roundtable/protocol";

import {
  FacilitatedDiscussionScheduler,
  type DiscussionCounters,
  type DiscussionFloorRequest,
  type DiscussionLimits,
  type DiscussionSchedulerSnapshot,
  type DiscussionTransition,
  type FloorRequestResult,
  type TurnBudgetResult,
} from "./discussion-scheduler.js";

export interface AgendaAdvanceResult {
  completed?: DiscussionSchedulerSnapshot["agendaItems"][number];
  active?: DiscussionSchedulerSnapshot["agendaItems"][number];
  transition?: DiscussionTransition;
  reason: string;
}

/**
 * Deterministic discussion-policy boundary. Runtime sessions, event sequence,
 * observer processes, and public turn delivery remain owned by the Host.
 */
export interface DiscussionOrchestrator {
  readonly configured: boolean;
  readonly mode: DiscussionMode;
  readonly activeAgendaItemId: string | undefined;
  readonly pendingRequestCount: number;
  configure(
    agendaTitles: readonly string[],
    participantCount: number,
    limits?: Partial<DiscussionLimits>,
  ): DiscussionSchedulerSnapshot;
  restore(snapshot: DiscussionSchedulerSnapshot): void;
  snapshot(): DiscussionSchedulerSnapshot;
  setMode(mode: DiscussionMode, reason: string): DiscussionTransition | undefined;
  pause(reason: string): DiscussionTransition | undefined;
  resume(reason: string): DiscussionTransition | undefined;
  advanceAgenda(reason: string): AgendaAdvanceResult;
  requestFloor(input: DiscussionFloorRequest): FloorRequestResult;
  rejectFloor(requestId: string): DiscussionFloorRequest | undefined;
  takeNextFloor(
    activeRoleIds: ReadonlySet<string>,
    requestId?: string,
  ): DiscussionFloorRequest | undefined;
  removeRole(roleId: string): DiscussionFloorRequest[];
  canAcceptInterruption(roleId: string): boolean;
  acceptInterruption(roleId: string): boolean;
  acceptObserverProbe(): boolean;
  beginSegment(): DiscussionCounters;
  recordTurn(
    roleId: string,
    progressKinds: readonly DiscussionProgressKind[],
  ): TurnBudgetResult;
}

/**
 * Behavior-preserving v0.4 implementation backed by the existing scheduler.
 * The wrapper is intentionally thin so policy order and budgets have one source.
 */
export class DefaultDiscussionOrchestrator implements DiscussionOrchestrator {
  readonly #scheduler: FacilitatedDiscussionScheduler;

  constructor(scheduler: FacilitatedDiscussionScheduler = new FacilitatedDiscussionScheduler()) {
    this.#scheduler = scheduler;
  }

  get configured(): boolean {
    return this.#scheduler.configured;
  }

  get mode(): DiscussionMode {
    return this.#scheduler.mode;
  }

  get activeAgendaItemId(): string | undefined {
    return this.#scheduler.activeAgendaItemId;
  }

  get pendingRequestCount(): number {
    return this.#scheduler.pendingRequestCount;
  }

  configure(
    agendaTitles: readonly string[],
    participantCount: number,
    limits: Partial<DiscussionLimits> = {},
  ): DiscussionSchedulerSnapshot {
    return this.#scheduler.configure(agendaTitles, participantCount, limits);
  }

  restore(snapshot: DiscussionSchedulerSnapshot): void {
    this.#scheduler.restore(snapshot);
  }

  snapshot(): DiscussionSchedulerSnapshot {
    return this.#scheduler.snapshot();
  }

  setMode(mode: DiscussionMode, reason: string): DiscussionTransition | undefined {
    return this.#scheduler.setMode(mode, reason);
  }

  pause(reason: string): DiscussionTransition | undefined {
    return this.#scheduler.pause(reason);
  }

  resume(reason: string): DiscussionTransition | undefined {
    return this.#scheduler.resume(reason);
  }

  advanceAgenda(reason: string): AgendaAdvanceResult {
    return this.#scheduler.advanceAgenda(reason);
  }

  requestFloor(input: DiscussionFloorRequest): FloorRequestResult {
    return this.#scheduler.requestFloor(input);
  }

  rejectFloor(requestId: string): DiscussionFloorRequest | undefined {
    return this.#scheduler.rejectFloor(requestId);
  }

  takeNextFloor(
    activeRoleIds: ReadonlySet<string>,
    requestId?: string,
  ): DiscussionFloorRequest | undefined {
    return this.#scheduler.takeNextFloor(activeRoleIds, requestId);
  }

  removeRole(roleId: string): DiscussionFloorRequest[] {
    return this.#scheduler.removeRole(roleId);
  }

  canAcceptInterruption(roleId: string): boolean {
    return this.#scheduler.canAcceptInterruption(roleId);
  }

  acceptInterruption(roleId: string): boolean {
    return this.#scheduler.acceptInterruption(roleId);
  }

  acceptObserverProbe(): boolean {
    return this.#scheduler.acceptObserverProbe();
  }

  beginSegment(): DiscussionCounters {
    return this.#scheduler.beginSegment();
  }

  recordTurn(
    roleId: string,
    progressKinds: readonly DiscussionProgressKind[],
  ): TurnBudgetResult {
    return this.#scheduler.recordTurn(roleId, progressKinds);
  }
}
