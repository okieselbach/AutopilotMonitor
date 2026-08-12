---
type: Concept
title: Navbar Architecture — Policy Module + Composed Surfaces
description: How the top navbar is structured after the 2026-08 refactor — every gating decision (variant, capabilities, bell badge, clearability, role badges) lives in the pure, unit-tested lib/navbarPolicy.ts, and the desktop/mobile surfaces render shared components so they cannot drift apart. Records the drift bugs the old monolith produced and the rules that prevent their return.
resource: src/Web/autopilot-monitor-web/lib/navbarPolicy.ts
tags:
  - web
  - architecture
  - navbar
  - rbac
  - testing
timestamp: 2026-08-12T18:00:00+02:00
---

# Schema

The navbar used to be one ~810-line component (`components/Navbar.tsx`) holding
five independent dropdown booleans, inline role gating, and three hand-copied
variants of the same menu content (desktop dropdowns, mobile overflow submenus,
minimal-navbar user menu). Copies drifted: Operators got an empty settings gear
on desktop, Global Readers had no settings entry at all in the mobile overflow,
and dead `previewMode` state (written, never read anywhere) accumulated. None of
the gating logic was testable.

The refactor splits the navbar into two layers:

**Decision layer — `lib/navbarPolicy.ts` (pure, unit-tested).** Every rule the
navbar applies is a pure function here, covered by
`lib/__tests__/navbarPolicy.test.ts`:

* `resolveNavbarVariant` — `hidden` (landing page / unauthenticated), `minimal`
  (regular members: no Admin/Operator role, no platform scope), `full`.
* `deriveNavbarCapabilities` — one struct answering every "may this user…?"
  question: `canDismissTenant` (tenant-shared dismiss → Admin/GA only),
  `canDismissGlobal` (real GA only), `showAdminToggle`, `showGlobalToggle`, and
  `showSettingsMenu` (= any toggle available; gates gear AND overflow entry).
* Bell math: `includeGlobalNotifications` (platform scope AND global mode on),
  `countBellNotifications`, `bellBadgeLabel` (`9+` cap),
  `hasClearableNotifications` (no dead "Clear all" for read-only Global
  Readers).
* Presentation-adjacent pure helpers: `getUserInitials`, `formatRelativeTime`,
  the three per-lane notification icon maps, `deriveRoleBadges` (stronger badge
  suppresses weaker: Global Admin hides Admin/Global Reader).

**Render layer — `components/navbar/*`.** `components/Navbar.tsx` is a thin
orchestrator; each surface exists exactly once:

* `useNavbarMenus` — a single `openMenu: NavbarMenuId | null` plus one
  outside-click listener replaces five booleans/refs. Two menus can never be
  open at once, and a new menu is an id in the union, not a new state + ref +
  handler branch.
* Shared content components consumed by BOTH desktop and mobile surfaces:
  `HelpMenuItems` (rendering `helpLinks.ts`, the single help-link list),
  `AdminModeToggles`, `UserMenu` (also reused by the minimal variant), and
  `NotificationRow` (one row for the tenant/GA/ephemeral lanes).
* Surface components: `NotificationBell`, `SettingsMenu`, `HelpMenu`,
  `OverflowMenu`. Surfaces differ only in placement/classes (passed as a
  `variant` prop), never in content or gating.
* `icons.tsx` — all SVG path data in one map (`MenuIcon`), so glyphs cannot
  fork between surfaces.

# Rules

* New gating logic goes into `navbarPolicy.ts` with a test — never inline in a
  component. If a component contains an `&&` combining role flags, it is in the
  wrong layer.
* New menu entries go into the shared data/content component (`helpLinks.ts`,
  `AdminModeToggles`, …), never into one surface. Both surfaces pick them up
  automatically.
* The vitest setup runs pure `.ts` tests only (no DOM). Keep components thin so
  everything worth testing stays in the policy module.

# Examples

Adding a help link = one entry in `helpLinks.ts` (appears on desktop + mobile).
Adding a role-gated control = a capability in `deriveNavbarCapabilities` + a
test + one consumer in the shared content component.

# Citations

* `src/Web/autopilot-monitor-web/lib/navbarPolicy.ts`
* `src/Web/autopilot-monitor-web/lib/__tests__/navbarPolicy.test.ts`
* `src/Web/autopilot-monitor-web/components/Navbar.tsx`
* `src/Web/autopilot-monitor-web/components/navbar/`
