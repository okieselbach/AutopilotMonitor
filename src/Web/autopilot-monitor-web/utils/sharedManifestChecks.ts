/**
 * COMPILE-TIME drift checks between hand-written TS mirrors and the C#-generated
 * shared manifest (utils/shared-manifests.json → shared-manifests.generated.ts).
 *
 * A red squiggle / failing `tsc --noEmit` here means one side changed without the
 * other: either add the field to the TS mirror, or regenerate the manifest
 * (AM_WRITE_SHARED_MANIFESTS=1 dotnet test --filter SharedManifestParityTests,
 * then node scripts/generate-shared-manifest-types.js).
 *
 * Runtime (value-level) parity lives in utils/__tests__/sharedManifestParity.test.ts.
 */
import type { AdminConfiguration } from "@/types/adminConfig";
import { SHARED_MANIFEST } from "./shared-manifests.generated";

/** Resolves only when T is empty — a non-empty union here IS the drift report. */
type AssertNever<T extends never> = T;

type AdminWireKey = (typeof SHARED_MANIFEST)["adminConfiguration"]["fields"][number];

/** C# fields the TS interface is missing. Must stay `never`. */
export type AdminConfigFieldsMissingInTs = AssertNever<
  Exclude<AdminWireKey, keyof AdminConfiguration>
>;

/** TS fields the C# model no longer has (ghost fields). Must stay `never`. */
export type AdminConfigGhostFieldsInTs = AssertNever<
  Exclude<keyof AdminConfiguration, AdminWireKey>
>;
