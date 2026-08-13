"use client";

import { useRouter } from "next/navigation";
import { useEffect } from "react";
import { ProtectedRoute } from "../../components/ProtectedRoute";
import { useAuth } from "../../contexts/AuthContext";
import { TenantConfigProvider } from "./TenantConfigContext";

export default function SettingsLayout({ children }: { children: React.ReactNode }) {
  const { user } = useAuth();
  const router = useRouter();

  useEffect(() => {
    if (!user) return;

    // Regular users (no tenant role) → redirect to progress portal. Viewers are admitted
    // read-only: the backend serves them redacted config via the MemberRead tier and
    // canEditConfig stays false, so the sections render without mutation affordances.
    if (!user.isTenantAdmin && !user.isGlobalAdmin && user.role !== "Operator" && user.role !== "Viewer") {
      router.replace("/progress");
      return;
    }

    // Operator without bootstrap permission → no settings access
    if (user.role === "Operator" && !user.isTenantAdmin && !user.isGlobalAdmin && !user.canManageBootstrapTokens) {
      router.replace("/dashboard");
    }
  }, [user, router]);

  // Don't render until we know user is allowed
  if (!user) return null;
  if (!user.isTenantAdmin && !user.isGlobalAdmin && user.role !== "Operator" && user.role !== "Viewer") return null;
  if (user.role === "Operator" && !user.isTenantAdmin && !user.isGlobalAdmin && !user.canManageBootstrapTokens) return null;

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
