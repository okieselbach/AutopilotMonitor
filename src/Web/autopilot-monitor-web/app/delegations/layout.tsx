"use client";

import { ProtectedRoute } from "@/components/ProtectedRoute";

/**
 * Self-service delegation pages (invitation accept). Sign-in only: the accept endpoint itself requires
 * the caller to be a tenant administrator of their own tenant (TenantAdminOrGA), and the page explains a
 * 403 instead of hiding the link behind a client-side role gate.
 */
export default function DelegationsLayout({ children }: { children: React.ReactNode }) {
  return <ProtectedRoute>{children}</ProtectedRoute>;
}
