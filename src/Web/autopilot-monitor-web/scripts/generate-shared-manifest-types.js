/**
 * Generate utils/shared-manifests.generated.ts AND utils/wire-types.generated.ts
 * from utils/shared-manifests.json (schemaVersion 2).
 *
 * The JSON is produced by the backend reflection guard (SharedManifestParityTests,
 * regenerated via AM_WRITE_SHARED_MANIFESTS=1). This script lifts it into TS twice:
 *  - shared-manifests.generated.ts: the catalog sections as an `as const` LITERAL
 *    module (compile-time drift checks in utils/sharedManifestChecks.ts). The bulky
 *    "types" section is stripped here — it lives in the second file.
 *  - wire-types.generated.ts: one interface per wire object, one string-union per
 *    wire enum, with the C# <summary> texts as JSDoc. These are the authoritative
 *    response types — hand-written mirrors re-export from here.
 *  - a second copy of the wire types into the MCP server
 *    (src/McpServer/autopilot-monitor-mcp/src/generated/wire-types.generated.ts),
 *    whose tools read the same backend responses.
 *  - wire-vocabularies.generated.ts for the MCP: the vocabulary sections as runtime
 *    ARRAYS, so tool input schemas derive their z.enum() from backend truth instead
 *    of retyping it (types alone are erased at runtime — that gap is what let the
 *    MCP enums drift).
 *
 * Run: node scripts/generate-shared-manifest-types.js  (npm run generate:manifests)
 * Freshness of the web outputs is pinned by utils/__tests__/sharedManifestParity.test.ts,
 * the MCP copy by the MCP suite's wire-types-freshness.test.ts.
 */

const fs = require("fs");
const path = require("path");

const WEB_ROOT = path.resolve(__dirname, "..");
const SOURCE = path.join(WEB_ROOT, "utils", "shared-manifests.json");
const DEST_MANIFEST = path.join(WEB_ROOT, "utils", "shared-manifests.generated.ts");
const DEST_TYPES = path.join(WEB_ROOT, "utils", "wire-types.generated.ts");
// Second copy for the MCP server (same repo) — its tools read the same backend wire.
const MCP_ROOT = path.resolve(WEB_ROOT, "..", "..", "McpServer", "autopilot-monitor-mcp");
const DEST_MCP_TYPES = path.join(MCP_ROOT, "src", "generated", "wire-types.generated.ts");
// Values (not types) for the MCP tool schemas — see MCP_VOCABULARIES.
const DEST_MCP_VOCAB = path.join(MCP_ROOT, "src", "generated", "wire-vocabularies.generated.ts");

const HEADER =
  "// GENERATED from shared-manifests.json — do not edit by hand.\n" +
  "// Regenerate: node scripts/generate-shared-manifest-types.js\n" +
  "// (after AM_WRITE_SHARED_MANIFESTS=1 dotnet test --filter SharedManifestParityTests)\n";

const MCP_HEADER =
  "// GENERATED — do not edit by hand. Second copy for the MCP server.\n" +
  "// Source: src/Web/autopilot-monitor-web/utils/shared-manifests.json.\n" +
  "// Regenerate: npm run generate:manifests in src/Web/autopilot-monitor-web.\n";

/** Pure builder so the vitest freshness check can reuse it. */
function buildGeneratedSource(manifestJsonText) {
  const manifest = JSON.parse(manifestJsonText);
  // The full type graph goes to wire-types.generated.ts — keep the literal module lean.
  delete manifest.types;
  return (
    HEADER + "\n" + "export const SHARED_MANIFEST = " + JSON.stringify(manifest, null, 2) + " as const;\n"
  );
}

/** Pure builder for utils/wire-types.generated.ts (same freshness contract). */
function buildWireTypesSource(manifestJsonText) {
  return buildWireTypesSourceWithHeader(manifestJsonText, HEADER);
}

/** Pure builder for the MCP server's src/generated/wire-types.generated.ts copy. */
function buildMcpWireTypesSource(manifestJsonText) {
  return buildWireTypesSourceWithHeader(manifestJsonText, MCP_HEADER);
}

