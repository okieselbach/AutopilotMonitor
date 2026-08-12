import { describe, it, expect } from 'vitest';
import {
  resolveNavbarVariant,
  deriveNavbarCapabilities,
  includeGlobalNotifications,
  countBellNotifications,
  bellBadgeLabel,
  hasClearableNotifications,
  getUserInitials,
  formatRelativeTime,
  ephemeralNotificationIcon,
  tenantNotificationIcon,
  globalNotificationIcon,
  deriveRoleBadges,
  type NavbarUserView,
} from '../navbarPolicy';

function user(overrides: Partial<NavbarUserView> = {}): NavbarUserView {
  return {
    displayName: 'Jane Doe',
    upn: 'jane@contoso.com',
    role: null,
    isTenantAdmin: false,
    isGlobalAdmin: false,
    isGlobalReader: false,
    ...overrides,
  };
}

describe('resolveNavbarVariant', () => {
  it('hides the navbar on the landing page even when authenticated', () => {
    expect(
      resolveNavbarVariant({
        pathname: '/',
        isAuthenticated: true,
        user: user({ isTenantAdmin: true }),
        hasGlobalScope: false,
      }),
    ).toBe('hidden');
  });

  it('hides the navbar when not authenticated', () => {
    expect(
      resolveNavbarVariant({
        pathname: '/dashboard',
        isAuthenticated: false,
        user: null,
        hasGlobalScope: false,
      }),
    ).toBe('hidden');
  });

  it('shows the minimal navbar for regular members (Viewer / no role, no scope)', () => {
    expect(
      resolveNavbarVariant({
        pathname: '/progress',
        isAuthenticated: true,
        user: user({ role: 'Viewer' }),
        hasGlobalScope: false,
      }),
    ).toBe('minimal');
    expect(
      resolveNavbarVariant({
        pathname: '/progress',
        isAuthenticated: true,
        user: user({ role: null }),
        hasGlobalScope: false,
      }),
    ).toBe('minimal');
  });

  it('shows the full navbar for tenant admins and operators', () => {
    expect(
      resolveNavbarVariant({
        pathname: '/dashboard',
        isAuthenticated: true,
        user: user({ isTenantAdmin: true, role: 'Admin' }),
        hasGlobalScope: false,
      }),
    ).toBe('full');
    expect(
      resolveNavbarVariant({
        pathname: '/dashboard',
        isAuthenticated: true,
        user: user({ role: 'Operator' }),
        hasGlobalScope: false,
      }),
    ).toBe('full');
  });

  it('shows the full navbar for platform scope even without a tenant role (Global Reader)', () => {
    expect(
      resolveNavbarVariant({
        pathname: '/dashboard',
        isAuthenticated: true,
        user: user({ isGlobalReader: true }),
        hasGlobalScope: true,
      }),
    ).toBe('full');
  });
});

describe('deriveNavbarCapabilities', () => {
  it('gives a plain Operator no settings menu and read-only bell', () => {
    const caps = deriveNavbarCapabilities(user({ role: 'Operator' }), false);
    expect(caps.isOperator).toBe(true);
    expect(caps.showSettingsMenu).toBe(false);
    expect(caps.showAdminToggle).toBe(false);
    expect(caps.showGlobalToggle).toBe(false);
    expect(caps.canDismissTenant).toBe(false);
    expect(caps.canDismissGlobal).toBe(false);
  });

  it('gives a tenant admin the admin toggle and tenant dismiss, but no global powers', () => {
    const caps = deriveNavbarCapabilities(user({ isTenantAdmin: true, role: 'Admin' }), false);
    expect(caps.showSettingsMenu).toBe(true);
    expect(caps.showAdminToggle).toBe(true);
    expect(caps.showGlobalToggle).toBe(false);
    expect(caps.canDismissTenant).toBe(true);
    expect(caps.canDismissGlobal).toBe(false);
  });

  it('gives a read-only Global Reader the settings menu (global toggle) but no dismiss rights', () => {
    const caps = deriveNavbarCapabilities(user({ isGlobalReader: true }), true);
    expect(caps.showSettingsMenu).toBe(true);
    expect(caps.showAdminToggle).toBe(false);
    expect(caps.showGlobalToggle).toBe(true);
    expect(caps.canDismissTenant).toBe(false);
    expect(caps.canDismissGlobal).toBe(false);
  });

  it('gives a Global Admin every capability', () => {
    const caps = deriveNavbarCapabilities(
      user({ isGlobalAdmin: true, isTenantAdmin: true, role: 'Admin' }),
      true,
    );
    expect(caps).toEqual({
      isTenantAdmin: true,
      isOperator: false,
      canDismissTenant: true,
      canDismissGlobal: true,
      showAdminToggle: true,
      showGlobalToggle: true,
      showSettingsMenu: true,
    });
  });

  it('handles a null user', () => {
    const caps = deriveNavbarCapabilities(null, false);
    expect(caps.showSettingsMenu).toBe(false);
    expect(caps.canDismissTenant).toBe(false);
  });
});

describe('includeGlobalNotifications', () => {
  it('requires platform scope AND global mode switched on', () => {
    expect(includeGlobalNotifications(true, true)).toBe(true);
    expect(includeGlobalNotifications(true, false)).toBe(false);
    expect(includeGlobalNotifications(false, true)).toBe(false);
    expect(includeGlobalNotifications(false, false)).toBe(false);
  });
});

