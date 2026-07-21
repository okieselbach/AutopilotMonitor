import { describe, it, expect } from "vitest";
import {
  validateRegistryTarget,
  validateFileTarget,
  validateWmiTarget,
  validateCommandTarget,
  validateDiagnosticsPath,
  validateGatherRuleTarget,
} from "../guardValidation";

// ---------------------------------------------------------------------------
// Registry validation
// ---------------------------------------------------------------------------

describe("validateRegistryTarget", () => {
  it("allows known HKLM MDM registry path", () => {
    const r = validateRegistryTarget(
      "HKLM\\SOFTWARE\\Microsoft\\Enrollments\\SomeSubkey",
      false,
    );
    expect(r.allowed).toBe(true);
    expect(r.unrestricted).toBe(false);
  });

  it("allows long-form HKEY_LOCAL_MACHINE prefix", () => {
    const r = validateRegistryTarget(
      "HKEY_LOCAL_MACHINE\\SOFTWARE\\Microsoft\\Enrollments",
      false,
    );
    expect(r.allowed).toBe(true);
  });

  it("rejects unknown registry path", () => {
    const r = validateRegistryTarget("HKLM\\SOFTWARE\\EvilCorp\\Secrets", false);
    expect(r.allowed).toBe(false);
  });

  it("allows HKCU with valid subpath (hive is stripped before matching)", () => {
    const r = validateRegistryTarget(
      "HKCU\\SOFTWARE\\Microsoft\\Enrollments",
      false,
    );
    // Guard strips the hive prefix — only subpath is validated
    expect(r.allowed).toBe(true);
  });

  it("rejects HKCU with unknown subpath", () => {
    const r = validateRegistryTarget(
      "HKCU\\SOFTWARE\\EvilCorp\\Secrets",
      false,
    );
    expect(r.allowed).toBe(false);
  });

  it("is case-insensitive", () => {
    const r = validateRegistryTarget(
      "hklm\\software\\microsoft\\enrollments\\test",
      false,
    );
    expect(r.allowed).toBe(true);
  });

  it("allows any path in unrestricted mode", () => {
    const r = validateRegistryTarget("HKLM\\ANYTHING\\HERE", true);
    expect(r.allowed).toBe(true);
    expect(r.unrestricted).toBe(true);
  });

  it("rejects empty subpath", () => {
    const r = validateRegistryTarget("HKLM\\", false);
    expect(r.allowed).toBe(false);
  });
});

// ---------------------------------------------------------------------------
// File validation
// ---------------------------------------------------------------------------

describe("validateFileTarget", () => {
  it("allows known ProgramData path", () => {
    const r = validateFileTarget(
      "C:\\ProgramData\\Microsoft\\IntuneManagementExtension\\Logs\\IntuneManagementExtension.log",
      false,
    );
    expect(r.allowed).toBe(true);
  });

  it("allows path with environment variable", () => {
    const r = validateFileTarget(
      "%ProgramData%\\Microsoft\\IntuneManagementExtension\\Logs",
      false,
    );
    expect(r.allowed).toBe(true);
  });

  it("blocks C:\\Users always (even unrestricted mode)", () => {
    const r = validateFileTarget("C:\\Users\\admin\\Documents\\secret.txt", true);
    expect(r.allowed).toBe(false);
    expect(r.reason).toContain("C:\\Users");
  });

  it("rejects path traversal attempts", () => {
    // Attempt to escape allowed prefix via ..
    const r = validateFileTarget(
      "C:\\ProgramData\\Microsoft\\..\\..\\Users\\admin\\file.txt",
      false,
    );
    expect(r.allowed).toBe(false);
  });

  it("rejects unknown environment variables", () => {
    const r = validateFileTarget("%UNKNOWN_VAR%\\something", false);
    expect(r.allowed).toBe(false);
    expect(r.reason).toContain("unknown environment variable");
  });

  it("allows wildcard in filename under valid prefix", () => {
    const r = validateFileTarget(
      "C:\\ProgramData\\Microsoft\\IntuneManagementExtension\\Logs\\*.log",
      false,
    );
    expect(r.allowed).toBe(true);
  });

  it("rejects wildcard path with no directory", () => {
    const r = validateFileTarget("*.log", false);
    expect(r.allowed).toBe(false);
  });

  it("rejects path not under any allowed prefix", () => {
    const r = validateFileTarget("D:\\RandomFolder\\file.txt", false);
    expect(r.allowed).toBe(false);
  });

  it("allows any path (except C:\\Users) in unrestricted mode", () => {
    const r = validateFileTarget("D:\\CustomLogs\\app.log", true);
    expect(r.allowed).toBe(true);
    expect(r.unrestricted).toBe(true);
  });

  it("rejects empty path", () => {
    const r = validateFileTarget("", false);
    expect(r.allowed).toBe(false);
  });

  it("allows %LOGGED_ON_USER_PROFILE% with AppData\\Local in file target", () => {
    const r = validateFileTarget(
      "%LOGGED_ON_USER_PROFILE%\\AppData\\Local\\SomeApp\\data.json",
      false,
    );
    expect(r.allowed).toBe(true);
  });

  it("blocks %LOGGED_ON_USER_PROFILE% with Desktop in file target", () => {
    const r = validateFileTarget(
      "%LOGGED_ON_USER_PROFILE%\\Desktop\\file.txt",
      false,
    );
    expect(r.allowed).toBe(false);
  });
});