/**
 * Manifest section → exported const name, for the MCP's VALUE copy of the vocabularies.
 *
 * The web reads vocabularies straight off SHARED_MANIFEST (an `as const` literal module),
 * but the MCP only ever received the TYPES file — and types are erased at runtime, while a
 * `z.enum([...])` needs values. That asymmetry is why every MCP tool had to retype its
 * vocabularies by hand, with nothing to compare against. Sections listed here are emitted as
 * runtime arrays so the tools can derive their enums instead.
 *
 * Add a section here only when the MCP actually consumes it — an unused export is one more
 * thing to keep honest for no gain.
 */
const MCP_VOCABULARIES = [
  { section: "sessionStatuses", constName: "SESSION_STATUSES", typeName: "SessionStatusName",
    doc: "Enrollment session statuses (C# SessionStatus)." },
  { section: "eventSeverities", constName: "EVENT_SEVERITIES", typeName: "EventSeverityName",
    doc: "Telemetry event severities (C# EventSeverity) — includes Debug and Trace." },
  { section: "opsEventCategories", constName: "OPS_EVENT_CATEGORIES", typeName: "OpsEventCategoryName",
    doc: "Ops event categories = OpsEvents partition keys (C# OpsEventCategory)." },
  { section: "opsEventSeverities", constName: "OPS_EVENT_SEVERITIES", typeName: "OpsEventSeverityName",
    doc: "Ops event severities, ascending (C# OpsEventSeverity) — the order IS the ladder." },
  { section: "opsEventTypes", constName: "OPS_EVENT_TYPES", typeName: "OpsEventTypeName",
    doc: "Every ops event type the backend can write (C# OpsEventTypes)." },
  { section: "annotationLanes", constName: "ANNOTATION_LANES", typeName: "AnnotationLane",
    doc: "Session-annotation lanes (C# AnnotationLanes)." },
  { section: "annotationVerdicts", constName: "ANNOTATION_VERDICTS", typeName: "AnnotationVerdict",
    doc: "Session-annotation verdicts (C# AnnotationVerdicts)." },
  { section: "logSources", constName: "LOG_SOURCES", typeName: "LogSourceName",
    doc: "Telemetry stores behind the operator KQL proxy — query_backend_logs `source` (C# LogQuerySources)." },
];

/**
 * Pure builder for the MCP server's src/generated/wire-vocabularies.generated.ts.
 * Same freshness contract as the wire types: byte-equal to a fresh run or the MCP suite fails.
 */
function buildMcpVocabulariesSource(manifestJsonText) {
  const manifest = JSON.parse(manifestJsonText);
  if (manifest.schemaVersion !== 2) {
    throw new Error("shared-manifests.json must be schemaVersion 2");
  }

  const parts = [
    MCP_HEADER +
      "//\n" +
      "// Vocabularies reflected from AutopilotMonitor.Shared. These are VALUES (not just types)\n" +
      "// so tool input schemas can derive their z.enum() from the backend truth instead of\n" +
      "// retyping it — a hand-typed list drifts silently and the tool then advertises a\n" +
      "// vocabulary the backend no longer has (or omits one it gained).\n",
  ];

  for (const { section, constName, typeName, doc } of MCP_VOCABULARIES) {
    const value = manifest[section];
    if (value === undefined) {
      throw new Error(`shared-manifests.json is missing the "${section}" section (MCP vocabulary)`);
    }
    // Enum sections are name→ordinal maps; the MCP only ever filters by NAME.
    const values = Array.isArray(value) ? value : Object.keys(value);
    if (values.length === 0) {
      throw new Error(`Manifest section "${section}" is empty — the reflection source is likely broken`);
    }
    parts.push("");
    parts.push(jsdoc(doc, ""));
    parts.push(`export const ${constName} = [`);
    for (const v of values) parts.push(`  ${JSON.stringify(v)},`);
    parts.push(`] as const;`);
    parts.push(`export type ${typeName} = (typeof ${constName})[number];`);
  }

  return parts.join("\n") + "\n";
}

