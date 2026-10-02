import type { Route } from "next";
import { trustedRoute } from "./routes";
import { hasOwnTenantOrPlatformRole, hasTenantReadScope, type TenantScopeUser } from "./tenantScope";

/**
 * Where a signed-in user of an activated tenant lands: the deep link they originally opened, else
 * their home by scope. The landing gate and the activation page share it, so leaving activation
 * lands a user exactly where any later sign-in would.
 */
export function landingTarget(user: TenantScopeUser, returnUrl: string | null): Route {
  if (returnUrl) return trustedRoute(returnUrl);
  // A delegated ("MSP") admin with no own-tenant or platform role manages a fleet.
  if (user.isDelegated && !hasOwnTenantOrPlatformRole(user)) return "/fleet";
  if (hasTenantReadScope(user)) return "/dashboard";
  return "/progress";
}
