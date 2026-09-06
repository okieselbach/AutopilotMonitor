#!/usr/bin/env node
/**
 * Error-code catalog generator (schemaVersion 2).
 *
 * Rebuilds src/Shared/AutopilotMonitor.Shared/Resources/error-codes.json from primary
 * sources and writes the web copy through the web sync script. Every entry carries the
 * source it was taken from; nothing is typed in from memory.
 *
 *   node scripts/error-codes/generate.mjs [--cache-dir DIR] [--check]
 *
 *   IME_ENUMS_JSON   required — path to the enum extract of one Intune Management Extension
 *                    build (customErrorCodes, enforcementStates, msiRetriableErrorCodes,
 *                    deliveryOptimization). Produced by operator tooling; the generator has
 *                    no notion of where the enums come from.
 *   --cache-dir DIR  read/write the fetched MS Learn pages as files in DIR (offline reruns).
 *   --check          do not write; exit 1 when the committed catalog differs from the output.
 *
 * Families, in precedence order (the first family that yields a key wins; later duplicates
 * are reported and dropped):
 *   win32                 MS "System Error Codes" + WinHTTP error page, keyed 0x8007xxxx
 *   msi                   MS "Windows Installer error codes", decimal keys 0 / 1601-1654 / 3010
 *   com                   winerror.h HRESULTs that are not Win32-derived (S_OK, E_FAIL, ...)
 *   appx                  MS "Troubleshooting packaging, deployment, and query of Windows apps"
 *   windows-update        MS "Windows Update error codes by component" (reference tables)
 *   cbs / windows-update  MS "Windows Update common errors and mitigation" (per-code tables)
 *   intune-*              MS "Intune app installation error codes" (iOS + Windows tables)
 *   mdm-enrollment/       MS enrollment troubleshooting + Autopilot known-issues pages (prose:
 *   autopilot/entra-join  one-sentence paraphrase, presence-checked against the page)
 *   intune-win32          IME CustomErrorCodes enum (0x87D3xxxx), member name paraphrased
 *   delivery-optimization IME DeliveryOptimizationConstants (0x80D0xxxx), member name paraphrased
 *   legacy                v1 entries without a primary source, kept at confidence "low"
 *
 * See README.md next to this file for the source list and the description rules.
 */
import { readFileSync, writeFileSync, existsSync, mkdirSync } from "node:fs";
import { execFileSync } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(HERE, "..", "..");
const TARGET = path.join(REPO_ROOT, "src", "Shared", "AutopilotMonitor.Shared", "Resources", "error-codes.json");
const WEB_SYNC = path.join(REPO_ROOT, "src", "Web", "autopilot-monitor-web", "scripts", "sync-error-codes.js");

// ── CLI ───────────────────────────────────────────────────────────────────────

const args = process.argv.slice(2);
const cacheDir = args.includes("--cache-dir") ? args[args.indexOf("--cache-dir") + 1] : undefined;
const checkOnly = args.includes("--check");
const imeEnumsPath = process.env.IME_ENUMS_JSON;
if (!imeEnumsPath || !existsSync(imeEnumsPath)) {
  console.error("IME_ENUMS_JSON must point to the IME enum extract (see README.md).");
  process.exit(2);
}
const ime = JSON.parse(readFileSync(imeEnumsPath, "utf8"));
if (!/^\d+(\.\d+)+$/.test(String(ime.imeVersion ?? ""))) {
  console.error("IME enum extract has no imeVersion.");
  process.exit(2);
}
const IME_SOURCE = `ime:${ime.imeVersion}`;

// ── Vocabulary (mirrored by the C#, web and MCP tests) ───────────────────────

export const CATEGORIES = [
  "msi", "win32", "com", "appx", "windows-update", "cbs", "intune-win32", "intune-mobile",
  "mdm-enrollment", "autopilot", "entra-join", "delivery-optimization",
];
export const CONFIDENCES = ["high", "medium", "low"];
export const SOURCE_PATTERN = /^(msdoc:https:\/\/learn\.microsoft\.com\/[^\s]+|ime:\d+(\.\d+)+|winerror\.h|rule:[A-Z]+-[A-Z]+-\d{3}|legacy-catalog-v1)$/;
export const KEY_PATTERN = /^(0x[0-9a-f]{8}|\d+)$/;
// winerror-style (ERROR_INSTALL_FAILURE) or an enum member (UnzipError); never a phrase.
const SYMBOL_PATTERN = /^[A-Za-z][A-Za-z0-9_]{2,}$/;
const MAX_DESCRIPTION = 200;

