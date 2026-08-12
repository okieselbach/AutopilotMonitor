/**
 * Pure decision logic for the top navbar — every gating rule the navbar applies
 * lives here so it can be unit-tested without rendering React.
 *
 * The component layer (components/Navbar.tsx + components/navbar/*) must not
 * re-derive any of these decisions inline; it consumes this module. That keeps
 * desktop and mobile surfaces from drifting apart, which is how the previous
 * monolithic navbar accumulated inconsistencies (e.g. settings reachable on
 * desktop but not in the mobile overflow for Global Readers).
 */

/** The slice of the authenticated user the navbar's decisions depend on. */
export interface NavbarUserView {
  displayName?: string | null;
  upn?: string | null;
  role: 'Admin' | 'Operator' | 'Viewer' | null;
  isTenantAdmin: boolean;
  isGlobalAdmin: boolean;
  isGlobalReader: boolean;
}

export type NavbarVariant = 'hidden' | 'minimal' | 'full';

/**
 * Which navbar to render.
 * - 'hidden': landing page (marketing navbar owns the header) or unauthenticated.
 * - 'minimal': regular members (no Admin/Operator role, no platform scope) get
 *   brand + user menu only — they only ever see the Progress Portal.
 * - 'full': Admins, Operators, and anyone with platform scope (Global Admin or
 *   read-only Global Reader).
 */
export function resolveNavbarVariant(args: {
  pathname: string | null;
  isAuthenticated: boolean;
  user: NavbarUserView | null;
  hasGlobalScope: boolean;
}): NavbarVariant {
  const { pathname, isAuthenticated, user, hasGlobalScope } = args;
  if (pathname === '/') return 'hidden';
  if (!isAuthenticated) return 'hidden';
  const isAdminOrOperator = (user?.isTenantAdmin ?? false) || user?.role === 'Operator';
  if (!isAdminOrOperator && !hasGlobalScope) return 'minimal';
  return 'full';
}

/** Everything the full navbar shows or hides per user, derived in one place. */
export interface NavbarCapabilities {
  isTenantAdmin: boolean;
  isOperator: boolean;
  /**
   * Tenant notification dismiss is tenant-shared (clearing for one user clears
   * for all), so only Admins / Global Admins may dismiss; Operators and Viewers
   * see the bell read-only. See TenantNotificationContext +
   * EndpointAccessPolicyCatalog.
   */
  canDismissTenant: boolean;
  /** Global (GA-lane) notifications are dismissable by real Global Admins only. */
  canDismissGlobal: boolean;
  showAdminToggle: boolean;
  showGlobalToggle: boolean;
  /**
   * The settings surface (desktop gear / mobile overflow entry) exists iff it
   * has at least one toggle to show. One flag for both surfaces.
   */
  showSettingsMenu: boolean;
}

export function deriveNavbarCapabilities(
  user: NavbarUserView | null,
  hasGlobalScope: boolean,
): NavbarCapabilities {
  const isTenantAdmin = user?.isTenantAdmin ?? false;
  const isGlobalAdmin = user?.isGlobalAdmin ?? false;
  return {
    isTenantAdmin,
    isOperator: user?.role === 'Operator',
    canDismissTenant: isTenantAdmin || isGlobalAdmin,
    canDismissGlobal: isGlobalAdmin,
    showAdminToggle: isTenantAdmin,
    showGlobalToggle: hasGlobalScope,
    showSettingsMenu: isTenantAdmin || hasGlobalScope,
  };
}

/**
 * The GA notification lane is only surfaced while the user has platform scope
 * AND has switched global mode on — a Global Admin browsing in tenant mode does
 * not see (or get counted) global notifications.
 */
export function includeGlobalNotifications(
  hasGlobalScope: boolean,
  globalAdminMode: boolean,
): boolean {
  return hasGlobalScope && globalAdminMode;
}

/** Total shown on the bell badge. Tenant notifications have no read state — all count. */
export function countBellNotifications(args: {
  ephemeralUnread: number;
  tenantCount: number;
  globalCount: number;
  includeGlobal: boolean;
}): number {
  const { ephemeralUnread, tenantCount, globalCount, includeGlobal } = args;
  return ephemeralUnread + tenantCount + (includeGlobal ? globalCount : 0);
}

/** Badge text, or null when the badge should not render. */
export function bellBadgeLabel(count: number): string | null {
  if (count <= 0) return null;
  return count > 9 ? '9+' : String(count);
}

/**
 * "Clear all" only shows when the caller can actually clear something:
 * ephemeral (client-side, anyone), tenant (if canDismissTenant), or global
 * (real GA only). A read-only Global Reader viewing only global notifications
 * gets no dead button.
 */
export function hasClearableNotifications(args: {
  ephemeralCount: number;
  tenantCount: number;
  visibleGlobalCount: number;
  canDismissTenant: boolean;
  canDismissGlobal: boolean;
}): boolean {
  const { ephemeralCount, tenantCount, visibleGlobalCount, canDismissTenant, canDismissGlobal } =
    args;
  return (
    ephemeralCount > 0 ||
    (canDismissTenant && tenantCount > 0) ||
    (canDismissGlobal && visibleGlobalCount > 0)
  );
}

/** Avatar initials: first+last name, else first letter of the name or UPN, else 'U'. */
export function getUserInitials(displayName?: string | null, upn?: string | null): string {
  if (displayName) {
    const names = displayName.split(' ');
    if (names.length >= 2) {
      return `${names[0].charAt(0)}${names[names.length - 1].charAt(0)}`.toUpperCase();
    }
    return displayName.charAt(0).toUpperCase();
  }
  return upn?.charAt(0).toUpperCase() || 'U';
}

/** Coarse relative age for notification rows ("Just now", "5m ago", "3h ago", "2d ago"). */
export function formatRelativeTime(date: Date, now: Date): string {
  const diffMs = now.getTime() - date.getTime();
  const diffMins = Math.floor(diffMs / 60000);
  if (diffMins < 1) return 'Just now';
  if (diffMins < 60) return `${diffMins}m ago`;
  const diffHours = Math.floor(diffMins / 60);
  if (diffHours < 24) return `${diffHours}h ago`;
  return `${Math.floor(diffHours / 24)}d ago`;
}

export function ephemeralNotificationIcon(type: string): string {
  switch (type) {
    case 'error':
      return '🔴';
    case 'warning':
      return '⚠️';
    case 'success':
      return '✅';
    default:
      return 'ℹ️';
  }
}

export function tenantNotificationIcon(type: string): string {
  return type === 'hardware_rejection' ? '🖥️' : '🔔';
}

export function globalNotificationIcon(type: string): string {
  return type === 'session_report' ? '📋' : '🌟';
}

export interface RoleBadge {
  key: 'global-admin' | 'global-reader' | 'operator' | 'admin';
  label: string;
}

/**
 * Role badges shown in the user dropdown, in display order. "Admin" and
 * "Global Reader" are suppressed for Global Admins (the stronger badge wins).
 */
export function deriveRoleBadges(user: NavbarUserView | null): RoleBadge[] {
  if (!user) return [];
  const badges: RoleBadge[] = [];
  if (user.isGlobalAdmin) badges.push({ key: 'global-admin', label: 'Global Admin' });
  if (user.isGlobalReader && !user.isGlobalAdmin)
    badges.push({ key: 'global-reader', label: 'Global Reader' });
  if (user.role === 'Operator') badges.push({ key: 'operator', label: 'Operator' });
  if (user.isTenantAdmin && !user.isGlobalAdmin) badges.push({ key: 'admin', label: 'Admin' });
  return badges;
}
