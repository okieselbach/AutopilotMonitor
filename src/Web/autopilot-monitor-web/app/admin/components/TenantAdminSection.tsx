"use client";

import { useEffect, useState } from "react";
import { api } from "@/lib/api";
import { apiErrorText, fetchJson, fetchOk, jsonBody } from "@/lib/apiClient";
import { useCanMutatePlatform } from "@/hooks/useCanMutatePlatform";
import { AddMemberForm } from "@/components/members/AddMemberForm";
import { MemberList } from "@/components/members/MemberList";
import type { AddTenantAdminRequest, TenantAdminRow, UpdateMemberPermissionsRequest } from "@/utils/wire-types.generated";
import { looksLikeGuid, type MemberKind } from "@/utils/principalKeys";

// Wire type is generated from the backend DTO ("role" is absent for legacy pre-role rows).
type TenantAdmin = TenantAdminRow;

interface TenantAdminSectionProps {
  tenantId: string;
  getAccessToken: () => Promise<string | null>;
  setError: (error: string | null) => void;
  setSuccessMessage: (message: string | null) => void;
}

export function TenantAdminSection({
  tenantId,
  getAccessToken,
  setError,
  setSuccessMessage,
}: TenantAdminSectionProps) {
  // Read-only Global Readers may view tenant members but not add/remove/toggle them.
  const canMutate = useCanMutatePlatform();
  const [tenantAdmins, setTenantAdmins] = useState<TenantAdmin[]>([]);
  const [loadingAdmins, setLoadingAdmins] = useState(false);
  const [newAdminEmail, setNewAdminEmail] = useState("");
  const [newMemberRole, setNewMemberRole] = useState<string>("Admin");
  const [newMemberKind, setNewMemberKind] = useState<MemberKind>("user");
  const addingApplication = newMemberKind === "application";
  const newMemberInputValid = addingApplication ? looksLikeGuid(newAdminEmail) : newAdminEmail.trim().length > 0;
  const [addingAdmin, setAddingAdmin] = useState(false);
  const [removingAdmin, setRemovingAdmin] = useState<string | null>(null);
  const [togglingAdmin, setTogglingAdmin] = useState<string | null>(null);

  // Fetch tenant admins when tenantId changes
  const fetchTenantAdmins = async (tid: string) => {
    try {
      setLoadingAdmins(true);
      const data = await fetchJson<TenantAdmin[]>(api.tenants.admins(tid), getAccessToken);
      setTenantAdmins(data);
    } catch (err) {
      console.error("Error fetching tenant admins:", err);
      setError(apiErrorText(err, "Failed to load tenant admins"));
    } finally {
      setLoadingAdmins(false);
    }
  };

  useEffect(() => {
    const run = async () => {
      await fetchTenantAdmins(tenantId);
    };
    void run();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [tenantId]);

  const handleAddTenantAdmin = async () => {
    if (!canMutate) return; // read-only Global Reader — also closes the Enter-key path past disabled buttons
    if (!newMemberInputValid) return;

    try {
      setAddingAdmin(true);
      setError(null);

      const body = addingApplication
        ? { applicationId: newAdminEmail.trim(), role: "Viewer", canManageBootstrapTokens: false }
        : { upn: newAdminEmail.trim(), role: newMemberRole, canManageBootstrapTokens: false };
      await fetchOk(api.tenants.admins(tenantId), getAccessToken, {
        method: "POST",
        body: jsonBody<AddTenantAdminRequest>(body),
      });

      setSuccessMessage(addingApplication
        ? `Service principal ${newAdminEmail.trim()} added as Viewer.`
        : `${newMemberRole} ${newAdminEmail} added successfully!`);
      setNewAdminEmail("");

      // Refresh admin list
      await fetchTenantAdmins(tenantId);

      // Auto-hide success message after 3 seconds
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      console.error("Error adding tenant admin:", err);
      setError(apiErrorText(err, "Failed to add admin"));
    } finally {
      setAddingAdmin(false);
    }
  };

  const handleRemoveTenantAdmin = async (adminUpn: string) => {
    if (!canMutate) return; // read-only Global Reader (the row asks for confirmation first)
    try {
      setRemovingAdmin(adminUpn);
      setError(null);

      await fetchOk(api.tenants.admin(tenantId, adminUpn), getAccessToken, {
        method: "DELETE",
      });

      setSuccessMessage(`Admin ${adminUpn} removed successfully!`);

      // Refresh admin list
      await fetchTenantAdmins(tenantId);

      // Auto-hide success message after 3 seconds
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      console.error("Error removing tenant admin:", err);
      setError(apiErrorText(err, "Failed to remove admin"));
    } finally {
      setRemovingAdmin(null);
    }
  };

  const handleToggleTenantAdmin = async (adminUpn: string, isEnabled: boolean) => {
    if (!canMutate) return; // read-only Global Reader
    try {
      setTogglingAdmin(adminUpn);
      setError(null);

      const action = isEnabled ? 'disable' : 'enable';
      await fetchOk(api.tenants.adminAction(tenantId, adminUpn, action), getAccessToken, { method: "PATCH" });

      setSuccessMessage(`Admin ${adminUpn} ${isEnabled ? 'disabled' : 'enabled'} successfully!`);

      // Refresh admin list
      await fetchTenantAdmins(tenantId);

      // Auto-hide success message after 3 seconds
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      console.error("Error toggling tenant admin:", err);
      setError(apiErrorText(err, "Failed to toggle admin"));
    } finally {
      setTogglingAdmin(null);
    }
  };

  const handleUpdatePermissions = async (adminUpn: string, role: string, canManageBootstrapTokens: boolean) => {
    if (!canMutate) return; // read-only Global Reader (consistency with the other mutation handlers)
    try {
      setTogglingAdmin(adminUpn);
      setError(null);

      await fetchOk(api.tenants.adminPermissions(tenantId, adminUpn), getAccessToken, {
        method: "PATCH",
        body: jsonBody<UpdateMemberPermissionsRequest>({ role, canManageBootstrapTokens }),
      });

      setSuccessMessage(`Permissions for ${adminUpn} updated successfully!`);

      // Refresh admin list
      await fetchTenantAdmins(tenantId);

      // Auto-hide success message after 3 seconds
      setTimeout(() => setSuccessMessage(null), 3000);
    } catch (err) {
      console.error("Error updating member permissions:", err);
      setError(apiErrorText(err, "Failed to update permissions"));
    } finally {
      setTogglingAdmin(null);
    }
  };

  return (
    <div className="bg-purple-50 border border-purple-200 rounded-lg p-4 space-y-4">
      <h3 className="font-semibold text-purple-900">
        Members
        {loadingAdmins && <span className="ml-2 text-sm font-normal text-purple-700">(Loading...)</span>}
      </h3>

      <AddMemberForm
        value={newAdminEmail}
        onValueChange={setNewAdminEmail}
        role={newMemberRole}
        onRoleChange={setNewMemberRole}
        kind={newMemberKind}
        onKindChange={setNewMemberKind}
        adding={addingAdmin}
        onAdd={handleAddTenantAdmin}
        disabled={!canMutate}
        stacked
      />

      {/* key: a tenant switch starts on page 1 with no filter, search or open row. */}
      <MemberList
        key={tenantId}
        members={tenantAdmins}
        loading={loadingAdmins}
        canMutate={canMutate}
        removingUpn={removingAdmin}
        updatingUpn={togglingAdmin}
        onRemove={handleRemoveTenantAdmin}
        onToggleEnabled={handleToggleTenantAdmin}
        onUpdatePermissions={handleUpdatePermissions}
      />
    </div>
  );
}
