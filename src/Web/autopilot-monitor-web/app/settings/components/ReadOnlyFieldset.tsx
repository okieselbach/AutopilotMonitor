"use client";

/**
 * Wraps a settings section's interactive content for read-only viewers (Operators).
 * The native fieldset `disabled` attribute inertly disables every nested input, select,
 * textarea and button while values stay visible and copyable; a small notice explains why.
 * Sections must ADDITIONALLY not render their SaveResetBar when read-only — a read-only
 * viewer gets no save affordance at all, not a disabled one.
 */
export default function ReadOnlyFieldset({
  readOnly,
  notice = true,
  children,
}: {
  readOnly: boolean;
  /** Set false on follow-up cards of the same page so the notice appears only once. */
  notice?: boolean;
  children: React.ReactNode;
}) {
  if (!readOnly) return <>{children}</>;
  return (
    <fieldset disabled className="min-w-0">
      {notice && (
        <p className="mb-4 rounded-md bg-gray-50 border border-gray-200 px-3 py-2 text-sm text-gray-500">
          Read-only view — configuration changes require a tenant administrator.
        </p>
      )}
      {children}
    </fieldset>
  );
}