// ---------------------------------------------------------------------------
// WMI validation
// ---------------------------------------------------------------------------

describe("validateWmiTarget", () => {
  it("allows known WMI query prefix", () => {
    const r = validateWmiTarget(
      "SELECT * FROM Win32_OperatingSystem",
      false,
    );
    expect(r.allowed).toBe(true);
  });

  it("allows WMI query with WHERE clause after prefix", () => {
    const r = validateWmiTarget(
      "SELECT * FROM Win32_OperatingSystem WHERE Version > '10'",
      false,
    );
    expect(r.allowed).toBe(true);
  });

  it("rejects unknown WMI query", () => {
    const r = validateWmiTarget("SELECT * FROM Win32_Process", false);
    expect(r.allowed).toBe(false);
  });

  it("allows any WMI in unrestricted mode", () => {
    const r = validateWmiTarget("SELECT * FROM Win32_Anything", true);
    expect(r.allowed).toBe(true);
    expect(r.unrestricted).toBe(true);
  });

  it("rejects empty query", () => {
    const r = validateWmiTarget("", false);
    expect(r.allowed).toBe(false);
  });
});

// ---------------------------------------------------------------------------
// Command validation
// ---------------------------------------------------------------------------

describe("validateCommandTarget", () => {
  it("allows known command from allowlist", () => {
    const r = validateCommandTarget("Get-Tpm", false);
    expect(r.allowed).toBe(true);
  });

  it("is case-insensitive for commands", () => {
    const r = validateCommandTarget("get-tpm", false);
    expect(r.allowed).toBe(true);
  });

  it("rejects unknown command", () => {
    const r = validateCommandTarget("Remove-Item C:\\Windows", false);
    expect(r.allowed).toBe(false);
  });

  it("allows any command in unrestricted mode", () => {
    const r = validateCommandTarget("Get-Process", true);
    expect(r.allowed).toBe(true);
    expect(r.unrestricted).toBe(true);
  });

  it("rejects empty command", () => {
    const r = validateCommandTarget("", false);
    expect(r.allowed).toBe(false);
  });
});

// ---------------------------------------------------------------------------
// Diagnostics path validation
// ---------------------------------------------------------------------------

