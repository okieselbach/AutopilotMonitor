import { SHARED_MANIFEST } from "@/lib/generated/shared-manifests.generated";

// The headline's vocabularies as the backend writes them (shared manifest). Text maps keyed by them
// are exhaustive, so a value the backend adds fails this build until it has a text.
type Vocabulary = typeof SHARED_MANIFEST.diagnosis;
export type Outcome = Vocabulary["outcomes"][number];
export type CulpritEvidence = Vocabulary["culpritEvidence"][number];
export type Caveat = Vocabulary["caveats"][number];
export type Prerequisite = Vocabulary["prerequisites"][number];

/** The entry for a wire value; undefined for a value newer than this build (the backend deploys first). */
export function vocabularyEntry<K extends string, V>(map: Record<K, V>, value: string | null | undefined): V | undefined {
  return value != null && Object.prototype.hasOwnProperty.call(map, value) ? map[value as K] : undefined;
}
