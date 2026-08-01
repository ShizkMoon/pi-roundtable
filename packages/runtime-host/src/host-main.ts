#!/usr/bin/env node

import {
  LocalRoundtableHost,
  type LocalRoundtableHostOptions,
} from "./local-roundtable-host.js";
import { StdioRuntimeHost } from "./stdio-runtime-host.js";

function requireEnvironment(name: string): string {
  const value = process.env[name];
  if (value === undefined || value.length === 0) {
    throw new Error(`Missing required environment variable ${name}`);
  }
  return value;
}

async function main(): Promise<void> {
  const generationText = process.env.PI_ROUNDTABLE_RUNTIME_GENERATION ?? "1";
  const runtimeGeneration = Number(generationText);
  if (!Number.isSafeInteger(runtimeGeneration) || runtimeGeneration < 1) {
    throw new Error("PI_ROUNDTABLE_RUNTIME_GENERATION must be a positive integer");
  }

  const options: LocalRoundtableHostOptions = {
    meetingId: requireEnvironment("PI_ROUNDTABLE_MEETING_ID"),
    runtimeGeneration,
  };
  if (process.env.PI_ROUNDTABLE_RUNTIME_ID !== undefined) {
    options.runtimeId = process.env.PI_ROUNDTABLE_RUNTIME_ID;
  }
  if (process.env.PI_ROUNDTABLE_WORKING_DIRECTORY !== undefined) {
    options.cwd = process.env.PI_ROUNDTABLE_WORKING_DIRECTORY;
  }
  const host = new LocalRoundtableHost(options);
  await new StdioRuntimeHost(host).run(process.stdin, process.stdout);
}

void main().catch((error: unknown) => {
  const message =
    error instanceof Error &&
    (error.message.startsWith("Missing required environment variable PI_ROUNDTABLE_") ||
      error.message === "PI_ROUNDTABLE_RUNTIME_GENERATION must be a positive integer")
      ? error.message
      : "Runtime Host terminated unexpectedly";
  process.stderr.write(`Pi Roundtable Runtime Host failed: ${message}\n`);
  process.exitCode = 1;
});