describe("validateDiagnosticsPath", () => {
  it("allows AutopilotMonitor ProgramData path", () => {
    const r = validateDiagnosticsPath(
      "C:\\ProgramData\\AutopilotMonitor\\logs\\agent.log",
      false,
    );
    expect(r.allowed).toBe(true);
  });

  it("strips surrounding quotes", () => {
    const r = validateDiagnosticsPath(
      '"C:\\ProgramData\\AutopilotMonitor\\data"',
      false,
    );
    expect(r.allowed).toBe(true);
  });

  it("blocks C:\\Users always", () => {
    const r = validateDiagnosticsPath("C:\\Users\\admin\\Desktop\\file.txt", true);
    expect(r.allowed).toBe(false);
  });

  it("rejects path traversal in diagnostics", () => {
    const r = validateDiagnosticsPath(
      "C:\\ProgramData\\AutopilotMonitor\\..\\..\\Users\\admin",
      false,
    );
    expect(r.allowed).toBe(false);
  });

  it("rejects unknown path", () => {
    const r = validateDiagnosticsPath("E:\\SomeFolder\\file.log", false);
    expect(r.allowed).toBe(false);
  });

  it("allows any path (except C:\\Users) in unrestricted mode", () => {
    const r = validateDiagnosticsPath("E:\\CustomLogs\\file.log", true);
    expect(r.allowed).toBe(true);
    expect(r.unrestricted).toBe(true);
  });

  it("allows %LOGGED_ON_USER_PROFILE% with AppData\\Local subpath", () => {
    const r = validateDiagnosticsPath(
      "%LOGGED_ON_USER_PROFILE%\\AppData\\Local\\RealmJoin\\Logs\\*.log",
      false,
    );
    expect(r.allowed).toBe(true);
    expect(r.reason).toContain("%LOGGED_ON_USER_PROFILE%");
  });

  it("allows %LOGGED_ON_USER_PROFILE% with AppData\\Roaming subpath", () => {
    const r = validateDiagnosticsPath(
      "%LOGGED_ON_USER_PROFILE%\\AppData\\Roaming\\SomeApp\\logs\\*.log",
      false,
    );
    expect(r.allowed).toBe(true);
  });

  it("blocks %LOGGED_ON_USER_PROFILE% with non-AppData subpath", () => {
    const r = validateDiagnosticsPath(
      "%LOGGED_ON_USER_PROFILE%\\Desktop\\secret.txt",
      false,
    );
    expect(r.allowed).toBe(false);
  });

  it("blocks %LOGGED_ON_USER_PROFILE% with Documents subpath", () => {
    const r = validateDiagnosticsPath(
      "%LOGGED_ON_USER_PROFILE%\\Documents\\file.log",
      false,
    );
    expect(r.allowed).toBe(false);
  });

  it("still blocks direct C:\\Users paths (no token)", () => {
    const r = validateDiagnosticsPath(
      "C:\\Users\\admin\\AppData\\Local\\RealmJoin\\Logs\\*.log",
      false,
    );
    expect(r.allowed).toBe(false);
  });
});

// ---------------------------------------------------------------------------
// Dispatcher (validateGatherRuleTarget)
// ---------------------------------------------------------------------------

describe("validateGatherRuleTarget", () => {
  it("routes registry type to validateRegistryTarget", () => {
    const r = validateGatherRuleTarget(
      "registry",
      "HKLM\\SOFTWARE\\Microsoft\\Enrollments",
      false,
    );
    expect(r).not.toBeNull();
    expect(r!.allowed).toBe(true);
  });

  it("routes file type to validateFileTarget", () => {
    const r = validateGatherRuleTarget("file", "C:\\Users\\test", false);
    expect(r).not.toBeNull();
    expect(r!.allowed).toBe(false); // C:\Users blocked
  });

  it("routes logparser/json/xml to validateFileTarget", () => {
    for (const type of ["logparser", "json", "xml"]) {
      const r = validateGatherRuleTarget(type, "D:\\invalid", false);
      expect(r).not.toBeNull();
      expect(r!.allowed).toBe(false);
    }
  });

  it("routes wmi type to validateWmiTarget", () => {
    const r = validateGatherRuleTarget(
      "wmi",
      "SELECT * FROM Win32_OperatingSystem",
      false,
    );
    expect(r).not.toBeNull();
    expect(r!.allowed).toBe(true);
  });

  it("routes command types to validateCommandTarget", () => {
    const r = validateGatherRuleTarget("command_allowlisted", "Get-Tpm", false);
    expect(r).not.toBeNull();
    expect(r!.allowed).toBe(true);
  });

  it("routes eventlog type to validateEventLogTarget", () => {
    const allowed = validateGatherRuleTarget("eventlog", "Application", false);
    expect(allowed).not.toBeNull();
    expect(allowed!.allowed).toBe(true);

    const blocked = validateGatherRuleTarget("eventlog", "Security", false);
    expect(blocked).not.toBeNull();
    expect(blocked!.allowed).toBe(false);
  });

  it("returns null for empty target", () => {
    const r = validateGatherRuleTarget("registry", "", false);
    expect(r).toBeNull();
  });

  it("returns null for unknown collector type", () => {
    const r = validateGatherRuleTarget("unknown_type", "some target", false);
    expect(r).toBeNull();
  });
});
