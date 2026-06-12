/**
 * Build-time precomputed search index — file format and validation.
 *
 * The knowledge base (rules/) and the event-type catalog are static per image,
 * so their embeddings are computed ONCE at Docker build time (see
 * precompute-embeddings.ts) and serialized to search-index.json. At boot the
 * server hydrates the vectors from disk instead of running 35-55s of ONNX
 * inference on the 0.25 vCPU container.
 *
 * Correctness invariant: the file is only trusted when BOTH the model name and
 * the per-section corpus hashes match what the booting server would otherwise
 * compute itself. A mismatch (rules changed without rebuild, model bumped,
 * malformed file) rejects the file and falls back to computing at boot — slow
 * but never stale.
 */

import { createHash } from 'node:crypto';
import type { SearchDocument } from './search-provider.js';
import type { PrecomputedDocument } from './vector-search-provider.js';

export interface PrecomputedSection {
  /** hashDocs() over the source corpus this section was computed from. */
  docsHash: string;
  entries: PrecomputedDocument[];
}

export interface PrecomputedIndexFile {
  /** Embedding model the vectors were computed with (must match MODEL_NAME). */
  model: string;
  knowledgeBase: PrecomputedSection;
  eventTypes: PrecomputedSection;
}

/**
 * Content hash of a document corpus. Order-insensitive (sorted by id) so an
 * incidental change in directory read order does not invalidate the index.
 * Metadata is included because search results carry it verbatim (candidate
 * selection reads metadata.eventType) — a metadata-only edit must invalidate.
 */
export function hashDocs(docs: SearchDocument[]): string {
  const h = createHash('sha256');
  for (const d of [...docs].sort((a, b) => a.id.localeCompare(b.id))) {
    h.update(d.id);
    h.update('\0');
    h.update(d.text);
    h.update('\0');
    h.update(JSON.stringify(d.metadata));
    h.update('\0');
  }
  return h.digest('hex');
}

export type PrecomputedValidation =
  | { ok: true; knowledgeBase: PrecomputedDocument[]; eventTypes: PrecomputedDocument[] }
  | { ok: false; reason: string };

function validateSection(
  section: PrecomputedSection | undefined,
  name: string,
  sourceDocs: SearchDocument[],
): string | null {
  if (!section || !Array.isArray(section.entries)) return `${name}: missing or malformed section`;
  if (section.entries.length === 0) return `${name}: empty entries`;
  if (section.docsHash !== hashDocs(sourceDocs)) {
    return `${name}: corpus hash mismatch (docs changed since the index was built)`;
  }
  if (section.entries.length !== sourceDocs.length) {
    return `${name}: entry count ${section.entries.length} != corpus size ${sourceDocs.length}`;
  }
  const dims = section.entries[0]?.embedding?.length ?? 0;
  if (dims === 0) return `${name}: first entry has no embedding`;
  for (const e of section.entries) {
    if (typeof e.id !== 'string' || typeof e.text !== 'string') return `${name}: entry missing id/text`;
    if (!Array.isArray(e.embedding) || e.embedding.length !== dims) {
      return `${name}: inconsistent embedding dimensions`;
    }
  }
  return null;
}

/**
 * Validate a parsed search-index.json against the corpus the server just loaded
 * and the embedding model it would use. Never throws — the caller treats any
 * rejection as "compute at boot instead" and must log the reason loudly.
 */
export function validatePrecomputedIndex(
  parsed: unknown,
  expectedModel: string,
  knowledgeDocs: SearchDocument[],
  eventTypeDocs: SearchDocument[],
): PrecomputedValidation {
  if (parsed === null || typeof parsed !== 'object') return { ok: false, reason: 'not an object' };
  const file = parsed as Partial<PrecomputedIndexFile>;
  if (file.model !== expectedModel) {
    return { ok: false, reason: `model mismatch: file=${String(file.model)} expected=${expectedModel}` };
  }
  const kbError = validateSection(file.knowledgeBase, 'knowledgeBase', knowledgeDocs);
  if (kbError) return { ok: false, reason: kbError };
  const etError = validateSection(file.eventTypes, 'eventTypes', eventTypeDocs);
  if (etError) return { ok: false, reason: etError };
  return { ok: true, knowledgeBase: file.knowledgeBase!.entries, eventTypes: file.eventTypes!.entries };
}
