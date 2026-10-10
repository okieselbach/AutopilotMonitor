/**
 * Helpers for detecting the Autopilot-Monitor bootstrap script version
 * from captured Intune Platform Script stdout, and for comparing
 * simple dotted version strings.
 *
 * The bootstrap script writes a deterministic marker line:
 *   "Bootstrap script version: v1.1"
 * (see scripts/Bootstrap/Install-AutopilotMonitor.ps1)
 */

export const BOOTSTRAP_VERSION_RE = /Bootstrap script version:\s*v(\d+(?:\.\d+){1,3})/;

/**
 * Extracts the bootstrap script version from a Platform Script stdout blob.
 * Returns null if the marker line is absent.
 */
export function extractBootstrapVersion(stdout?: string | null): string | null {
  if (!stdout) return null;
  const m = stdout.match(BOOTSTRAP_VERSION_RE);
  return m ? m[1] : null;
}

/**
 * Compares two dotted version strings (e.g. "1.0.706" vs "1.1").
 * Segments are parsed as integers and missing segments are treated as 0,
 * so "1.1" and "1.1.0" compare equal. Non-numeric segments are treated as 0
 * (ignored) — this is intentional, we only care about "newer vs older" for badge purposes.
 *
 * @returns negative if a < b, 0 if equal, positive if a > b.
 */
export function compareVersions(a: string, b: string): number {
  const pa = a.split(".").map(s => parseInt(s, 10) || 0);
  const pb = b.split(".").map(s => parseInt(s, 10) || 0);
  const len = Math.max(pa.length, pb.length);
  for (let i = 0; i < len; i++) {
    const av = pa[i] ?? 0;
    const bv = pb[i] ?? 0;
    if (av !== bv) return av - bv;
  }
  return 0;
}

/**
 * Strips the +{gitHash} suffix from an agent version string (e.g. "1.0.706+abc1234" → "1.0.706").
 * Leaves clean versions untouched.
 */
export function stripGitHashSuffix(version: string): string {
  return version.replace(/\+.*$/, "");
}
