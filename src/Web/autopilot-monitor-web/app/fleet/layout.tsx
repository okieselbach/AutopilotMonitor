"use client";

import { ProtectedRoute } from "@/components/ProtectedRoute";

/**
 * The Fleet (MSP) area. Gated on fleet scope: a delegated ("MSP") admin who manages a subset of tenants,
 * OR full platform scope (GA/Reader). The backend enforces the per-tenant bound on every request; this
 * gate only keeps a single-tenant user with no fleet off the page.
 */
export default function FleetLayout({ children }: { children: React.ReactNode }) {
  return <ProtectedRoute requireFleetScope>{children}</ProtectedRoute>;
}
