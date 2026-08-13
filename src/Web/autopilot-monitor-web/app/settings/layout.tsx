"use client";

import { useRouter } from "next/navigation";
import { useEffect } from "react";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import { useAuth } from "../../contexts/AuthContext";
import { hasOwnTenantOrPlatformRole } from "@/lib/tenantScope";
import { TenantConfigProvider } from "./TenantConfigContext";

export default function SettingsLayout({ children }: { children: React.ReactNode }) {
  const { user } = useAuth();
  const router = useRouter();

  // Admission mirrors the sidebar's Configuration visibility (user decision 2026-08-13):
  // any own-tenant role (Admin, Operator, Viewer) or platform scope (GlobalAdmin,
  // read-only GlobalReader) may VIEW settings. Read-only is enforced by the data layer,
  // not by this gate — the backend serves non-admins redacted config via the MemberRead
  // tier, canEditConfig stays false for them, and every write path (PUT + field PATCH)
  // is TenantAdminOrGA server-side. Delegated (MSP) callers without an own-tenant role
  // stay excluded: settings operates on the caller's OWN tenant, which they don't have.
  const admitted = hasOwnTenantOrPlatformRole(user);

  useEffect(() => {
    if (!user) return;
    if (!admitted) {
      router.replace("/progress");
    }
  }, [user, admitted, router]);

  // Don't render until we know user is allowed
  if (!user || !admitted) return null;

  return (
    <ProtectedRoute>
      <TenantConfigProvider>
        <div className="min-h-screen bg-gray-50">
          {children}
        </div>
      </TenantConfigProvider>
    </ProtectedRoute>
  );
}
