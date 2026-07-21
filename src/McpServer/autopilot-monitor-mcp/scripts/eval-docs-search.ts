/**
 * Offline retrieval evaluation for search_docs.
 *
 * Exists because a single anecdote is not evidence. The WOW6432Node case showed
 * that the keyword fallback misses when semantic search returns weak-but-present
 * results — but "it fixes that one query" says nothing about how many queries a fix
 * BREAKS. This harness answers that: it scores several ranking strategies over the
 * same query set and reports, per class, how many queries each strategy fixes and
 * how many it regresses relative to the deployed behaviour.
 *
 * Three query classes, deliberately different in character:
 *
 *   known-item  Every chunk's own heading as the query, expecting any chunk with
 *               that heading back. Easy by construction — its value is as a
 *               REGRESSION detector, not an absolute score. If a strategy drops
 *               these, it is breaking retrieval of things the corpus plainly names.
 *   literal     Rare identifiers (few chunks, >=6 chars) as single-word queries,
 *               expecting the chunks that literally contain them. This is the class
 *               WOW6432Node belongs to and the one the fallback exists for.
 *   handwritten Real questions a human would ask, with the page they should reach.
 *               The reality anchor: unlike the other two, these are not derived from
 *               the corpus and share little vocabulary with it.
 *
 * Embeddings are computed ONCE per query and every strategy is a pure function over
 * the same semantic + literal candidate lists, so a threshold sweep costs no extra
 * inference.
 *
 * Usage: npx tsx scripts/eval-docs-search.ts [docsDir]
 */

import { loadDocsCorpus } from '../src/docs-corpus.js';
import { VectorSearchProvider } from '../src/vector-search-provider.js';
import { extractDistinctiveTerms } from '../src/tools/search.js';
import type { SearchDocument, SearchResult } from '../src/search-provider.js';

const DOCS_DIR = process.argv[2] ?? 'c:/Code/GitHubRepos/autopilotmonitor-docs';
const TOP_K = 3;
const MIN_SCORE = 0.25;

interface EvalQuery {
  klass: 'known-item' | 'literal' | 'handwritten';
  query: string;
  /** Any of these ids counts as correct. */
  expected: Set<string>;
}

// ── Query set ────────────────────────────────────────────────

function buildQueries(docs: SearchDocument[]): EvalQuery[] {
  const queries: EvalQuery[] = [];

  // known-item: heading → chunks carrying that heading.
  const byHeading = new Map<string, Set<string>>();
  for (const d of docs) {
    const h = d.metadata.heading as string | null;
    if (!h) continue;
    const leaf = h.split('›').pop()!.trim();
    if (leaf.length < 8) continue;
    if (!byHeading.has(leaf)) byHeading.set(leaf, new Set());
    byHeading.get(leaf)!.add(d.id);
  }
  for (const [heading, ids] of byHeading) {
    queries.push({ klass: 'known-item', query: heading, expected: ids });
  }

  // literal: rare identifier-ish tokens → chunks literally containing them.
  const occurrences = new Map<string, Set<string>>();
  for (const d of docs) {
    for (const t of new Set(d.text.toLowerCase().match(/[a-z][a-z0-9_-]{5,}/g) ?? [])) {
      if (!occurrences.has(t)) occurrences.set(t, new Set());
      occurrences.get(t)!.add(d.id);
    }
  }
  for (const [token, ids] of occurrences) {
    if (ids.size > 3) continue; // not rare enough to be a known-item probe
    // Expected set is every chunk literally containing it, not just the tokenized hits.
    const containing = new Set(docs.filter((d) => d.text.toLowerCase().includes(token)).map((d) => d.id));
    queries.push({ klass: 'literal', query: token, expected: containing });
  }

  // handwritten: the reality anchor.
  const byPath = (path: string) => new Set(docs.filter((d) => d.metadata.path === path).map((d) => d.id));
  const handwritten: Array<[string, string]> = [
    ['how do I deploy the agent with Intune', 'troubleshooting/faq.md'],
    ['where is my data stored', 'troubleshooting/faq.md'],
    ['which role is allowed to delete a session', 'portal-guide/dashboard-and-sessions.md'],
    ['how do I set up Teams notifications', 'reference/settings.md'],
    ['what firewall URLs do I need to allow', 'reference/network-endpoints.md'],
    ['is Autopilot Monitor free', 'troubleshooting/faq.md'],
    ['how long is my data retained before deletion', 'trust/security-faq.md'],
    ['what is a gather rule and when does it run', 'rules/gather-rules.md'],
    ['what can helpdesk staff see in the progress portal', 'portal-guide/progress-portal.md'],
    ['can an MSP access my tenant', 'trust/security-faq.md'],
    ['Office Click-to-Run install never finished', 'rules/analyze-rules/built-in-rules.md'],
    ['proxy configuration blocking app downloads', 'rules/analyze-rules/built-in-rules.md'],
  ];
  for (const [query, path] of handwritten) {
    queries.push({ klass: 'handwritten', query, expected: byPath(path) });
  }

  return queries;
}

// ── Strategies ───────────────────────────────────────────────

interface Candidates {
  semantic: SearchResult[]; // sorted desc, unfiltered
  literal: SearchResult[]; // corroborated, ranked by term count
}

type Strategy = (c: Candidates) => SearchResult[];

