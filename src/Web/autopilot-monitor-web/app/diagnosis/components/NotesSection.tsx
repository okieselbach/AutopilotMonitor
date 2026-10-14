"use client";

import { useSessionAnnotations } from "@/app/sessions/hooks/useSessionAnnotations";
import SessionAnnotationsBody from "@/app/sessions/components/SessionAnnotationsBody";
import type { AnnotationUser } from "@/app/sessions/components/sessionAnnotationLogic";

/** The team's verdict and notes on this session — the same lanes as the session page's annotations. */
export default function NotesSection({
  sessionId,
  tenantId,
  user,
  getAccessToken,
}: {
  sessionId: string;
  tenantId?: string;
  user: AnnotationUser | null | undefined;
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>;
}) {
  const state = useSessionAnnotations({ sessionId, effectiveTenantId: tenantId, user, getAccessToken });
  return (
    <section className="rounded-lg bg-white p-5 shadow sm:p-6" aria-labelledby="notes-title">
      <h2 id="notes-title" className="text-lg font-semibold text-gray-900">
        Notes from your team
      </h2>
      <p className="mb-4 mt-1 text-sm text-gray-500">Confirmed root causes and corrections feed rule quality.</p>
      <SessionAnnotationsBody state={state} />
    </section>
  );
}