function buildWireTypesSourceWithHeader(manifestJsonText, header) {
  const manifest = JSON.parse(manifestJsonText);
  const types = manifest.types;
  if (!types || manifest.schemaVersion !== 2) {
    throw new Error("shared-manifests.json must be schemaVersion 2 with a types section");
  }

  const parts = [
    header +
      "//\n" +
      "// Wire response types reflected from AutopilotMonitor.Shared (every IApiResponse\n" +
      "// implementer + [WireContract] type, transitively closed). Key ORDER, presence\n" +
      "// (optional = key absent under WhenWritingNull) and names mirror the C# wire exactly.\n",
  ];

  for (const name of Object.keys(types)) {
    const node = types[name];
    parts.push("");
    if (node.doc) parts.push(jsdoc(node.doc, ""));
    if (node.kind === "enum") {
      const union = node.members.map((m) => JSON.stringify(m)).join(" | ");
      parts.push(`export type ${name} = ${union};`);
    } else {
      parts.push(`export interface ${name} {`);
      for (const field of node.fields) {
        if (field.doc) parts.push(jsdoc(field.doc, "  "));
        const ro = field.readonly ? "readonly " : "";
        const opt = field.optional ? "?" : "";
        parts.push(`  ${ro}${fieldName(field.name)}${opt}: ${tsType(field.type)};`);
      }
      parts.push("}");
    }
  }

  return parts.join("\n") + "\n";
}

function tsType(descriptor) {
  const nullable = descriptor.nullable ? " | null" : "";
  switch (descriptor.kind) {
    case "primitive":
      return descriptor.name + nullable;
    case "ref":
    case "enum":
      return descriptor.name + nullable;
    case "partial":
      return `Partial<${descriptor.name}>` + nullable;
    case "array": {
      const element = tsType(descriptor.element);
      const wrapped = /[|\s]/.test(element) ? `(${element})` : element;
      return `${wrapped}[]` + nullable;
    }
    case "map":
      return `Record<string, ${tsType(descriptor.value)}>` + nullable;
    default:
      throw new Error(`Unknown descriptor kind: ${descriptor.kind}`);
  }
}

function fieldName(name) {
  return /^[A-Za-z_$][A-Za-z0-9_$]*$/.test(name) ? name : JSON.stringify(name);
}

function jsdoc(text, indent) {
  return `${indent}/** ${text.replace(/\*\//g, "*\\/")} */`;
}

function main() {
  if (!fs.existsSync(SOURCE)) {
    console.error(`[shared-manifests] Source not found: ${SOURCE}`);
    process.exit(1);
  }
  const json = fs.readFileSync(SOURCE, "utf8");
  fs.writeFileSync(DEST_MANIFEST, buildGeneratedSource(json), "utf8");
  console.log(`[shared-manifests] Wrote ${path.relative(WEB_ROOT, DEST_MANIFEST)}`);
  fs.writeFileSync(DEST_TYPES, buildWireTypesSource(json), "utf8");
  console.log(`[shared-manifests] Wrote ${path.relative(WEB_ROOT, DEST_TYPES)}`);
  fs.mkdirSync(path.dirname(DEST_MCP_TYPES), { recursive: true });
  fs.writeFileSync(DEST_MCP_TYPES, buildMcpWireTypesSource(json), "utf8");
  console.log(`[shared-manifests] Wrote ${path.relative(WEB_ROOT, DEST_MCP_TYPES)}`);
  fs.writeFileSync(DEST_MCP_VOCAB, buildMcpVocabulariesSource(json), "utf8");
  console.log(`[shared-manifests] Wrote ${path.relative(WEB_ROOT, DEST_MCP_VOCAB)}`);
}

if (require.main === module) {
  main();
}

module.exports = { buildGeneratedSource, buildWireTypesSource, buildMcpWireTypesSource, buildMcpVocabulariesSource };