// ── Sources ──────────────────────────────────────────────────────────────────

const URL = {
  msi: "https://learn.microsoft.com/en-us/windows/win32/msi/error-codes",
  appx: "https://learn.microsoft.com/en-us/windows/win32/appxpkg/troubleshooting",
  wuReference: "https://learn.microsoft.com/en-us/windows/deployment/update/windows-update-error-reference",
  wuErrors: "https://learn.microsoft.com/en-us/windows/deployment/update/windows-update-errors",
  intuneApps: "https://learn.microsoft.com/en-us/troubleshoot/mem/intune/app-management/app-install-error-codes",
  enrollErrors: "https://learn.microsoft.com/en-us/troubleshoot/mem/intune/device-enrollment/troubleshoot-windows-enrollment-errors",
  knownIssues: "https://learn.microsoft.com/en-us/autopilot/known-issues",
  winhttp: "https://learn.microsoft.com/en-us/windows/win32/winhttp/error-messages",
  win32: (range) => `https://learn.microsoft.com/en-us/windows/win32/debug/system-error-codes--${range}-`,
};
const WIN32_RANGES = [[0, 499], [500, 999], [1000, 1299], [1300, 1699], [1700, 3999], [4000, 5999], [6000, 8199], [8200, 8999], [9000, 11999], [12000, 15999]];
const WINHTTP_RANGE = [12000, 12200];

const msdoc = (url) => `msdoc:${url}`;

