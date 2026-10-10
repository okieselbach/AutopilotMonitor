"use client";

import { useState } from "react";
import type { TenantAdminRow } from "@/lib/generated/wire-types.generated";
import { isApplicationKey, principalLabel } from "@/lib/principalKeys";
import {
  MEMBER_ROLES,
  effectiveMemberFilter,
  effectiveMemberRole,
  matchesMemberFilter,
  matchesMemberSearch,
  memberCounts,
  memberPage,
  permissionChange,
  sortMembers,
  visibleMemberFilters,
  type MemberFilter,
} from "./memberListModel";

interface MemberListProps {
  members: TenantAdminRow[];
  loading: boolean;
  /** The signed-in user's UPN: that row shows "You" and offers no actions. */
  currentUserUpn?: string | null;
  /** False for a read-only caller: rows still open, their actions stay disabled. */
  canMutate?: boolean;
  /** The member whose removal is in flight. */
  removingUpn: string | null;
  /** The member whose role, bootstrap permission or enabled state is being saved. */
  updatingUpn: string | null;
  onRemove: (upn: string) => void | Promise<void>;
  onToggleEnabled: (upn: string, isEnabled: boolean) => void;
  onUpdatePermissions: (upn: string, role: string, canManageBootstrapTokens: boolean) => void;
}

const FILTER_LABEL: Record<MemberFilter, string> = {
  all: "All",
  Admin: "Admin",
  Operator: "Operator",
  Viewer: "Viewer",
  disabled: "Disabled",
};

const ROLE_BADGE: Record<string, string> = {
  Admin: "bg-green-100 text-green-800",
  Operator: "bg-blue-100 text-blue-800",
  Viewer: "bg-gray-100 text-gray-800",
};

const PILL = "inline-flex items-center px-2 py-0.5 rounded text-xs font-medium";
const CONTEXT_PILL = `${PILL} bg-gray-100 border border-gray-300 text-gray-600`;
const APPLICATION_TITLE =
  "Service principal — automation with its own token, or an app whose users connect through it; read-only";

/**
 * Tenant members as one line each. A click opens a row with who added it and its actions (role,
 * bootstrap permission, disable, remove with an in-row confirmation). Role chips with counts and the
 * search narrow the list; it pages at 25 rows and never shows a page past the end.
 */
