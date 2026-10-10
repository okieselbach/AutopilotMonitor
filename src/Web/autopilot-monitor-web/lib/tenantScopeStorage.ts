/**
 * Tab-scoped persistence for the cross-tenant ("Global Admin" / delegated "MSP") tenant-switcher
 * selection. Stored in sessionStorage so the choice survives in-tab navigation between monitoring
 * pages but never leaks across tabs or browser restarts.
 *
 * Value semantics:
 *   - a tenant GUID → a concrete tenant is selected
 *   - ""            → the aggregated "All tenants" view (Global Admin only; never written by delegated)
 *   - null          → never set (the caller falls back to its own default: own tenant / first managed)
 *
 * Only an explicit user action (the selector's onChange) should write here. Auto-defaults and
 * auto-resolves must NOT persist, so a GA's aggregated ("") intent survives a detour through an
 * override-only page that can only render a concrete tenant.
 */

const STORAGE_KEY = "tenantScope";

/** Read the persisted selection, or null when nothing is stored / outside the browser. */
export function readTenantScope(): string | null {
  if (typeof window === "undefined") return null;
  try {
    return window.sessionStorage.getItem(STORAGE_KEY);
  } catch {
    // Private-mode / disabled storage — degrade to "no preference".
    return null;
  }
}

/** Persist the current selection ("" = aggregated, GUID = concrete tenant). No-op outside the browser. */
export function writeTenantScope(value: string): void {
  if (typeof window === "undefined") return;
  try {
    window.sessionStorage.setItem(STORAGE_KEY, value);
  } catch {
    // Ignore quota / disabled-storage failures — persistence is best-effort.
  }
}

/** Clear the persisted selection. */
export function clearTenantScope(): void {
  if (typeof window === "undefined") return;
  try {
    window.sessionStorage.removeItem(STORAGE_KEY);
  } catch {
    // Ignore.
  }
}
