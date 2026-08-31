// The auth/me wire shape is GENERATED from the C# contract (AuthMeResponse in
// AutopilotMonitor.Shared → utils/wire-types.generated.ts). This module narrows the
// two plain-string wire slots to the canonical literals the backend actually emits,
// so a backend rename or type change goes tsc-red in every consumer.
import type { AuthMeResponse as WireAuthMeResponse } from "@/utils/wire-types.generated";

/** Tenant role emitted by auth/me ("role" is absent on the wire for a roleless caller). */
export type TenantRole = "Admin" | "Operator" | "Viewer";

/** Which app registration the tenant is homed on (dual app-reg operation model). */
export type HomedApp = "primary" | "legacy";

export type AuthMeResponse = Omit<WireAuthMeResponse, "role" | "homedApp"> & {
  role?: TenantRole;
  homedApp: HomedApp;
};
