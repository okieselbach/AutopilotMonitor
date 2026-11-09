/**
 * Field writes to a tenant configuration made outside the settings area (the setup guide). The
 * settings provider applies them to its loaded copy, so an open settings page shows the new values
 * without a reload, and a card's own unsaved edits to other fields stay as they are.
 */
type PatchListener = (tenantId: string, fields: Readonly<Record<string, unknown>>) => void;

const listeners = new Set<PatchListener>();

export function subscribeTenantConfigPatches(listener: PatchListener): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

/** Call after the field PATCH succeeded, with exactly the fields it sent. */
export function publishTenantConfigPatch(tenantId: string, fields: Readonly<Record<string, unknown>>): void {
  for (const listener of [...listeners]) listener(tenantId, fields);
}