export function MemberList({
  members,
  loading,
  currentUserUpn,
  canMutate = true,
  removingUpn,
  updatingUpn,
  onRemove,
  onToggleEnabled,
  onUpdatePermissions,
}: MemberListProps) {
  const [query, setQuery] = useState("");
  const [filter, setFilter] = useState<MemberFilter>("all");
  const [page, setPage] = useState(0);
  const [expandedUpn, setExpandedUpn] = useState<string | null>(null);
  const [confirmRemoveUpn, setConfirmRemoveUpn] = useState<string | null>(null);

  if (members.length === 0) {
    return <p className="text-sm text-gray-500 italic">{loading ? "Loading members…" : "No members found"}</p>;
  }

  const counts = memberCounts(members);
  const activeFilter = effectiveMemberFilter(filter, counts);
  const matching = sortMembers(
    members.filter((m) => matchesMemberFilter(m, activeFilter) && matchesMemberSearch(m, query)),
  );
  const shown = memberPage(matching, page);
  const ownUpn = currentUserUpn?.toLowerCase();

  // A vanished chip or a shrunken list moves the selection for good (guarded, converges after one extra
  // render), so a member disabled or added later never snaps the list back to the old chip or page.
  if (activeFilter !== filter) setFilter(activeFilter);
  if (shown.page !== page) setPage(shown.page);

  const changeQuery = (value: string) => {
    setQuery(value);
    setPage(0);
  };
  const toggleRow = (upn: string) => {
    setExpandedUpn((open) => (open === upn ? null : upn));
    setConfirmRemoveUpn(null);
  };
  const confirmRemove = async (upn: string) => {
    await onRemove(upn);
    setConfirmRemoveUpn(null);
  };

  return (
    <div className="space-y-2">
      <div className="flex flex-col gap-2 sm:flex-row sm:items-center">
        <div role="group" aria-label="Filter members by role" className="flex flex-wrap items-center gap-1.5">
          {visibleMemberFilters(counts).map((f) => {
            const active = f === activeFilter;
            return (
              <button
                key={f}
                type="button"
                aria-pressed={active}
                disabled={f !== "all" && counts[f] === 0}
                onClick={() => {
                  setFilter(f);
                  setPage(0);
                }}
                className={`inline-flex items-center gap-1.5 px-2.5 py-1 rounded-md border text-xs font-medium transition-colors disabled:opacity-50 disabled:cursor-not-allowed ${
                  active
                    ? "bg-purple-100 border-purple-300 text-purple-900"
                    : "bg-white border-gray-300 text-gray-700 hover:bg-gray-50"
                }`}
              >
                {FILTER_LABEL[f]}
                <span className={active ? "text-purple-700" : "text-gray-500"}>{counts[f]}</span>
              </button>
            );
          })}
        </div>
        <div className="relative sm:ml-auto sm:w-64">
          <input
            type="text"
            name="member-search"
            value={query}
            onChange={(e) => changeQuery(e.target.value)}
            placeholder="Search members..."
            aria-label="Search members"
            autoComplete="off"
            className="w-full pl-8 pr-8 py-1.5 border border-gray-300 rounded-lg text-sm text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-purple-500 focus:border-purple-500 transition-colors"
          />
          <svg className="absolute left-2.5 top-2 w-4 h-4 text-gray-400" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true">
            <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
          </svg>
          {query && (
            <button
              type="button"
              onClick={() => changeQuery("")}
              className="absolute right-2.5 top-2 text-gray-400 hover:text-gray-600 transition-colors"
              title="Clear search"
            >
              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M6 18L18 6M6 6l12 12" />
              </svg>
            </button>
          )}
        </div>
      </div>

      {matching.length === 0 ? (
        <p className="text-sm text-gray-500 italic p-4 text-center bg-gray-50 rounded-lg">No members match your search</p>
      ) : (
        <ul className="border border-gray-200 rounded-lg divide-y divide-gray-100 bg-white">
          {shown.rows.map((m) => (
            <MemberRow
              key={m.upn}
              member={m}
              open={expandedUpn === m.upn}
              confirmingRemove={confirmRemoveUpn === m.upn}
              isCurrentUser={ownUpn !== undefined && m.upn.toLowerCase() === ownUpn}
              canMutate={canMutate}
              removing={removingUpn === m.upn}
              updating={updatingUpn === m.upn}
              onToggleOpen={() => toggleRow(m.upn)}
              onAskRemove={() => setConfirmRemoveUpn(m.upn)}
              onCancelRemove={() => setConfirmRemoveUpn(null)}
              onConfirmRemove={() => void confirmRemove(m.upn)}
              onToggleEnabled={() => onToggleEnabled(m.upn, m.isEnabled)}
              onSavePermissions={(role, canManageBootstrapTokens) => onUpdatePermissions(m.upn, role, canManageBootstrapTokens)}
            />
          ))}
        </ul>
      )}

      {shown.pageCount > 1 && (
        <div className="flex items-center justify-between pt-1">
          <button
            type="button"
            onClick={() => setPage(shown.page - 1)}
            disabled={shown.page === 0}
            className="px-3 py-1.5 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-lg hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
          >
            Previous
          </button>
          <span className="text-xs text-gray-600">
            {`Page ${shown.page + 1} of ${shown.pageCount} (${matching.length} members)`}
          </span>
          <button
            type="button"
            onClick={() => setPage(shown.page + 1)}
            disabled={shown.page >= shown.pageCount - 1}
            className="px-3 py-1.5 text-sm font-medium text-gray-700 bg-white border border-gray-300 rounded-lg hover:bg-gray-50 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
          >
            Next
          </button>
        </div>
      )}
    </div>
  );
}

