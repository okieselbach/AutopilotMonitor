/** The push receiver lives under this prefix; the portal chrome stands down there (K24). */
export const PUSH_APP_PREFIX = "/push";

export function isPushAppPath(pathname: string | null): boolean {
  if (!pathname) return false;
  return pathname === PUSH_APP_PREFIX || pathname.startsWith(PUSH_APP_PREFIX + "/");
}