const pageCache = new Map();
async function fetchPage(url) {
  if (pageCache.has(url)) return pageCache.get(url);
  const cacheFile = cacheDir ? path.join(cacheDir, url.replace(/^https?:\/\//, "").replace(/[^a-z0-9.-]+/gi, "_") + ".html") : null;
  let html;
  if (cacheFile && existsSync(cacheFile)) {
    html = readFileSync(cacheFile, "utf8");
  } else {
    const res = await fetch(url, { headers: { "user-agent": "Mozilla/5.0 (error-code catalog generator)" } });
    if (!res.ok) throw new Error(`GET ${url} → ${res.status}`);
    html = await res.text();
    if (cacheFile) {
      mkdirSync(path.dirname(cacheFile), { recursive: true });
      writeFileSync(cacheFile, html, "utf8");
    }
  }
  pageCache.set(url, html);
  return html;
}

// ── HTML helpers ─────────────────────────────────────────────────────────────

const decode = (s) => s
  .replace(/<br\s*\/?>/gi, " ")
  .replace(/<\/(p|li|div)>/gi, " ")
  .replace(/<[^>]+>/g, "")
  .replace(/&nbsp;/g, " ").replace(/&amp;/g, "&").replace(/&lt;/g, "<").replace(/&gt;/g, ">")
  .replace(/&quot;/g, '"').replace(/&#39;/g, "'").replace(/&#x27;/g, "'")
  .replace(/\s+/g, " ").trim();

/** Every <table> of a page as rows of decoded cell strings, in document order. */
function parseTables(html) {
  return [...html.matchAll(/<table[\s\S]*?<\/table>/gi)].map((t) =>
    [...t[0].matchAll(/<tr[\s\S]*?<\/tr>/gi)]
      .map((r) => [...r[0].matchAll(/<t[hd][^>]*>([\s\S]*?)<\/t[hd]>/gi)].map((c) => decode(c[1])))
      .filter((cells) => cells.length > 0));
}

/** h2/h3 headings and the table that follows each, in order (the "one table per code" pages). */
function parseHeadedTables(html) {
  const out = [];
  const seq = [...html.matchAll(/<h([23])[^>]*>([\s\S]*?)<\/h\1>|<table[\s\S]*?<\/table>/gi)];
  let heading = null;
  for (const m of seq) {
    if (m[0].startsWith("<h")) { heading = decode(m[2]); continue; }
    if (!heading) continue;
    const rows = [...m[0].matchAll(/<tr[\s\S]*?<\/tr>/gi)]
      .map((r) => [...r[0].matchAll(/<t[hd][^>]*>([\s\S]*?)<\/t[hd]>/gi)].map((c) => decode(c[1])));
    out.push({ heading, rows });
    heading = null;
  }
  return out;
}

/**
 * winerror.h-style reference pages: `<strong>SYMBOL</strong>` followed by a paragraph with the
 * decimal value (optionally "(0x…)") and a paragraph with the text. Shape shared by the
 * System Error Codes pages and the WinHTTP error page.
 */
function parseSymbolPage(html) {
  const map = new Map();
  const re = /<strong>([A-Z][A-Z0-9_]+)<\/strong><\/p>(?:\s|<\/?(?:dt|dd|dl)>)*<p>(\d+)(?:\s*\(0x[0-9A-Fa-f]+\))?<\/p>(?:\s|<\/?(?:dt|dd|dl)>)*<p>([\s\S]*?)<\/p>/g;
  for (const m of html.matchAll(re)) {
    const value = Number(m[2]);
    if (!map.has(value)) map.set(value, { symbol: m[1], text: decode(m[3]) });
  }
  return map;
}

// ── Description shaping ──────────────────────────────────────────────────────

/**
 * One short line per code: whole sentences from the source text up to MAX_DESCRIPTION
 * characters (at least one sentence; a single over-long sentence is cut with an ellipsis).
 * Cross-reference sentences ("For more information, see …", "See …") are dropped. Pages whose
 * cell continues with troubleshooting prose pass `firstSentence` and keep only the first one.
 */
export function shapeDescription(text, { firstSentence = false } = {}) {
  const clean = String(text).replace(/\s+/g, " ").trim();
  const sentences = clean.split(/(?<=[.!?])\s+(?=[A-Z0-9("'])/)
    .map((s) => s.trim())
    .filter((s) => s && !/^(For (more )?information|See |Refer to)/i.test(s));
  const kept = [];
  let length = 0;
  for (const s of sentences) {
    if (kept.length > 0 && (firstSentence || length + 1 + s.length > MAX_DESCRIPTION)) break;
    kept.push(s);
    length += (kept.length > 1 ? 1 : 0) + s.length;
  }
  let result = kept.join(" ") || clean;
  if (result.length > MAX_DESCRIPTION + 20) result = result.slice(0, MAX_DESCRIPTION).trimEnd() + "…";
  result = result.replace(/\.$/, "");
  return result.charAt(0).toUpperCase() + result.slice(1);
}

/** "InvalidDetectionRuleUnknownDetectionType" → "Invalid detection rule: unknown detection type". */
export function humanizeMember(name) {
  const PREFIXES = ["InvalidDetectionRule", "InvalidRequirementRule", "AppLogUpload", "NotAttempted", "InProgress", "SuccessBut", "Success", "Error", "ResumeJobFailure"];
  const ACRONYMS = { CDN: "CDN", DO: "DO", SAS: "SAS", MSI: "MSI", VPP: "VPP", IOS: "iOS", TPM: "TPM", MDM: "MDM", HTTP: "HTTP", URL: "URL", COM: "COM", APK: "APK", CP: "CP", ID: "ID" };
  const words = (s) => s
    .replace(/([a-z0-9])([A-Z])/g, "$1 $2")
    .replace(/([A-Z]+)([A-Z][a-z])/g, "$1 $2")
    .split(" ")
    .map((w) => ACRONYMS[w.toUpperCase()] ?? w.toLowerCase());
  const prefix = PREFIXES.find((p) => name.startsWith(p) && name.length > p.length);
  if (prefix) {
    const head = words(prefix);
    const tail = words(name.slice(prefix.length));
    return capitalize(head.join(" ")) + ": " + tail.join(" ");
  }
  return capitalize(words(name).join(" "));
}
const capitalize = (s) => s.charAt(0).toUpperCase() + s.slice(1);

// ── Key helpers ──────────────────────────────────────────────────────────────

const hexKey = (n) => "0x" + ((n >>> 0).toString(16)).padStart(8, "0");
const fromSigned = (n) => hexKey(n < 0 ? n + 0x100000000 : n);
const normalizeHex = (raw) => hexKey(parseInt(raw.replace(/^0x/i, ""), 16));
const isFacility7 = (key) => key.startsWith("0x8007");
const MSI_DECIMAL = (n) => n === 0 || (n >= 1601 && n <= 1654) || n === 3010;

// ── Families ─────────────────────────────────────────────────────────────────

/** Win32 codes that the field has shown as 0x8007xxxx HRESULTs (beyond what other families name). */
const WIN32_FIELD_SEEN = [
  1, 2, 3, 4, 5, 6, 8, 13, 14, 23, 32, 50, 53, 64, 67, 87, 112, 120, 122, 258, 314, 995, 1053, 1058, 1067,
  1114, 1168, 1203, 1219, 1220, 1222, 1223, 1231, 1232, 1235, 1259, 1311, 1312, 1326, 1327, 1355, 1385,
  1392, 1450, 1460, 1717, 1722, 1726, 1780, 1784, 1789, 1908, 1909, 3017, 8239,
  12002, 12007, 12029, 12030, 12152, 12175, 14081, 14107,
];

/** HRESULTs that are not Win32-derived; text as in winerror.h. */
const COM_ENTRIES = [
  ["0x00000000", "S_OK", "Success"],
  ["0x80004001", "E_NOTIMPL", "Not implemented"],
  ["0x80004002", "E_NOINTERFACE", "No such interface supported"],
  ["0x80004003", "E_POINTER", "Invalid pointer"],
  ["0x80004004", "E_ABORT", "Operation aborted"],
  ["0x80004005", "E_FAIL", "Unspecified error"],
  ["0x8000ffff", "E_UNEXPECTED", "Catastrophic failure"],
  ["0x80010108", "RPC_E_DISCONNECTED", "The object invoked has disconnected from its clients"],
  ["0x80020005", "DISP_E_TYPEMISMATCH", "Type mismatch"],
  ["0x80190193", "HTTP_E_STATUS_FORBIDDEN", "Forbidden (403)"],
  ["0x80190194", "HTTP_E_STATUS_NOT_FOUND", "Not found (404)"],
];

/**
 * Codes documented on prose pages (no table to parse). The generator asserts that the code
 * appears on the named page; the description is a one-sentence paraphrase of that page.
 */
const PROSE_ENTRIES = [
  { key: "0x80180014", category: "mdm-enrollment", page: URL.enrollErrors,
    description: "Your organization does not support this version of Windows: MDM enrollment is disabled or restricted in the tenant, or an Autopilot device record still exists on redeploy" },
  { key: "0x80180022", category: "autopilot", page: URL.enrollErrors,
    description: "Autopilot device enrollment failed: the device runs Windows Home edition (Pro or higher is required)" },
  { key: "0x8018002b", category: "mdm-enrollment", page: URL.enrollErrors,
    description: "Automatic MDM enrollment failed: the user's UPN has an unverified or non-routable domain, or the MDM user scope is set to None" },
  { key: "0x801c03ea", category: "entra-join", page: URL.enrollErrors,
    description: "Registering the device for mobile management failed: the TPM supports 2.0 but has not been upgraded to 2.0, or the device is in two groups with different Autopilot profiles" },
  { key: "0x801c03f3", category: "entra-join", page: URL.knownIssues,
    description: "Microsoft Entra ID has no device object for the device being deployed (the object was deleted); remove the device from Entra ID, Intune and Autopilot and re-register it" },
  { key: "0x81039001", category: "autopilot", page: URL.knownIssues, symbol: "E_AUTOPILOT_CLIENT_TPM_MAX_ATTESTATION_RETRY_EXCEEDED",
    description: "TPM attestation failed intermittently during self-deploying or pre-provisioning (Securing your hardware); a subsequent provisioning attempt often succeeds" },
  { key: "0x81039023", category: "autopilot", page: URL.knownIssues,
    description: "TPM attestation failed on Windows 11 during self-deploying or pre-provisioning; fixed by the May 2022 cumulative update (KB5013943) or later" },
  { key: "0x81039024", category: "autopilot", page: URL.knownIssues,
    description: "TPM attestation failed because known vulnerabilities were detected in the TPM; update the TPM firmware from the PC manufacturer" },
  { key: "0xc1036501", category: "mdm-enrollment", page: URL.knownIssues,
    description: "The device cannot do an automatic MDM enrollment because there are multiple MDM configurations in Microsoft Entra ID" },
  // Named only in our own analysis rule, no MS page found: confidence medium.
  { key: "0x80180018", category: "mdm-enrollment", source: "rule:ANALYZE-ENRL-004", confidence: "medium",
    description: "MDM enrollment refused for the user: missing Intune, EMS or Microsoft 365 license, or the device enrollment limit was exceeded" },
];

/** v1 entries that no primary source covers — kept, marked low. */
const LEGACY_ENTRIES = [
  ["0x87d00213", "intune-win32", "App not applicable"],
  ["0x87d00215", "intune-win32", "App dependency failed"],
  ["0x87d00216", "intune-win32", "App supersedence conflict"],
  ["0x87d00324", "intune-win32", "App installation failed"],
  ["0x87d00607", "intune-win32", "App download failed"],
  ["0x87d00651", "intune-win32", "Detection rules not met after install"],
  ["0x87d13b88", "intune-mobile", "License assignment failed with token expired (VPP)"],
  ["0x87d1fde8", "intune-win32", "Remediation failed"],
  ["0x800f0991", "cbs", "A payload file is missing from the component store", "PSFX_E_MISSING_PAYLOAD_FILE"],
];

/**
 * Wording pins: consumers (tests, rule explanations) depend on these exact phrases. Each pin
 * says the same thing as the source row it replaces; the confidence stays with the source.
 */
const DESCRIPTION_PINS = {
  "0x87d1041c": { description: "Application not detected after installation completed successfully" },
  // The MS row describes one dated incident ("The July cumulative update failed…"); the code itself is generic.
  "0x800f0922": { description: "Windows servicing: a package installer failed during update installation", confidence: "medium" },
};

/**
 * Known defects in the IME enum source (sign / digit typos). The fixup applies the intended
 * value and FAILS when the source no longer shows the defect, so a fixed build drops the fixup.
 */
const IME_ENUM_FIXUPS = {
  InvalidRequirementRuleUnknownRequirementType: { seen: 2016215026, intended: -2016215026 },
  AppLogUploadSasUrlExpired: { seen: -216214830, intended: -2016214830 },
};

// ── Build ────────────────────────────────────────────────────────────────────

const entries = new Map();          // key → entry
const dropped = [];                 // { key, family, reason }
function add(family, key, entry) {
  if (!KEY_PATTERN.test(key)) throw new Error(`${family}: bad key ${key}`);
  if (entries.has(key)) { dropped.push({ key, family, reason: `already defined by ${entries.get(key)._family}` }); return; }
  const description = shapeDescription(entry.description, { firstSentence: entry.firstSentence });
  if (!description) throw new Error(`${family}: empty description for ${key}`);
  const out = { description, confidence: entry.confidence, source: entry.source, category: entry.category };
  // Symbol cells may be wrapped with <br> ("ERROR_INSTALL_OPEN_ PACKAGE_FAILED"); the token has no spaces.
  const symbol = entry.symbol ? String(entry.symbol).replace(/\s+/g, "") : "";
  if (symbol && SYMBOL_PATTERN.test(symbol)) out.symbol = symbol;
  if (entry.imeRetriesDuringEsp) out.imeRetriesDuringEsp = true;
  const pin = DESCRIPTION_PINS[key];
  if (pin) { out.description = pin.description; if (pin.confidence) out.confidence = pin.confidence; }
  Object.defineProperty(out, "_family", { value: family, enumerable: false });
  entries.set(key, out);
}

async function main() {
  const retriable = new Set(ime.msiRetriableErrorCodes.map(Number));

  // Families other than Win32 are collected first so every 0x8007xxxx code they name can be
  // resolved against the Win32 reference, which then takes precedence.
  const pending = [];
  const queue = (family, key, entry) => pending.push({ family, key, entry });

  // msi — decimal keys, MSI range only (other rows are plain Win32 codes and live as 0x8007xxxx).
  {
    const [table] = parseTables(await fetchPage(URL.msi));
    for (const [symbol, value, description] of table.slice(1)) {
      const n = Number(value);
      if (!Number.isInteger(n) || !MSI_DECIMAL(n)) continue;
      queue("msi", String(n), { symbol, description, confidence: "high", source: msdoc(URL.msi), category: "msi", imeRetriesDuringEsp: retriable.has(n) });
    }
  }

  // com
  for (const [key, symbol, description] of COM_ENTRIES) queue("com", key, { symbol, description, confidence: "high", source: "winerror.h", category: "com" });

  // appx
  {
    const [table] = parseTables(await fetchPage(URL.appx));
    for (const [symbol, value, description] of table.slice(1)) {
      if (!/^0x[0-9a-f]{8}$/i.test(value)) continue;
      queue("appx", normalizeHex(value), { symbol, description, firstSentence: true, confidence: "high", source: msdoc(URL.appx), category: "appx" });
    }
  }

  // windows-update reference (Error code | Message | Description), the components an
  // enrollment can meet: agent core, handlers, download manager, protocol talker (HTTP),
  // data store, drivers, MSI handler, agent setup, AU client. Inventory / expression
  // evaluator / reporter / redirector are WSUS-internal and left out.
  const WU_SECTIONS = /^(Automatic Update Errors|Windows Update UI errors|Protocol Talker errors|Other Protocol Talker errors|Download Manager errors|Update Handler errors|Data Store errors|Driver Util errors|Windows Update error codes|Windows Update success codes|Windows Installer minor errors|Windows Update Agent update and setup errors)$/;
  for (const { heading, rows: table } of parseHeadedTables(await fetchPage(URL.wuReference))) {
    if (!WU_SECTIONS.test(heading)) continue;
    for (const [value, message, description] of table.slice(1)) {
      if (!/^0x[0-9a-f]{8}$/i.test(value)) continue;
      const key = normalizeHex(value);
      queue("windows-update", key, { symbol: message, description, confidence: "high", source: msdoc(URL.wuReference), category: categoryForWuKey(key) });
    }
  }

  // windows-update common errors (one table per heading; Message | Description | Mitigation)
  for (const { heading, rows } of parseHeadedTables(await fetchPage(URL.wuErrors))) {
    // A heading that lumps several codes into one table ("0x80072EFD or 0x80072EFE or 0x80D02002")
    // describes a symptom, not the codes; only single-code sections are taken.
    const codes = [...heading.matchAll(/0x[0-9a-f]{8}/gi)].map((m) => normalizeHex(m[0]));
    if (codes.length !== 1 || rows.length < 2) continue;
    const [message, description] = rows[1];
    const [symbolPart, ...textParts] = message.split(";");
    const symbol = symbolPart.trim();
    const text = textParts.join(";").trim() || (description && description !== "NA" ? description : "");
    if (!text) continue;
    for (const key of codes) queue("windows-update", key, { symbol, description: text, firstSentence: true, confidence: "high", source: msdoc(URL.wuErrors), category: categoryForWuKey(key) });
  }

  // intune — skip the Android table (heading order: Android, iOS/iPadOS, Other)
  {
    const tables = parseTables(await fetchPage(URL.intuneApps));
    for (const table of tables.slice(1)) {
      for (const [value, , message, description] of table.slice(1)) {
        if (!/^0x[0-9a-f]{8}$/i.test(value)) continue;
        const key = normalizeHex(value);
        const text = /^\(client error\)$/i.test(message) ? description : message;
        const category = key === "0x87d1041c" || key === "0x87d103e8" ? "intune-win32" : key.startsWith("0x87d1") ? "intune-mobile" : "appx";
        queue("intune", key, { description: text, firstSentence: true, confidence: "high", source: msdoc(URL.intuneApps), category });
      }
    }
  }

  // prose pages
  for (const e of PROSE_ENTRIES) {
    if (e.page) {
      const html = (await fetchPage(e.page)).toLowerCase();
      if (!html.includes(e.key.toLowerCase())) throw new Error(`prose: ${e.key} not found on ${e.page}`);
    }
    queue("prose", e.key, { symbol: e.symbol, description: e.description, confidence: e.confidence ?? "high", source: e.source ?? msdoc(e.page), category: e.category });
  }

  // IME CustomErrorCodes (0x87D3xxxx)
  for (const [name, rawValue] of Object.entries(ime.customErrorCodes)) {
    if (name === "Undefined") continue;
    let value = Number(rawValue);
    const fix = IME_ENUM_FIXUPS[name];
    if (fix) {
      if (value !== fix.seen) throw new Error(`IME fixup for ${name} is stale: source shows ${value}, fixup expects ${fix.seen}`);
      value = fix.intended;
    }
    queue("ime-custom", fromSigned(value), { symbol: name, description: humanizeMember(name), confidence: "medium", source: IME_SOURCE, category: "intune-win32" });
  }
  for (const missing of Object.keys(IME_ENUM_FIXUPS).filter((n) => !(n in ime.customErrorCodes))) {
    throw new Error(`IME fixup names ${missing}, which the enum extract does not contain`);
  }

  // IME Delivery Optimization constants
  const doc = ime.deliveryOptimization;
  for (const group of [doc.errorCodesDoSvc, doc.transientErrorCodesDoSvc]) {
    for (const [name, value] of Object.entries(group)) {
      queue("ime-do", fromSigned(Number(value)), { symbol: name, description: humanizeMember(name), confidence: "medium", source: IME_SOURCE, category: "delivery-optimization" });
    }
  }
  const doWindowsCodes = Object.values(doc.windowsErrorCodes).map((v) => fromSigned(Number(v)));

  // legacy
  for (const [key, category, description, symbol] of LEGACY_ENTRIES) queue("legacy", key, { symbol, description, confidence: "low", source: "legacy-catalog-v1", category });

  // win32 — seed: field list ∪ retriable ∪ every facility-7 code any family named, minus the MSI decimal range.
  const win32Seed = new Set(WIN32_FIELD_SEEN);
  for (const n of retriable) win32Seed.add(n);
  // The AppX page is the authority for its own facility-7 range (0x80073Cxx/0x80073Dxx): its rows
  // carry the packaging-specific wording, so those codes are not routed through the Win32 reference.
  const appxKeys = new Set(pending.filter((p) => p.family === "appx").map((p) => p.key));
  for (const key of [...pending.map((p) => p.key), ...doWindowsCodes]) {
    if (isFacility7(key) && !appxKeys.has(key)) win32Seed.add(parseInt(key.slice(6), 16));
  }
  for (const n of [...win32Seed]) if (MSI_DECIMAL(n)) win32Seed.delete(n);

  const win32Pages = new Map();
  const resolveWin32 = async (n) => {
    const useWinHttp = n >= WINHTTP_RANGE[0] && n <= WINHTTP_RANGE[1];
    const range = WIN32_RANGES.find(([lo, hi]) => n >= lo && n <= hi);
    const candidates = useWinHttp ? [URL.winhttp] : [];
    if (range) candidates.push(URL.win32(`${range[0]}-${range[1]}`));
    for (const url of candidates) {
      if (!win32Pages.has(url)) win32Pages.set(url, parseSymbolPage(await fetchPage(url)));
      const hit = win32Pages.get(url).get(n);
      if (hit) return { ...hit, url };
    }
    return null;
  };
  // A code another family names but the Win32 reference does not list (the AppX page carries
  // 0x80073Dxx symbols the System Error Codes pages omit) stays with that family; a code from
  // the explicit seed must resolve.
  const mustResolve = new Set([...WIN32_FIELD_SEEN, ...retriable]);
  const unresolved = [];
  for (const n of [...win32Seed].sort((a, b) => a - b)) {
    const hit = await resolveWin32(n);
    if (!hit) { if (mustResolve.has(n)) unresolved.push(n); continue; }
    add("win32", hexKey(0x80070000 + n), { symbol: hit.symbol, description: hit.text, confidence: "high", source: msdoc(hit.url), category: "win32", imeRetriesDuringEsp: retriable.has(n) });
  }
  if (unresolved.length > 0) throw new Error(`win32: no reference entry for ${unresolved.join(", ")}`);

  for (const p of pending) add(p.family, p.key, p.entry);

  // enforcement states
  const enforcementStates = {};
  for (const [name, value] of Object.entries(ime.enforcementStates)) {
    enforcementStates[String(value)] = { name, description: humanizeMember(name) };
  }

  validate(entries, enforcementStates);

  // ── Emit ──
  const keys = [...entries.keys()].sort((a, b) => {
    const ah = a.startsWith("0x"), bh = b.startsWith("0x");
    if (ah !== bh) return ah ? 1 : -1;
    return ah ? a.localeCompare(b) : Number(a) - Number(b);
  });
  const stateKeys = Object.keys(enforcementStates).sort((a, b) => Number(a) - Number(b));
  const header = {
    schemaVersion: 2,
    description:
      "Single-source catalog of Windows / MSI / Intune error codes, generated by scripts/error-codes/generate.mjs from primary sources — " +
      "never edit by hand. Consumed by the backend (ErrorCodeCatalog.cs, ErrorCodeEnricher.cs), the web app (utils/errorCodeMap.ts via the " +
      "sync-error-codes prebuild step) and the MCP server (lookup_error_code). Keys are lowercase 8-digit hex ('0x80070005') or decimal MSI exit " +
      "codes ('1603'; only 0, 1601-1654, 3010 — other decimal exit codes are installer-defined). Win32 codes live only as 0x8007xxxx entries; " +
      "HRESULT_FROM_WIN32 values in the MSI range resolve to the decimal entry at lookup time. confidence: high = MS Learn or the IME build named " +
      "in source; medium = paraphrase of an enum member name or of an analysis rule; low = carried over from the v1 catalog without a primary " +
      "source. category is one of: " + CATEGORIES.join(", ") + ". imeRetriesDuringEsp marks the MSI return codes the IME retries automatically " +
      "during the Autopilot ESP. enforcementStates maps the IME app enforcement state numbers to their names.",
  };
  const lines = [];
  lines.push("{");
  lines.push(`  "schemaVersion": ${header.schemaVersion},`);
  lines.push(`  "description": ${JSON.stringify(header.description)},`);
  lines.push(`  "entries": {`);
  keys.forEach((k, i) => lines.push(`    ${JSON.stringify(k)}: ${JSON.stringify(entries.get(k))}${i < keys.length - 1 ? "," : ""}`));
  lines.push(`  },`);
  lines.push(`  "enforcementStates": {`);
  stateKeys.forEach((k, i) => lines.push(`    ${JSON.stringify(k)}: ${JSON.stringify(enforcementStates[k])}${i < stateKeys.length - 1 ? "," : ""}`));
  lines.push(`  }`);
  lines.push("}");
  const output = lines.join("\n") + "\n";

  const byFamily = {};
  for (const e of entries.values()) byFamily[e._family] = (byFamily[e._family] ?? 0) + 1;
  console.error(`entries: ${entries.size} (${Object.entries(byFamily).map(([f, n]) => `${f} ${n}`).join(", ")}), enforcementStates: ${stateKeys.length}`);
  if (dropped.length > 0) console.error(`dropped duplicates: ${dropped.map((d) => `${d.key}←${d.family} (${d.reason})`).join("; ")}`);

  if (checkOnly) {
    const current = existsSync(TARGET) ? readFileSync(TARGET, "utf8") : "";
    if (current !== output) { console.error("catalog differs from generator output"); process.exit(1); }
    console.error("catalog is up to date");
    return;
  }
  writeFileSync(TARGET, output, "utf8");
  console.error(`wrote ${path.relative(REPO_ROOT, TARGET)}`);
  execFileSync(process.execPath, [WEB_SYNC], { stdio: "inherit" });
}

function categoryForWuKey(key) {
  if (key.startsWith("0x800f")) return "cbs";
  if (key.startsWith("0x80d0")) return "delivery-optimization";
  return "windows-update";
}

function validate(map, states) {
  const symbols = new Map();
  for (const [key, e] of map) {
    if (!KEY_PATTERN.test(key)) throw new Error(`bad key ${key}`);
    if (!e.description) throw new Error(`${key}: empty description`);
    if (e.description.length > MAX_DESCRIPTION + 21) throw new Error(`${key}: description too long`);
    if (!CONFIDENCES.includes(e.confidence)) throw new Error(`${key}: bad confidence ${e.confidence}`);
    if (!CATEGORIES.includes(e.category)) throw new Error(`${key}: bad category ${e.category}`);
    if (!SOURCE_PATTERN.test(e.source)) throw new Error(`${key}: bad source ${e.source}`);
    if (e.symbol) {
      if (symbols.has(e.symbol)) throw new Error(`symbol ${e.symbol} on both ${symbols.get(e.symbol)} and ${key}`);
      symbols.set(e.symbol, key);
    }
  }
  const names = new Set();
  for (const [k, s] of Object.entries(states)) {
    if (!/^-?\d+$/.test(k)) throw new Error(`enforcement state key ${k}`);
    if (names.has(s.name)) throw new Error(`enforcement state name ${s.name} twice`);
    names.add(s.name);
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch((err) => { console.error(err.stack ?? String(err)); process.exit(1); });
}
