import type { TenantAdminRow } from "@/lib/generated/wire-types.generated";
import { isApplicationKey, principalLabel } from "@/lib/principalKeys";

/** Tenant roles a member row can grant, highest first. */
export const MEMBER_ROLES = ["Admin", "Operator", "Viewer"] as const;
export type MemberRole = (typeof MEMBER_ROLES)[number];

/** Chip filter of the member list: every member, one role, or the disabled entries. */
export type MemberFilter = "all" | MemberRole | "disabled";

/** Rows per page: one-line rows, so a large tenant fits on one or two pages. */
export const MEMBER_PAGE_SIZE = 25;

type MemberFields = Pick<TenantAdminRow, "upn" | "role" | "isEnabled">;

/**
 * The role a row grants. A row without a role predates roles and is an Admin; a service principal is
 * always read-only, whatever its row says (the backend caps it the same way).
 */
export function effectiveMemberRole(row: Pick<TenantAdminRow, "upn" | "role">): string {
  if (isApplicationKey(row.upn)) return "Viewer";
  return row.role ?? "Admin";
}

export type MemberCounts = Record<MemberFilter, number>;

export function memberCounts(rows: readonly MemberFields[]): MemberCounts {
  const counts: MemberCounts = { all: rows.length, Admin: 0, Operator: 0, Viewer: 0, disabled: 0 };
  for (const row of rows) {
    const role = effectiveMemberRole(row);
    if (role === "Admin" || role === "Operator" || role === "Viewer") counts[role]++;
    if (!row.isEnabled) counts.disabled++;
  }
  return counts;
}

/**
 * The chips the list shows: All and the three roles always, so the row does not jump while counts change;
 * Disabled only while an entry is disabled.
 */
export function visibleMemberFilters(counts: MemberCounts): MemberFilter[] {
  const filters: MemberFilter[] = ["all", ...MEMBER_ROLES];
  if (counts.disabled > 0) filters.push("disabled");
  return filters;
}

/** A selected chip whose last entry was removed or changed falls back to All instead of an empty list. */
export function effectiveMemberFilter(filter: MemberFilter, counts: MemberCounts): MemberFilter {
  return filter === "all" || counts[filter] > 0 ? filter : "all";
}

export function matchesMemberFilter(row: MemberFields, filter: MemberFilter): boolean {
  if (filter === "all") return true;
  if (filter === "disabled") return !row.isEnabled;
  return effectiveMemberRole(row) === filter;
}

/** Search matches the shown name (UPN, or "Service principal <client-id>"), ignoring case. */
export function matchesMemberSearch(row: Pick<TenantAdminRow, "upn">, query: string): boolean {
  const q = query.trim().toLowerCase();
  return q === "" || principalLabel(row.upn).toLowerCase().includes(q);
}

/** People first, then service principals; each group alphabetically by the shown name. */
export function sortMembers<T extends Pick<TenantAdminRow, "upn">>(rows: readonly T[]): T[] {
  return [...rows].sort((a, b) => {
    const appA = isApplicationKey(a.upn);
    const appB = isApplicationKey(b.upn);
    if (appA !== appB) return appA ? 1 : -1;
    return principalLabel(a.upn).localeCompare(principalLabel(b.upn), undefined, { sensitivity: "base" });
  });
}

export interface PermissionChange {
  /** Something differs from the stored row, so Save has work to do. */
  dirty: boolean;
  /** The bootstrap flag Save writes: the draft for an Operator, otherwise the stored value unchanged. */
  canManageBootstrapTokens: boolean;
}

/**
 * The write behind an open row's Save button. Role and bootstrap permission are drafts until Save, so a
 * stray select change never alters anyone's access on its own.
 */
export function permissionChange(
  row: Pick<TenantAdminRow, "upn" | "role" | "canManageBootstrapTokens">,
  draftRole: string,
  draftBootstrap: boolean,
): PermissionChange {
  const canManageBootstrapTokens = draftRole === "Operator" ? draftBootstrap : row.canManageBootstrapTokens;
  return {
    dirty: draftRole !== effectiveMemberRole(row) || canManageBootstrapTokens !== row.canManageBootstrapTokens,
    canManageBootstrapTokens,
  };
}

export interface MemberPage<T> {
  rows: T[];
  /** The page actually shown — clamped, so a list that shrank never shows an empty page. */
  page: number;
  pageCount: number;
}

export function memberPage<T>(rows: readonly T[], page: number, pageSize: number = MEMBER_PAGE_SIZE): MemberPage<T> {
  const pageCount = Math.max(1, Math.ceil(rows.length / pageSize));
  const shown = Math.min(Math.max(0, page), pageCount - 1);
  return { rows: rows.slice(shown * pageSize, (shown + 1) * pageSize), page: shown, pageCount };
}
