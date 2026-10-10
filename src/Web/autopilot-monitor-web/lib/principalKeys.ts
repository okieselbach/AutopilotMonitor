/**
 * Principal keys — the value every role list (tenant members, MCP users, delegation assignees) stores a
 * principal under. A person is keyed on the UPN; a service principal (an application calling with an
 * app-only token) on `app:<client-id>`. Mirrors `Constants.PrincipalKeys` in the backend: the prefix is
 * the wire contract, a colon can never appear in a UPN, so the two key spaces cannot collide.
 */
export const APPLICATION_KEY_PREFIX = "app:";

/**
 * What the add-member forms are adding: a person (UPN) or a service principal (an application in the
 * tenant calling with an app-only token, e.g. an automation behind a federated credential). Service
 * principals are read-only — the backend fixes their role to Viewer.
 */
export type MemberKind = "user" | "application";

export function isApplicationKey(principalKey: string | null | undefined): boolean {
  return typeof principalKey === "string" && principalKey.toLowerCase().startsWith(APPLICATION_KEY_PREFIX);
}

/** The application (client) id behind an application key, or null for a person's key. */
export function applicationIdOf(principalKey: string | null | undefined): string | null {
  return isApplicationKey(principalKey) ? principalKey!.slice(APPLICATION_KEY_PREFIX.length) : null;
}

/** Display form: the UPN as is, or "Service principal <client-id>" for an application key. */
export function principalLabel(principalKey: string): string {
  const applicationId = applicationIdOf(principalKey);
  return applicationId ? `Service principal ${applicationId}` : principalKey;
}

/** Loose GUID check for the application (client) id a person types into the add form. */
export function looksLikeGuid(value: string): boolean {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value.trim());
}