interface MemberRowProps {
  member: TenantAdminRow;
  open: boolean;
  confirmingRemove: boolean;
  isCurrentUser: boolean;
  canMutate: boolean;
  removing: boolean;
  updating: boolean;
  onToggleOpen: () => void;
  onAskRemove: () => void;
  onCancelRemove: () => void;
  onConfirmRemove: () => void;
  onToggleEnabled: () => void;
  onSavePermissions: (role: string, canManageBootstrapTokens: boolean) => void;
}

function MemberRow({ member, open, onToggleOpen, ...details }: MemberRowProps) {
  const isApplication = isApplicationKey(member.upn);
  const role = effectiveMemberRole(member);
  const label = principalLabel(member.upn);
  const added = new Date(member.addedDate).toLocaleDateString();

  return (
    <li className={open ? "bg-gray-50" : undefined}>
      <button
        type="button"
        aria-expanded={open}
        onClick={onToggleOpen}
        className="w-full flex items-start sm:items-center gap-2 px-3 py-2 text-left text-sm hover:bg-gray-50 focus:outline-none focus-visible:ring-2 focus-visible:ring-inset focus-visible:ring-purple-500"
      >
        <span className="flex min-w-0 flex-1 flex-wrap sm:flex-nowrap items-center gap-x-2 gap-y-1">
          <span
            className={`min-w-0 basis-full sm:basis-auto break-all sm:truncate font-medium ${member.isEnabled ? "text-gray-900" : "text-gray-500"}`}
            title={label}
          >
            {label}
          </span>
          <span className="flex flex-shrink-0 flex-wrap items-center gap-1.5">
            {details.isCurrentUser && <span className={CONTEXT_PILL}>You</span>}
            {isApplication && (
              <span className={`${PILL} bg-gray-100 text-gray-700`} title={APPLICATION_TITLE}>
                App
              </span>
            )}
            <span className={`${PILL} ${ROLE_BADGE[role] ?? "bg-gray-100 text-gray-600"}`}>{role}</span>
            {role === "Operator" && member.canManageBootstrapTokens && (
              <span className={CONTEXT_PILL} title="Can manage bootstrap tokens">
                Bootstrap tokens
              </span>
            )}
            {!member.isEnabled && <span className={`${PILL} bg-gray-200 text-gray-700`}>Disabled</span>}
          </span>
        </span>
        <span className="hidden sm:inline flex-shrink-0 text-xs text-gray-400">{`Added ${added}`}</span>
        <svg
          className={`mt-0.5 sm:mt-0 h-4 w-4 flex-shrink-0 text-gray-400 transition-transform ${open ? "rotate-180" : ""}`}
          fill="none"
          stroke="currentColor"
          viewBox="0 0 24 24"
          aria-hidden="true"
        >
          <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
        </svg>
      </button>

      {open && <MemberDetails member={member} label={label} added={added} {...details} />}
    </li>
  );
}

interface MemberDetailsProps extends Omit<MemberRowProps, "open" | "onToggleOpen"> {
  label: string;
  added: string;
}

/**
 * The open part of a row. Role and bootstrap permission stay drafts until Save, so a stray select change
 * never alters anyone's access; the part unmounts with the closed row, which discards an unsaved draft.
 */
