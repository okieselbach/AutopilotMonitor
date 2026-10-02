"use client";

import { useState } from "react";
import { TenantAdmin } from "../types";
import { SectionCardHeader } from "@/components/SectionCardHeader";
import { AddMemberForm } from "@/components/members/AddMemberForm";
import { MemberList } from "@/components/members/MemberList";
import { DOCS_PATHS } from "@/lib/docsPaths";
import type { MemberKind } from "@/utils/principalKeys";

interface AdminManagementSectionProps {
  admins: TenantAdmin[];
  loadingAdmins: boolean;
  newAdminEmail: string;
  setNewAdminEmail: (value: string) => void;
  newMemberRole: string;
  setNewMemberRole: (value: string) => void;
  newMemberKind: MemberKind;
  setNewMemberKind: (value: MemberKind) => void;
  addingAdmin: boolean;
  removingAdmin: string | null;
  togglingAdmin: string | null;
  user: { upn?: string } | null;
  /** The tenant also takes roles from Entra app-role assignments (an operator opt-in). */
  entraAppRolesEnabled: boolean;
  onAddAdmin: () => void;
  onRemoveAdmin: (upn: string) => Promise<void>;
  onToggleAdmin: (upn: string, isEnabled: boolean) => void;
  onUpdatePermissions: (upn: string, role: string, canManageBootstrapTokens: boolean) => void;
}

export default function AdminManagementSection({
  admins,
  loadingAdmins,
  newAdminEmail,
  setNewAdminEmail,
  newMemberRole,
  setNewMemberRole,
  newMemberKind,
  setNewMemberKind,
  addingAdmin,
  removingAdmin,
  togglingAdmin,
  user,
  entraAppRolesEnabled,
  onAddAdmin,
  onRemoveAdmin,
  onToggleAdmin,
  onUpdatePermissions,
}: AdminManagementSectionProps) {
  const [rolesOpen, setRolesOpen] = useState(false);

  return (
    <div className="bg-white rounded-lg shadow">
      <SectionCardHeader
        tone="purple"
        iconPath="M12 4.354a4 4 0 110 5.292M15 21H3v-1a6 6 0 0112 0v1zm0 0h6v-1a6 6 0 00-9-5.197M13 7a4 4 0 11-8 0 4 4 0 018 0z"
        title="Access Management"
        subtitle="Manage team members and their roles for this tenant"
        docsPath={DOCS_PATHS.accessManagement}
      />
      <div className="p-6 space-y-5">
        <div className="bg-blue-50 border border-blue-200 rounded-lg">
          <button
            type="button"
            aria-expanded={rolesOpen}
            onClick={() => setRolesOpen((open) => !open)}
            className="w-full flex items-center gap-3 px-4 py-2.5 text-left"
          >
            <svg className="w-5 h-5 flex-shrink-0 text-blue-600" fill="none" stroke="currentColor" viewBox="0 0 24 24" aria-hidden="true">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
            </svg>
            <span className="flex-1 text-sm font-medium text-blue-800">About Roles</span>
            <svg
              className={`w-4 h-4 flex-shrink-0 text-blue-600 transition-transform ${rolesOpen ? "rotate-180" : ""}`}
              fill="none"
              stroke="currentColor"
              viewBox="0 0 24 24"
              aria-hidden="true"
            >
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 9l-7 7-7-7" />
            </svg>
          </button>
          {rolesOpen && (
            <div className="px-4 pb-3 pl-12 text-sm text-blue-800">
              <p>
                <strong>Member</strong> — No role assigned. Sees only the Progress Portal for their own enrollments.
              </p>
              <p className="mt-1">
                <strong>Viewer</strong> — Read-only access to everything: sessions, rules, settings, and reports. Cannot change anything or trigger actions.
              </p>
              <p className="mt-1">
                <strong>Operator</strong> — Day-to-day operations: dashboard, sessions, and monitoring. Can optionally manage bootstrap tokens if permitted.
              </p>
              <p className="mt-1">
                <strong>Admin</strong> — Full management: all tenant configuration, sessions, diagnostics, and settings.
              </p>
              <p className="mt-2">
                <strong>Your email:</strong> {user?.upn}
              </p>
            </div>
          )}
        </div>

        <AddMemberForm
          value={newAdminEmail}
          onValueChange={setNewAdminEmail}
          role={newMemberRole}
          onRoleChange={setNewMemberRole}
          kind={newMemberKind}
          onKindChange={setNewMemberKind}
          adding={addingAdmin}
          onAdd={onAddAdmin}
        />

        <div>
          <h3 className="mb-2 text-gray-700 font-medium">
            Team members
            {loadingAdmins && <span className="ml-2 text-sm font-normal text-gray-500">(Loading...)</span>}
          </h3>
          {entraAppRolesEnabled && (
            <p className="mb-2 text-xs text-gray-500">
              Entra app roles are enabled for this tenant. Users with the Admin, Operator or Viewer app role on the enterprise application get that role when they sign in, without an entry here. An entry here always takes precedence, and a disabled entry blocks access.
            </p>
          )}
          <MemberList
            members={admins}
            loading={loadingAdmins}
            currentUserUpn={user?.upn}
            removingUpn={removingAdmin}
            updatingUpn={togglingAdmin}
            onRemove={onRemoveAdmin}
            onToggleEnabled={onToggleAdmin}
            onUpdatePermissions={onUpdatePermissions}
          />
        </div>

        <div className="bg-yellow-50 border border-yellow-200 rounded-lg p-3">
          <div className="flex items-start space-x-2">
            <svg className="w-5 h-5 text-yellow-600 mt-0.5" fill="none" stroke="currentColor" viewBox="0 0 24 24">
              <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-3L13.732 4c-.77-1.333-2.694-1.333-3.464 0L3.34 16c-.77 1.333.192 3 1.732 3z" />
            </svg>
            <p className="text-sm text-yellow-800">
              <strong>Important:</strong> Make sure to keep at least one Admin in the list to maintain full access!
              The first user to log in was automatically made an admin.
            </p>
          </div>
        </div>
      </div>
    </div>
  );
}
