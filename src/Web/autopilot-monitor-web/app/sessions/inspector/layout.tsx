"use client";

import { ProtectedRoute } from "../../../components/ProtectedRoute";

export default function InspectorLayout({ children }: { children: React.ReactNode }) {
  return <ProtectedRoute requireGlobalAdmin>{children}</ProtectedRoute>;
}
