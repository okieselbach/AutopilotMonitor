/**
 * Build-time embedding precomputation (Docker build stage).
 *
 * Embeds the knowledge base (rules/) and the event-type catalog ONCE at image
 * build time and writes search-index.json, so the production boot hydrates
 * vectors from disk instead of spending 35-55s of ONNX inference on the
 * 0.25 vCPU container. Running the real pipeline here also doubles as the
 * model prefetch into HF_CACHE_DIR (no HuggingFace CDN dependency at runtime).
 *
 * Usage: node dist/precompute-embeddings.js <rulesDir> <outFile>
 */

import { writeFileSync } from 'node:fs';
import { loadKnowledgeDocs } from './knowledge-base.js';
import { buildEventTypeSearchDocs } from './resource-catalog.js';
import { MODEL_NAME, VectorSearchProvider } from './vector-search-provider.js';
import { hashDocs, type PrecomputedIndexFile } from './precomputed-index.js';

const [rulesDir, outFile] = process.argv.slice(2);
if (!rulesDir || !outFile) {
  console.error('usage: node dist/precompute-embeddings.js <rulesDir> <outFile>');
  process.exit(2);
}

const knowledgeDocs = await loadKnowledgeDocs(rulesDir);
if (knowledgeDocs.length === 0) {
  // An empty corpus means the rules dir is missing/miscopied — writing an
  // "index" of nothing would mask the build defect until someone searches.
  console.error(`no knowledge documents found under ${rulesDir} — refusing to write an empty index`);
  process.exit(1);
}
const eventTypeDocs = buildEventTypeSearchDocs();

const knowledgeBase = new VectorSearchProvider();
await knowledgeBase.index(knowledgeDocs);
const eventTypeIndex = new VectorSearchProvider();
await eventTypeIndex.index(eventTypeDocs);

const file: PrecomputedIndexFile = {
  model: MODEL_NAME,
  knowledgeBase: { docsHash: hashDocs(knowledgeDocs), entries: knowledgeBase.serialize() },
  eventTypes: { docsHash: hashDocs(eventTypeDocs), entries: eventTypeIndex.serialize() },
};
writeFileSync(outFile, JSON.stringify(file));
console.error(
  `precomputed embeddings: ${knowledgeDocs.length} knowledge docs + ${eventTypeDocs.length} event types (${MODEL_NAME}) → ${outFile}`,
);
