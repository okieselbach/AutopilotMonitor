/**
 * Client-side port of agent guardrail validation logic.
 *
 * All allowlists are loaded from guardrails.generated.ts which is generated
 * from the single source of truth: rules/guardrails.json.
 * Run: node rules/scripts/combine.js
 */

import {
  ALLOWED_REGISTRY_PREFIXES,
  ALLOWED_FILE_PREFIXES,
  ALLOWED_WMI_QUERY_PREFIXES,
  ALLOWED_COMMANDS_LIST,
  ALLOWED_DIAGNOSTICS_PATH_PREFIXES,
  BLOCKED_FILE_PREFIXES,
  ALLOWED_EVENT_LOG_CHANNELS,
  BLOCKED_EVENT_LOG_CHANNELS,
} from "./guardrails.generated";

// Re-export for consumers that imported from here
export { ALLOWED_REGISTRY_PREFIXES, ALLOWED_FILE_PREFIXES, ALLOWED_WMI_QUERY_PREFIXES, ALLOWED_COMMANDS_LIST, ALLOWED_DIAGNOSTICS_PATH_PREFIXES, ALLOWED_EVENT_LOG_CHANNELS };

// ---------------------------------------------------------------------------
// Result type
// ---------------------------------------------------------------------------
export interface ValidationResult {
  allowed: boolean;
  /** Human-readable explanation (shown in tooltip) */
  reason: string;
  /** True if allowed ONLY because unrestricted mode is on */
  unrestricted: boolean;
}

// ---------------------------------------------------------------------------
// Derived sets
// ---------------------------------------------------------------------------

const ALLOWED_COMMANDS_SET = new Set(
  ALLOWED_COMMANDS_LIST.map((c) => c.toLowerCase())
);

const BLOCKED_USERS_PREFIX = BLOCKED_FILE_PREFIXES[0] || "C:\\Users";

// ---------------------------------------------------------------------------
// Common Windows environment variables (for client-side expansion)
// ---------------------------------------------------------------------------
const COMMON_ENV_VARS: Record<string, string> = {
  "%ProgramData%": "C:\\ProgramData",
  "%SystemRoot%": "C:\\Windows",
  "%windir%": "C:\\Windows",
  "%SystemDrive%": "C:",
};

/**
 * Custom agent token that resolves at runtime to the logged-on user's profile.
 * Expanded client-side to a placeholder for validation purposes only.
 */
const USER_PROFILE_TOKEN = "%LOGGED_ON_USER_PROFILE%";
const USER_PROFILE_PLACEHOLDER = "C:\\Users\\__AGENT_RESOLVED__";

/** Allowed subdirectories under a user profile when using the custom token. */
const ALLOWED_USER_PROFILE_SUBDIRS = ["AppData\\Local", "AppData\\Roaming"];

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

/** Escape a literal string so it can be embedded in a RegExp without its
 *  characters being interpreted as regex metacharacters. */
function escapeRegExp(literal: string): string {
  return literal.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
}

/** Expand known Windows environment variables and custom tokens (case-insensitive). */
function expandEnvVars(path: string): string {
  let result = path;
  // Expand custom agent token first
  result = result.replace(new RegExp(escapeRegExp(USER_PROFILE_TOKEN), "gi"), USER_PROFILE_PLACEHOLDER);
  for (const [envVar, replacement] of Object.entries(COMMON_ENV_VARS)) {
    const regex = new RegExp(escapeRegExp(envVar), "gi");
    result = result.replace(regex, replacement);
  }
  return result;
}

/**
 * Simple client-side path normalization.
 * Replaces forward slashes, resolves .. and ., trims trailing backslash.
 */