function MemberDetails({
  member,
  label,
  added,
  confirmingRemove,
  isCurrentUser,
  canMutate,
  removing,
  updating,
  onAskRemove,
  onCancelRemove,
  onConfirmRemove,
  onToggleEnabled,
  onSavePermissions,
}: MemberDetailsProps) {
  const isApplication = isApplicationKey(member.upn);
  const role = effectiveMemberRole(member);
  const [draftRole, setDraftRole] = useState(role);
  const [draftBootstrap, setDraftBootstrap] = useState(member.canManageBootstrapTokens);
  const change = permissionChange(member, draftRole, draftBootstrap);
  const busy = removing || updating;

  const discard = () => {
    setDraftRole(role);
    setDraftBootstrap(member.canManageBootstrapTokens);
  };

  return (
    <div className="space-y-2 px-3 pb-3 text-sm">
      <p className="text-xs text-gray-500">{`Added ${added} by ${member.addedBy}`}</p>
      {isCurrentUser ? (
        <p className="text-xs text-gray-500">This is your own entry. Only another admin can change its role or access.</p>
      ) : (
        <div className="flex flex-wrap items-center gap-x-4 gap-y-2">
          <label className="flex items-center gap-2 text-gray-700">
            Role
            <select
              value={draftRole}
              onChange={(e) => setDraftRole(e.target.value)}
              disabled={!canMutate || isApplication || busy}
              title={isApplication ? "A service principal is always read-only (Viewer)" : undefined}
              className="px-2 py-1 text-sm border border-gray-300 rounded bg-white text-gray-700 focus:outline-none focus:ring-1 focus:ring-purple-500 disabled:opacity-50"
            >
              {MEMBER_ROLES.map((r) => (
                <option key={r} value={r}>
                  {r}
                </option>
              ))}
            </select>
          </label>
          {draftRole === "Operator" && (
            <label className="flex items-center gap-2 text-gray-700 cursor-pointer">
              <input
                type="checkbox"
                checked={draftBootstrap}
                onChange={(e) => setDraftBootstrap(e.target.checked)}
                disabled={!canMutate || busy}
                className="h-4 w-4 text-purple-600 rounded border-gray-300 focus:ring-purple-500 disabled:opacity-50"
              />
              Can manage bootstrap tokens
            </label>
          )}
          {!isApplication && (
            <span className="flex items-center gap-2">
              <button
                type="button"
                onClick={() => onSavePermissions(draftRole, change.canManageBootstrapTokens)}
                disabled={!canMutate || busy || !change.dirty}
                className="px-3 py-1 text-sm bg-purple-600 text-white rounded hover:bg-purple-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
              >
                Save
              </button>
              {change.dirty && (
                <button
                  type="button"
                  onClick={discard}
                  disabled={busy}
                  className="px-2 py-1 text-sm text-gray-600 hover:text-gray-800 disabled:opacity-50"
                >
                  Discard
                </button>
              )}
            </span>
          )}
          <span className="flex flex-wrap items-center gap-2 sm:ml-auto">
            {confirmingRemove ? (
              <>
                <span className="text-xs text-gray-600">{`Remove ${label}?`}</span>
                <button
                  type="button"
                  onClick={onConfirmRemove}
                  disabled={!canMutate || busy}
                  className="px-3 py-1 text-sm bg-red-600 text-white rounded hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                >
                  {removing ? "Removing..." : "Remove"}
                </button>
                <button
                  type="button"
                  onClick={onCancelRemove}
                  disabled={removing}
                  className="px-2 py-1 text-sm text-gray-600 hover:text-gray-800 disabled:opacity-50"
                >
                  Cancel
                </button>
              </>
            ) : (
              <>
                <button
                  type="button"
                  onClick={onToggleEnabled}
                  disabled={!canMutate || busy}
                  className={`px-3 py-1 text-sm text-white rounded transition-colors disabled:opacity-50 disabled:cursor-not-allowed ${
                    member.isEnabled ? "bg-yellow-600 hover:bg-yellow-700" : "bg-green-600 hover:bg-green-700"
                  }`}
                >
                  {updating ? "..." : member.isEnabled ? "Disable" : "Enable"}
                </button>
                <button
                  type="button"
                  onClick={onAskRemove}
                  disabled={!canMutate || busy}
                  className="px-3 py-1 text-sm bg-red-600 text-white rounded hover:bg-red-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors"
                >
                  Remove
                </button>
              </>
            )}
          </span>
        </div>
      )}
    </div>
  );
}
