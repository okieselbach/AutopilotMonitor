"use client";

import { looksLikeGuid, type MemberKind } from "@/utils/principalKeys";
import { MEMBER_ROLES } from "./memberListModel";

interface AddMemberFormProps {
  value: string;
  onValueChange: (value: string) => void;
  role: string;
  onRoleChange: (role: string) => void;
  kind: MemberKind;
  onKindChange: (kind: MemberKind) => void;
  adding: boolean;
  onAdd: () => void;
  /** Read-only caller: the form stays visible but cannot submit. */
  disabled?: boolean;
}

const KINDS = [["user", "User"], ["application", "Service principal"]] as const;

const USER_HINT = "Enter the user email (UPN) and select a role to grant access.";
const APPLICATION_HINT =
  "Enter the application (client) ID of an app in your Entra tenant; it is always read-only (Viewer). Automation calling with its own token needs the access_as_application permission. An app that signs users in and sends their tokens needs the delegated access_as_user permission, and its users can only read. Grant either by admin consent in your Entra tenant.";

/** Adds a person by UPN or a service principal by application ID; a service principal is always a Viewer. */
export function AddMemberForm({
  value,
  onValueChange,
  role,
  onRoleChange,
  kind,
  onKindChange,
  adding,
  onAdd,
  disabled = false,
}: AddMemberFormProps) {
  const addingApplication = kind === "application";
  const valid = addingApplication ? looksLikeGuid(value) : value.trim().length > 0;
  const canSubmit = !disabled && !adding && valid;

  return (
    <div>
      <h3 className="text-gray-700 font-medium">Add member</h3>
      <p className="mt-0.5 mb-2 text-sm text-gray-500">{addingApplication ? APPLICATION_HINT : USER_HINT}</p>
      <div className="flex flex-wrap gap-2">
        <div className="inline-flex rounded-lg border border-gray-300 overflow-hidden text-sm" role="group" aria-label="Member type">
          {KINDS.map(([k, label]) => (
            <button
              key={k}
              type="button"
              aria-pressed={kind === k}
              onClick={() => {
                onKindChange(k);
                onValueChange("");
              }}
              className={`px-3 py-2 ${kind === k ? "bg-purple-600 text-white" : "bg-white text-gray-700 hover:bg-gray-50"}`}
            >
              {label}
            </button>
          ))}
        </div>
        <input
          type={addingApplication ? "text" : "email"}
          name="new-member"
          value={value}
          onChange={(e) => onValueChange(e.target.value)}
          placeholder={addingApplication ? "Application (client) ID, e.g. 00000000-0000-0000-0000-000000000000" : "user@tenant.com"}
          aria-label={addingApplication ? "Application (client) ID" : "User email (UPN)"}
          autoComplete="off"
          className="w-full sm:w-auto sm:flex-1 min-w-0 px-4 py-2 border border-gray-300 rounded-lg text-gray-900 placeholder-gray-500 focus:outline-none focus:ring-2 focus:ring-purple-500 focus:border-purple-500 transition-colors"
          onKeyDown={(e) => {
            if (e.key === "Enter") {
              e.preventDefault();
              if (canSubmit) onAdd();
            }
          }}
        />
        <select
          value={addingApplication ? "Viewer" : role}
          onChange={(e) => onRoleChange(e.target.value)}
          disabled={addingApplication}
          aria-label="Role"
          title={addingApplication ? "A service principal is always read-only (Viewer)" : undefined}
          className="px-3 py-2 border border-gray-300 rounded-lg text-gray-700 bg-white focus:outline-none focus:ring-2 focus:ring-purple-500 focus:border-purple-500 transition-colors disabled:opacity-50"
        >
          {MEMBER_ROLES.map((r) => (
            <option key={r} value={r}>
              {r}
            </option>
          ))}
        </select>
        <button
          type="button"
          onClick={onAdd}
          disabled={!canSubmit}
          className="px-6 py-2 bg-purple-600 text-white rounded-lg hover:bg-purple-700 disabled:opacity-50 disabled:cursor-not-allowed transition-colors flex items-center gap-2"
        >
          {adding ? (
            <>
              <span className="animate-spin rounded-full h-4 w-4 border-b-2 border-white" aria-hidden="true" />
              Adding...
            </>
          ) : (
            <>
              <svg className="w-5 h-5" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 4v16m8-8H4" />
              </svg>
              Add
            </>
          )}
        </button>
      </div>
    </div>
  );
}
