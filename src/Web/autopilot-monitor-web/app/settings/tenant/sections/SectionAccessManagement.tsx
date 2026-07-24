"use client";

import { useTenantConfig } from "../../TenantConfigContext";
import { TenantNotifications } from "../../TenantNotifications";
import AdminManagementSection from "../../components/AdminManagementSection";

export function SectionAccessManagement() {
  const {
    canEditConfig,
    admins, loadingAdmins,
    newAdminEmail, setNewAdminEmail,
    newMemberRole, setNewMemberRole,
    addingAdmin, removingAdmin, togglingAdmin,
    adminSearchQuery, setAdminSearchQuery,
    currentAdminPage, setCurrentAdminPage,
    user,
    handleAddAdmin, handleRemoveAdmin,
    handleToggleTenantAdmin, handleUpdatePermissions,
  } = useTenantConfig();

  // Operators do not manage members, and the member list itself is admin-tier data
  // (the backend never serves it to them) — hide the section instead of rendering
  // an empty table with dead controls.
  if (!canEditConfig) {
    return (
      <div className="bg-amber-50 border border-amber-200 rounded-lg p-4 text-sm text-amber-800">
        This page is available to tenant administrators only.
      </div>
    );
  }

  return (
    <>
      <TenantNotifications />
      <AdminManagementSection
        admins={admins}
        loadingAdmins={loadingAdmins}
        newAdminEmail={newAdminEmail}
        setNewAdminEmail={setNewAdminEmail}
        newMemberRole={newMemberRole}
        setNewMemberRole={setNewMemberRole}
        addingAdmin={addingAdmin}
        removingAdmin={removingAdmin}
        togglingAdmin={togglingAdmin}
        adminSearchQuery={adminSearchQuery}
        setAdminSearchQuery={setAdminSearchQuery}
        currentAdminPage={currentAdminPage}
        setCurrentAdminPage={setCurrentAdminPage}
        user={user}
        onAddAdmin={handleAddAdmin}
        onRemoveAdmin={handleRemoveAdmin}
        onToggleAdmin={handleToggleTenantAdmin}
        onUpdatePermissions={handleUpdatePermissions}
      />
    </>
  );
}
