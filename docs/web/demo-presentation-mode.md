---
type: Concept
title: Demo Presentation Mode — Two Levels of Hiding Operator Internals
description: How the portal can be shown live without leaking operator-only detail. The existing Global-Admin view toggle now also collapses the GA extras inside tenant pages, and a hidden demo mode (armed by a self-consuming ?demo=1 URL parameter) additionally removes the toggle itself, the Global-Admin badge and access to the platform area. Presentation only — every real gate stays on the server.
resource: src/Web/autopilot-monitor-web/lib/demoMode.ts
tags:
  - web
  - presentation
  - global-admin
  - operator-tooling
timestamp: 2026-09-01T20:30:00+02:00
---

# Problem

The product is demonstrated live, and the operator doing the demonstration is a platform Global Admin. Two things leaked into those sessions.

**The Global-Admin view toggle only covered navigation.** `globalAdminMode` is a localStorage view switch read by `lib/navVisibility.ts` and the scope hooks (`hooks/useAggregatedAdminScope.ts`), so switching it off collapsed the sidebar and the cross-tenant data scope. Every GA extra *inside* an otherwise tenant-scoped page was gated on the identity `user.isGlobalAdmin` instead and stayed on screen: the platform-bot Telegram provider, the cert-device-binding toggle, the retention escape hatch, and — most exposed — the Backend Build and Portal Build cards on `/health-check`, which carry version strings and commit hashes hyperlinked into the private repository.

**Nothing existed for a stage demo.** The Global-Admin toggle and the purple "Global Admin" badge were always on screen, one click away from opening the platform view mid-presentation.

# Model

Two levels, both purely presentational.

**Level 1 — the Global-Admin view toggle.** Switching it off now yields a view indistinguishable from a tenant admin's. New hook `useGlobalAdminUi()` = *real GA* **and** *global view on*; it replaces the bare identity check at every GA-exclusive **visible** surface. For quick product demos between other work.

**Level 2 — demo mode.** A separate, hidden flag that forces the global view off for every consumer, hides the toggle itself and the platform role badges, and bounces the platform routes. For conference demos, where nothing may happen by accident.

`useCanMutatePlatform()` deliberately does **not** change: it stays bound to the identity and gates the mutating controls in the `/admin` area, where a view toggle must never decide what may be written.

`user.isGlobalAdmin` is never faked globally. That would be the shorter lever but it would drag `canEditConfig` (`app/settings/TenantConfigContext.tsx`) along, and a Global Admin without an own tenant-admin role would lose every write affordance — unable to demonstrate the product at all.

# Activation

`?demo=1` on any portal URL writes `demoMode=true` to localStorage and the parameter is then **consumed**: `history.replaceState` strips it from the address bar immediately, so it survives no screenshot and needs no devtools on stage. `?demo=0` clears it the same way. `1|true|on|` (bare) arm; `0|false|off` clear; anything unrecognised returns `null` and leaves the stored value alone, so a typo cannot drop the operator out mid-presentation.

The stored `globalAdminMode` value is never overwritten — demo mode masks it at read time (`effectiveGlobalAdminMode`), so the operator's usual setting returns with `?demo=0`.

# What each level hides

| Surface | Level 1 (view toggle off) | Level 2 (demo mode) |
| --- | --- | --- |
| Purple platform section in the sidebar | hidden | hidden |
| Cross-tenant data scope, platform notifications | collapsed to the tenant | collapsed to the tenant |
| Telegram provider, cert-device-binding toggle, retention escape hatch | hidden | hidden |
| `/health-check` build + commit cards | hidden | hidden |
| "Global Admin" / "Global Reader" badge in the user menu | shows "Admin" instead | shows "Admin" instead |
| The Global-Admin toggle itself | still there | hidden |
| `/admin/*` reached by a typed URL or bookmark | Access Denied card | silent replace to `/dashboard` |
| Admin Mode (delete sessions) | unchanged | unchanged — a tenant admin has it too, so it belongs in an honest demo |

An already-configured Telegram channel stays visible and readable at both levels (`components/notifications/ChannelEditor.tsx`): it is the tenant's own channel, a tenant admin sees it too, and hiding its provider would make the next save destroy it.

# Not a security boundary

Both levels are presentation. Every rule they mirror is enforced server-side and unchanged: `TenantConfigValidation.ValidateTelegramChannelGate` rejects a non-GA Telegram write on both the PUT and the MCP field-patch path, the retention cap is validated in `ValidateModel`, and the platform endpoints answer `GlobalAdminOnly` with 403. Demo mode must never be described, documented or relied on as an access control.

# Citations

- `src/Web/autopilot-monitor-web/lib/demoMode.ts` — pure logic (parameter read/strip, effective view flag), covered by `lib/__tests__/demoMode.test.ts`
- `src/Web/autopilot-monitor-web/hooks/useAdminMode.ts` — the three modes, URL consumption, effective `globalAdminMode`
- `src/Web/autopilot-monitor-web/hooks/useGlobalAdminUi.ts` — the visible-GA-extras hook
- `src/Web/autopilot-monitor-web/components/ProtectedRoute.tsx` — platform-route bounce while presenting
- [portal-navigation-prefetch.md](portal-navigation-prefetch.md) — the other portal-wide behaviour switch
