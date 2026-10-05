import { describe, expect, it } from "vitest";
import { strFromU8, unzipSync } from "fflate";
import { bytesToBase64 } from "../base64";
import {
  ATTACHMENT_BUDGET_BYTES,
  REPORT_REQUEST_MAX_BYTES,
  fitsReportRequest,
  packLogs,
  packScreenshots,
  packedSize,
  uniqueEntryNames,
  utf8ByteLength,
  type NamedBlob,
} from "../reportAttachments";
import { SHARED_MANIFEST } from "@/utils/shared-manifests.generated";

function blob(name: string, content: string | Uint8Array): NamedBlob {
  const bytes = typeof content === "string" ? new TextEncoder().encode(content) : content;
  return { name, size: bytes.length, arrayBuffer: async () => bytes.slice().buffer };
}

function pseudoRandomBytes(length: number): Uint8Array {
  const out = new Uint8Array(length);
  let x = 0x2545f491;
  for (let i = 0; i < length; i++) {
    x ^= x << 13;
    x ^= x >>> 17;
    x ^= x << 5;
    out[i] = x & 0xff;
  }
  return out;
}

const LOG_LINE = "<![LOG[Processing app policy 3f2504e0-4f89-11d3-9a0c-0305e82c3301]LOG]!><time=\"09:15:23.1234567\">\n";

describe("bytesToBase64", () => {
  it.each([0, 1, 2, 3, 0x7fff, 0x8000, 0x8001, 100_000])("matches Node's encoder for %i bytes", (length) => {
    const bytes = pseudoRandomBytes(length);
    expect(bytesToBase64(bytes)).toBe(Buffer.from(bytes).toString("base64"));
  });
});

describe("attachment budget", () => {
  it("comes from the backend's body cap in the shared manifest", () => {
    expect(REPORT_REQUEST_MAX_BYTES).toBe(SHARED_MANIFEST.submissionLimits.reportRequestMaxBytes);
  });

  it("fits the cap once base64-encoded, with room left for the rest of the body", () => {
    const encoded = 4 * Math.ceil(ATTACHMENT_BUDGET_BYTES / 3);
    expect(encoded + 256 * 1024).toBeLessThanOrEqual(REPORT_REQUEST_MAX_BYTES);
  });

  it("allows well over 10 MB of packed attachments", () => {
    expect(ATTACHMENT_BUDGET_BYTES).toBeGreaterThan(14 * 1024 * 1024);
  });
});

describe("uniqueEntryNames", () => {
  it("keeps every file when names repeat (case-insensitive), numbering the later ones", () => {
    expect(uniqueEntryNames(["IME.log", "ime.log", "IME.log", "noext", "noext"]))
      .toEqual(["IME.log", "ime (2).log", "IME (3).log", "noext", "noext (2)"]);
  });
});

describe("packLogs", () => {
  it("returns null without files", async () => {
    expect(await packLogs([], "logs.zip")).toBeNull();
  });

  it("zips a single log under its own name and shrinks text", async () => {
    const text = LOG_LINE.repeat(2000);
    const packed = await packLogs([blob("IntuneManagementExtension.log", text)], "logs.zip");

    expect(packed!.fileName).toBe("IntuneManagementExtension.log.zip");
    expect(packed!.bytes.length).toBeLessThan(text.length / 5);
    const entries = unzipSync(packed!.bytes);
    expect(strFromU8(entries["IntuneManagementExtension.log"])).toBe(text);
  });

  it("sends a single .zip as it is", async () => {
    const zip = pseudoRandomBytes(5000);
    const packed = await packLogs([blob("collected.zip", zip)], "logs.zip");

    expect(packed!.fileName).toBe("collected.zip");
    expect(packed!.bytes).toEqual(zip);
  });

  it("bundles several files into the given zip without losing same-named ones", async () => {
    const packed = await packLogs([blob("IME.log", "first"), blob("IME.log", "second device")], "agent-logs.zip");

    expect(packed!.fileName).toBe("agent-logs.zip");
    const entries = unzipSync(packed!.bytes);
    expect(strFromU8(entries["IME.log"])).toBe("first");
    expect(strFromU8(entries["IME (2).log"])).toBe("second device");
  });
});

describe("packScreenshots", () => {
  it("sends one image as it is", async () => {
    const png = pseudoRandomBytes(3000);
    const packed = await packScreenshots([blob("esp.png", png)], "screenshots.zip");

    expect(packed!.fileName).toBe("esp.png");
    expect(packed!.bytes).toEqual(png);
  });

  it("stores several images uncompressed in one zip", async () => {
    const a = pseudoRandomBytes(4000);
    const b = pseudoRandomBytes(6000);
    const packed = await packScreenshots([blob("a.png", a), blob("b.png", b)], "screenshots.zip");

    expect(packed!.fileName).toBe("screenshots.zip");
    expect(packed!.bytes.length).toBeGreaterThanOrEqual(a.length + b.length);
    const entries = unzipSync(packed!.bytes);
    expect(entries["a.png"]).toEqual(a);
    expect(entries["b.png"]).toEqual(b);
  });
});

describe("sizes", () => {
  it("adds up the packed attachments, ignoring missing ones", () => {
    expect(packedSize(null, { bytes: new Uint8Array(10), fileName: "a" }, { bytes: new Uint8Array(5), fileName: "b" }))
      .toBe(15);
  });

  it("measures bodies in UTF-8 bytes, like the backend", () => {
    expect(utf8ByteLength("ä€")).toBe(5);
    expect(fitsReportRequest("x".repeat(REPORT_REQUEST_MAX_BYTES))).toBe(true);
    expect(fitsReportRequest("x".repeat(REPORT_REQUEST_MAX_BYTES + 1))).toBe(false);
    expect(fitsReportRequest("ä".repeat(REPORT_REQUEST_MAX_BYTES / 2 + 1))).toBe(false);
  });
});
