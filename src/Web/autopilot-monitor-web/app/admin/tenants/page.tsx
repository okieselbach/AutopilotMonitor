"use client";

import { ClientRedirect } from "@/components/ClientRedirect";

// Index route → default section. Client-side replace: server redirect() is not
// supported under output:'export'.
export default function IndexRedirect() {
  return <ClientRedirect to="/admin/tenants/management" />;
}