function normalizePath(path: string): string {
  let p = path.replace(/\//g, "\\");
  // Resolve . and .. segments
  const parts = p.split("\\");
  const resolved: string[] = [];
  for (const part of parts) {
    if (part === "..") {
      if (resolved.length > 1) resolved.pop();
    } else if (part !== "." && part !== "") {
      resolved.push(part);
    } else if (resolved.length === 0 && part === "") {
      // Keep leading empty for UNC paths
    }
  }
  p = resolved.join("\\");
  // Remove trailing backslash (unless it's a root like C:\)
  if (p.length > 3 && p.endsWith("\\")) {
    p = p.slice(0, -1);
  }
  return p;
}

/**
 * Segment-bounded prefix match (case-insensitive).
 * The value must start with prefix and the character at prefix.length
 * must be `separator` or end-of-string.
 */
function matchesPrefix(
  value: string,
  prefix: string,
  separator: string
): boolean {
  if (value.length < prefix.length) return false;
  if (!value.substring(0, prefix.length).toLowerCase().startsWith(prefix.toLowerCase()))
    return false;
  return (
    value.length === prefix.length || value[prefix.length] === separator
  );
}

/**
 * Strip registry hive prefix (HKLM\, HKCU\, or long forms).
 * Returns the subpath after the hive, or the original string if no hive found.
 */
function stripRegistryHive(target: string): string {
  const hivePrefixes = [
    "HKLM\\",
    "HKEY_LOCAL_MACHINE\\",
    "HKCU\\",
    "HKEY_CURRENT_USER\\",
  ];
  const upper = target.toUpperCase();
  for (const prefix of hivePrefixes) {
    if (upper.startsWith(prefix.toUpperCase())) {
      return target.substring(prefix.length);
    }
  }
  return target;
}

/**
 * Returns true if the normalized path is under the user-profile placeholder
 * and within one of the allowed subdirectories (AppData\Local, AppData\Roaming).
 * This mirrors the agent-side IsUserProfileSubpathAllowed guard logic.
 */
function isUserProfileSubpathAllowed(normalizedDir: string): boolean {
  for (const subdir of ALLOWED_USER_PROFILE_SUBDIRS) {
    const allowedPrefix = `${USER_PROFILE_PLACEHOLDER}\\${subdir}`;
    if (matchesPrefix(normalizedDir, allowedPrefix, "\\")) {
      return true;
    }
  }
  return false;
}

// ---------------------------------------------------------------------------
// Validation functions
// ---------------------------------------------------------------------------

export function validateRegistryTarget(
  target: string,
  unrestrictedMode: boolean
): ValidationResult {
  const subPath = stripRegistryHive(target.trim());
  if (!subPath) {
    return { allowed: false, reason: "No registry subpath provided", unrestricted: false };
  }

  if (unrestrictedMode) {
    return { allowed: true, reason: "All registry paths allowed in unrestricted mode", unrestricted: true };
  }

  for (const prefix of ALLOWED_REGISTRY_PREFIXES) {
    if (matchesPrefix(subPath, prefix, "\\")) {
      return { allowed: true, reason: `Matches: ${prefix}`, unrestricted: false };
    }
  }

  return {
    allowed: false,
    reason: "Not under any allowed registry prefix",
    unrestricted: false,
  };
}

export function validateFileTarget(
  target: string,
  unrestrictedMode: boolean
): ValidationResult {
  const trimmed = target.trim();
  if (!trimmed) {
    return { allowed: false, reason: "No file path provided", unrestricted: false };
  }

  const expanded = expandEnvVars(trimmed);

  // Check for unexpanded env vars
  const unexpanded = expanded.match(/%[^%]+%/);
  if (unexpanded) {
    return {
      allowed: false,
      reason: `Contains unknown environment variable ${unexpanded[0]}`,
      unrestricted: false,
    };
  }

  // Handle wildcards in filename: strip filename and normalize directory
  const lastSep = expanded.lastIndexOf("\\");
  const fileName = lastSep >= 0 ? expanded.substring(lastSep + 1) : expanded;
  const hasWildcard = fileName.includes("*") || fileName.includes("?");

  let normalizedDir: string;
  if (hasWildcard) {
    const dir = lastSep >= 0 ? expanded.substring(0, lastSep) : "";
    if (!dir) {
      return { allowed: false, reason: "Wildcard path has no directory", unrestricted: false };
    }
    normalizedDir = normalizePath(dir);
  } else {
    normalizedDir = normalizePath(expanded);
  }

  // Hard block: C:\Users always blocked — except allowed subdirs via %LOGGED_ON_USER_PROFILE% token
  if (matchesPrefix(normalizedDir, BLOCKED_USERS_PREFIX, "\\")) {
    if (isUserProfileSubpathAllowed(normalizedDir)) {
      return { allowed: true, reason: "Allowed via %LOGGED_ON_USER_PROFILE% (AppData only)", unrestricted: false };
    }
    return { allowed: false, reason: "C:\\Users is always blocked (privacy protection)", unrestricted: false };
  }

  if (unrestrictedMode) {
    return { allowed: true, reason: "All file paths allowed in unrestricted mode (except C:\\Users)", unrestricted: true };
  }

  for (const prefix of ALLOWED_FILE_PREFIXES) {
    if (matchesPrefix(normalizedDir, prefix, "\\")) {
      return { allowed: true, reason: `Matches: ${prefix}`, unrestricted: false };
    }
  }

  return {
    allowed: false,
    reason: "Not under any allowed file path prefix",
    unrestricted: false,
  };
}

export function validateWmiTarget(
  target: string,
  unrestrictedMode: boolean
): ValidationResult {
  const trimmed = target.trim();
  if (!trimmed) {
    return { allowed: false, reason: "No WMI query provided", unrestricted: false };
  }

  if (unrestrictedMode) {
    return { allowed: true, reason: "All WMI queries allowed in unrestricted mode", unrestricted: true };
  }

  for (const prefix of ALLOWED_WMI_QUERY_PREFIXES) {
    if (trimmed.length >= prefix.length) {
      const candidate = trimmed.substring(0, prefix.length);
      if (candidate.toLowerCase() === prefix.toLowerCase()) {
        if (trimmed.length === prefix.length || /\s/.test(trimmed[prefix.length])) {
          return { allowed: true, reason: `Matches: ${prefix}`, unrestricted: false };
        }
      }
    }
  }

  return {
    allowed: false,
    reason: "Not a recognized WMI query prefix",
    unrestricted: false,
  };
}

export function validateCommandTarget(
  target: string,
  unrestrictedMode: boolean
): ValidationResult {
  const trimmed = target.trim();
  if (!trimmed) {
    return { allowed: false, reason: "No command provided", unrestricted: false };
  }

  if (unrestrictedMode) {
    return { allowed: true, reason: "All commands allowed in unrestricted mode", unrestricted: true };
  }

  if (ALLOWED_COMMANDS_SET.has(trimmed.toLowerCase())) {
    return { allowed: true, reason: `Exact match on allowlist`, unrestricted: false };
  }

  return {
    allowed: false,
    reason: "Command not on the allowlist (exact match required)",
    unrestricted: false,
  };
}

export function validateDiagnosticsPath(
  rawPath: string,
  unrestrictedMode: boolean
): ValidationResult {
  const trimmed = rawPath.trim().replace(/^["']+|["']+$/g, "");
  if (!trimmed) {
    return { allowed: false, reason: "No path provided", unrestricted: false };
  }

  const expanded = expandEnvVars(trimmed);

  const unexpanded = expanded.match(/%[^%]+%/);
  if (unexpanded) {
    return {
      allowed: false,
      reason: `Contains unknown environment variable ${unexpanded[0]}`,
      unrestricted: false,
    };
  }

  // Handle wildcards in filename
  const lastSep = expanded.lastIndexOf("\\");
  const fileName = lastSep >= 0 ? expanded.substring(lastSep + 1) : expanded;
  const hasWildcard = fileName.includes("*") || fileName.includes("?");

  let normalizedDir: string;
  if (hasWildcard) {
    const dir = lastSep >= 0 ? expanded.substring(0, lastSep) : "";
    if (!dir) {
      return { allowed: false, reason: "Wildcard path has no directory", unrestricted: false };
    }
    normalizedDir = normalizePath(dir);
  } else {
    normalizedDir = normalizePath(expanded);
  }

  // Hard block: C:\Users always blocked — except allowed subdirs via %LOGGED_ON_USER_PROFILE% token
  if (matchesPrefix(normalizedDir, BLOCKED_USERS_PREFIX, "\\")) {
    if (isUserProfileSubpathAllowed(normalizedDir)) {
      return { allowed: true, reason: "Allowed via %LOGGED_ON_USER_PROFILE% (AppData only)", unrestricted: false };
    }
    return { allowed: false, reason: "C:\\Users is always blocked (privacy protection)", unrestricted: false };
  }

  if (unrestrictedMode) {
    return { allowed: true, reason: "All paths allowed in unrestricted mode (except C:\\Users)", unrestricted: true };
  }

  for (const prefix of ALLOWED_DIAGNOSTICS_PATH_PREFIXES) {
    if (matchesPrefix(normalizedDir, prefix, "\\")) {
      return { allowed: true, reason: `Matches: ${prefix}`, unrestricted: false };
    }
  }

  return {
    allowed: false,
    reason: "Not under any allowed diagnostics path prefix",
    unrestricted: false,
  };
}

// ---------------------------------------------------------------------------
// Event log channels
// ---------------------------------------------------------------------------

/**
 * Channel names match with a boundary on "/" so that "Microsoft-Windows-AAD"
 * admits "Microsoft-Windows-AAD/Operational" but not a longer provider name.
 */
function matchesChannel(channel: string, prefix: string): boolean {
  const c = channel.toLowerCase();
  const p = prefix.toLowerCase();
  return c === p || c.startsWith(p + "/");
}

export function validateEventLogTarget(
  target: string,
  unrestrictedMode: boolean
): ValidationResult {
  const channel = target.trim();

  const blocked = BLOCKED_EVENT_LOG_CHANNELS.find((b) => matchesChannel(channel, b));
  if (blocked) {
    return {
      allowed: false,
      reason: `"${blocked}" is never readable — it carries the audit trail or script-block logging`,
      unrestricted: false,
    };
  }

  if (ALLOWED_EVENT_LOG_CHANNELS.some((a) => matchesChannel(channel, a))) {
    return { allowed: true, reason: "Channel is on the allowlist", unrestricted: false };
  }

  if (unrestrictedMode) {
    return {
      allowed: true,
      reason: "Allowed only because unrestricted mode is enabled",
      unrestricted: true,
    };
  }

  return {
    allowed: false,
    reason: "Not an allowed event log channel",
    unrestricted: false,
  };
}

// ---------------------------------------------------------------------------
// Dispatcher — routes to the correct validator by collector type
// ---------------------------------------------------------------------------

/**
 * Validate a gather rule target. Returns null for empty targets or
 * collector types that have no allowlist.
 */
export function validateGatherRuleTarget(
  collectorType: string,
  target: string,
  unrestrictedMode: boolean
): ValidationResult | null {
  if (!target.trim()) return null;

  switch (collectorType) {
    case "registry":
      return validateRegistryTarget(target, unrestrictedMode);
    case "file":
    case "logparser":
    case "json":
    case "xml":
      return validateFileTarget(target, unrestrictedMode);
    case "wmi":
      return validateWmiTarget(target, unrestrictedMode);
    case "command_allowlisted":
    case "command":
      return validateCommandTarget(target, unrestrictedMode);
    case "eventlog":
      return validateEventLogTarget(target, unrestrictedMode);
    default:
      return null;
  }
}
