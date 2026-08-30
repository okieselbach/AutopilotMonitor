// The admin-config wire shape is GENERATED from the C# contract (AdminConfiguration
// in AutopilotMonitor.Shared → utils/wire-types.generated.ts). This module narrows
// excessiveEventAutoActionMode to the canonical literals the UI emits (the wire
// carries a plain string — the server tolerates casing drift, the UI must not),
// and keeps OpsAlertRule, which is a client-side view over opsAlertRulesJson rather
// than a wire type of its own.
import type { AdminConfiguration as WireAdminConfiguration } from "@/utils/wire-types.generated";

export type AdminConfiguration = Omit<WireAdminConfiguration, "excessiveEventAutoActionMode"> & {
  /**
   * Auto-action mode for runaway sessions whose EventCount crosses
   * `excessiveEventAutoActionThreshold`. "Off" keeps warn-only behaviour;
   * "Block" stops device uploads for `excessiveEventAutoActionDurationHours`;
   * "Kill" issues a remote self-destruct signal. Server tolerates casing drift
   * but the UI emits these canonical values.
   */
  excessiveEventAutoActionMode?: "Off" | "Block" | "Kill";
};

/** One parsed entry of AdminConfiguration.opsAlertRulesJson (client-side view). */
export interface OpsAlertRule {
  eventType: string;
  minSeverity: string;
  enabled: boolean;
}