/** Deployed behaviour: semantic, topped up by literal only when short of topK. */
const baseline: Strategy = ({ semantic, literal }) => {
  const res = semantic.filter((s) => s.score >= MIN_SCORE).slice(0, TOP_K);
  if (res.length < TOP_K) {
    const have = new Set(res.map((r) => r.id));
    for (const l of literal) {
      if (res.length >= TOP_K) break;
      if (have.has(l.id)) continue;
      have.add(l.id);
      res.push(l);
    }
  }
  return res;
};

/**
 * Candidate fix: additionally treat a WEAK semantic head as unconvincing and let
 * exact literal matches take the front, keeping at least one semantic result so a
 * weak-but-right hit is never fully displaced.
 */
function weakTrigger(threshold: number): Strategy {
  return ({ semantic, literal }) => {
    const res = semantic.filter((s) => s.score >= MIN_SCORE).slice(0, TOP_K);
    const have = new Set(res.map((r) => r.id));
    const fresh = literal.filter((l) => !have.has(l.id));
    if (res.length < TOP_K) {
      for (const l of fresh) {
        if (res.length >= TOP_K) break;
        res.push(l);
      }
      return res;
    }
    if (fresh.length > 0 && (res[0]?.score ?? 0) < threshold) {
      return [...fresh.slice(0, TOP_K - 1), ...res].slice(0, TOP_K);
    }
    return res;
  };
}

// ── Scoring ──────────────────────────────────────────────────

interface ClassStats {
  n: number;
  hit: number; // recall@TOP_K
  mrr: number;
}

function score(results: SearchResult[], expected: Set<string>): { hit: boolean; rr: number } {
  const rank = results.findIndex((r) => expected.has(r.id));
  return { hit: rank >= 0, rr: rank >= 0 ? 1 / (rank + 1) : 0 };
}

async function main() {
  const docs = await loadDocsCorpus(DOCS_DIR);
  console.error(`corpus: ${docs.length} chunks`);
  const index = new VectorSearchProvider();
  await index.index(docs);
  console.error(`indexed: ${index.vectorCount} vectors\n`);

  const queries = buildQueries(docs);
  const counts = queries.reduce<Record<string, number>>((a, q) => ({ ...a, [q.klass]: (a[q.klass] ?? 0) + 1 }), {});
  console.error(`queries: ${queries.length} (${Object.entries(counts).map(([k, v]) => `${k}=${v}`).join(', ')})`);
  console.error('embedding queries…');

  // One inference pass; strategies are pure functions over these candidates.
  const candidates: Candidates[] = [];
  for (let i = 0; i < queries.length; i++) {
    const q = queries[i];
    const semantic = await index.search(q.query, { topK: 10, minScore: -1 });
    const terms = extractDistinctiveTerms(q.query);
    let literal: SearchResult[] = [];
    if (terms.length > 0) {
      const required = Math.min(2, terms.length);
      literal = index
        .lexicalMatch(terms)
        .map((hit) => ({ hit, matched: terms.filter((t) => hit.text.toLowerCase().includes(t)).length }))
        .filter((x) => x.matched >= required)
        .sort((a, b) => b.matched - a.matched)
        .map((x) => x.hit);
    }
    candidates.push({ semantic, literal });
    if ((i + 1) % 200 === 0) console.error(`  ${i + 1}/${queries.length}`);
  }

  const strategies: Array<[string, Strategy]> = [
    ['baseline (deployed)', baseline],
    ['weak<0.30', weakTrigger(0.30)],
    ['weak<0.35', weakTrigger(0.35)],
    ['weak<0.40', weakTrigger(0.40)],
    ['weak<0.45', weakTrigger(0.45)],
  ];

  // Per-query correctness under the baseline, for the fixed/broken delta.
  const baseHit = queries.map((q, i) => score(baseline(candidates[i]), q.expected).hit);

  console.log(`\n${'strategy'.padEnd(20)} ${'class'.padEnd(12)} ${'n'.padStart(5)} ${'recall@3'.padStart(9)} ${'MRR'.padStart(7)} ${'fixed'.padStart(6)} ${'broken'.padStart(7)}`);
  console.log('-'.repeat(75));

  for (const [name, strat] of strategies) {
    const stats = new Map<string, ClassStats>();
    let fixed = 0;
    let broken = 0;
    queries.forEach((q, i) => {
      const { hit, rr } = score(strat(candidates[i]), q.expected);
      const s = stats.get(q.klass) ?? { n: 0, hit: 0, mrr: 0 };
      s.n++;
      if (hit) s.hit++;
      s.mrr += rr;
      stats.set(q.klass, s);
      if (hit && !baseHit[i]) fixed++;
      if (!hit && baseHit[i]) broken++;
    });
    for (const [klass, s] of [...stats].sort()) {
      console.log(
        `${name.padEnd(20)} ${klass.padEnd(12)} ${String(s.n).padStart(5)} ` +
          `${((s.hit / s.n) * 100).toFixed(1).padStart(8)}% ${(s.mrr / s.n).toFixed(3).padStart(7)} ` +
          `${'—'.padStart(6)} ${'—'.padStart(7)}`,
      );
    }
    console.log(`${name.padEnd(20)} ${'TOTAL'.padEnd(12)} ${String(queries.length).padStart(5)} ${''.padStart(9)} ${''.padStart(7)} ${String(fixed).padStart(6)} ${String(broken).padStart(7)}`);
    console.log('-'.repeat(75));
  }
}

await main();
