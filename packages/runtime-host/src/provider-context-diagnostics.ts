import type { ContextCompactionRecordV1 } from "./context-compaction.js";
import type { ProviderCacheDiagnosticV1 } from "./provider-cache-adapter.js";
import type { ProviderUsageSampleV1 } from "./provider-usage.js";

export type ProviderContextDiagnosticV1 =
  | ProviderUsageSampleV1
  | ProviderCacheDiagnosticV1
  | ContextCompactionRecordV1;

export type ProviderContextDiagnosticListener = (diagnostic: ProviderContextDiagnosticV1) => void;