describe('countBellNotifications / bellBadgeLabel', () => {
  it('sums ephemeral unread + all tenant + global only when included', () => {
    expect(
      countBellNotifications({ ephemeralUnread: 2, tenantCount: 1, globalCount: 4, includeGlobal: true }),
    ).toBe(7);
    expect(
      countBellNotifications({ ephemeralUnread: 2, tenantCount: 1, globalCount: 4, includeGlobal: false }),
    ).toBe(3);
  });

  it('renders no badge at zero, exact counts up to 9, then 9+', () => {
    expect(bellBadgeLabel(0)).toBeNull();
    expect(bellBadgeLabel(1)).toBe('1');
    expect(bellBadgeLabel(9)).toBe('9');
    expect(bellBadgeLabel(10)).toBe('9+');
  });
});

describe('hasClearableNotifications', () => {
  it('ephemeral notifications are always clearable', () => {
    expect(
      hasClearableNotifications({
        ephemeralCount: 1,
        tenantCount: 0,
        visibleGlobalCount: 0,
        canDismissTenant: false,
        canDismissGlobal: false,
      }),
    ).toBe(true);
  });

  it('a read-only Global Reader viewing only global notifications gets no dead Clear-all button', () => {
    expect(
      hasClearableNotifications({
        ephemeralCount: 0,
        tenantCount: 0,
        visibleGlobalCount: 3,
        canDismissTenant: false,
        canDismissGlobal: false,
      }),
    ).toBe(false);
  });

  it('tenant notifications are clearable only with tenant dismiss rights', () => {
    const base = {
      ephemeralCount: 0,
      tenantCount: 2,
      visibleGlobalCount: 0,
      canDismissGlobal: false,
    };
    expect(hasClearableNotifications({ ...base, canDismissTenant: true })).toBe(true);
    expect(hasClearableNotifications({ ...base, canDismissTenant: false })).toBe(false);
  });

  it('global notifications are clearable only for real Global Admins', () => {
    const base = {
      ephemeralCount: 0,
      tenantCount: 0,
      visibleGlobalCount: 1,
      canDismissTenant: false,
    };
    expect(hasClearableNotifications({ ...base, canDismissGlobal: true })).toBe(true);
    expect(hasClearableNotifications({ ...base, canDismissGlobal: false })).toBe(false);
  });

  it('is false when there is nothing at all', () => {
    expect(
      hasClearableNotifications({
        ephemeralCount: 0,
        tenantCount: 0,
        visibleGlobalCount: 0,
        canDismissTenant: true,
        canDismissGlobal: true,
      }),
    ).toBe(false);
  });
});

describe('getUserInitials', () => {
  it('uses first + last name', () => {
    expect(getUserInitials('Jane Doe')).toBe('JD');
    expect(getUserInitials('Jane van der Doe')).toBe('JD');
  });
  it('uses the first letter of a single name', () => {
    expect(getUserInitials('Jane')).toBe('J');
  });
  it('falls back to the UPN, then to U', () => {
    expect(getUserInitials(undefined, 'jane@contoso.com')).toBe('J');
    expect(getUserInitials(null, null)).toBe('U');
    expect(getUserInitials('', '')).toBe('U');
  });
});

describe('formatRelativeTime', () => {
  const now = new Date('2026-08-12T12:00:00Z');
  it('buckets into just now / minutes / hours / days', () => {
    expect(formatRelativeTime(new Date('2026-08-12T11:59:30Z'), now)).toBe('Just now');
    expect(formatRelativeTime(new Date('2026-08-12T11:55:00Z'), now)).toBe('5m ago');
    expect(formatRelativeTime(new Date('2026-08-12T09:00:00Z'), now)).toBe('3h ago');
    expect(formatRelativeTime(new Date('2026-08-10T12:00:00Z'), now)).toBe('2d ago');
  });
});

describe('notification icons', () => {
  it('maps ephemeral types', () => {
    expect(ephemeralNotificationIcon('error')).toBe('🔴');
    expect(ephemeralNotificationIcon('warning')).toBe('⚠️');
    expect(ephemeralNotificationIcon('success')).toBe('✅');
    expect(ephemeralNotificationIcon('info')).toBe('ℹ️');
    expect(ephemeralNotificationIcon('anything-else')).toBe('ℹ️');
  });
  it('maps tenant and global types', () => {
    expect(tenantNotificationIcon('hardware_rejection')).toBe('🖥️');
    expect(tenantNotificationIcon('other')).toBe('🔔');
    expect(globalNotificationIcon('session_report')).toBe('📋');
    expect(globalNotificationIcon('other')).toBe('🌟');
  });
});

describe('deriveRoleBadges', () => {
  it('shows nothing for viewers and null users', () => {
    expect(deriveRoleBadges(user({ role: 'Viewer' }))).toEqual([]);
    expect(deriveRoleBadges(null)).toEqual([]);
  });

  it('suppresses Admin and Global Reader when Global Admin', () => {
    expect(
      deriveRoleBadges(user({ isGlobalAdmin: true, isGlobalReader: true, isTenantAdmin: true })),
    ).toEqual([{ key: 'global-admin', label: 'Global Admin' }]);
  });

  it('shows Global Reader for read-only platform scope', () => {
    expect(deriveRoleBadges(user({ isGlobalReader: true }))).toEqual([
      { key: 'global-reader', label: 'Global Reader' },
    ]);
  });

  it('stacks Operator with Global Reader, and shows Admin for tenant admins', () => {
    expect(deriveRoleBadges(user({ isGlobalReader: true, role: 'Operator' }))).toEqual([
      { key: 'global-reader', label: 'Global Reader' },
      { key: 'operator', label: 'Operator' },
    ]);
    expect(deriveRoleBadges(user({ isTenantAdmin: true, role: 'Admin' }))).toEqual([
      { key: 'admin', label: 'Admin' },
    ]);
  });
});
