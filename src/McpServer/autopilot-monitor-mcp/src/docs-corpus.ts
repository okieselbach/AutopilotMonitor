/**
 * Loads the published customer documentation bundle (autopilotmonitor-docs) and
 * chunks it into SearchDocuments for semantic search via the `search_docs` tool.
 *
 * This is the product-knowledge corpus — "how do I deploy the agent", "where is
 * my data stored", "which roles exist". It is deliberately SEPARATE from the
 * rules corpus in knowledge-base.ts: rules answer "why did this session fail",
 * docs answer "how does the product work". Indexed together they would compete
 * for the same topK and dilute each other.
 *
 * Like the rules corpus, docs are baked into the image and embedded at Docker
 * build time (see precompute-embeddings.ts) — the 0.25-0.5 vCPU container never
 * embeds a corpus at boot.
 *
 * Chunking matters more here than for rules: a rule is already a short, self-
 * contained record, whereas a doc page runs to 36 KB (trust/security-faq.md).
 * MiniLM-L6-v2 truncates at ~256 tokens, so a whole-page embedding would silently
 * represent only the first paragraph. We therefore split on heading boundaries,
 * which in this bundle are also the semantic boundaries (one h3 = one question).
 */

import { readdir, readFile } from 'node:fs/promises';
import type { Dirent } from 'node:fs';
import { join, relative, sep } from 'node:path';
import type { SearchDocument } from './search-provider.js';

/** Published base URL of the bundle — used to build a citable link per chunk. */
const DOCS_BASE_URL = 'https://docs.autopilotmonitor.com';

/**
 * Upper bound on chunk text. MiniLM-L6-v2's 256-token window is roughly 1000-1200
 * characters of English prose; going much beyond that means the tail of the chunk
 * contributes nothing to the embedding. We allow a little slack over that so a
 * coherent section (or an indivisible table) is not split for the sake of a few
 * characters — the overflow is only ever the part that gets truncated.
 */
const MAX_CHUNK_CHARS = 1500;

/**
 * A trailing fragment shorter than this is folded back into the preceding chunk
 * when splitting an oversized section, so a split never leaves a stranded line.
 */
const MIN_CHUNK_CHARS = 120;

/**
 * Minimum body length for a chunk to be indexed at all, EXCLUDING the breadcrumb
 * header. Set low on purpose: a short section is very often the most valuable
 * kind of hit ("Is Autopilot Monitor free?" → "Yes — the Community plan is free
 * and stays free"), and the breadcrumb already supplies the context that a short
 * body lacks. This filter only exists to drop structural residue — a stray rule
 * line, an empty section left behind by stripped GitBook markers.
 */
const MIN_BODY_CHARS = 25;

/**
 * Structural/meta files that are not documentation content.
 * `SUMMARY.md` is the GitBook table of contents and `log.md` is the OKF change
 * log — both are navigation scaffolding whose text would match queries about
 * every topic at once. `index.md` (the OKF bundle catalog) is deliberately KEPT:
 * it is the documentation map and answers "what documentation exists".
 */
const SKIP_FILES = new Set(['SUMMARY.md', 'log.md']);

const SKIP_DIRS = new Set(['.git', '.github', '.gitbook', 'node_modules']);

// ── Frontmatter ──────────────────────────────────────────────

/**
 * Normalize CRLF/CR to LF before ANY parsing.
 *
 * Not cosmetic — a correctness precondition. The docs repo is authored on
 * Windows and ships CRLF, and in JavaScript `.` does not match `\r`. A pattern
 * like /^(#{2,3})\s+(.*)$/ therefore fails on every single heading of a CRLF
 * file: `(.*)` stops before the `\r` and `$` never reaches end-of-string. The
 * observable symptom is silent, not a crash — headings and frontmatter keys
 * simply stop being recognized and the page degrades to unlabelled paragraph
 * splits. Normalizing once here is the root fix; the alternative is remembering
 * `\r?` in every pattern in this file forever.
 */
function normalizeNewlines(text: string): string {
  return text.replace(/\r\n?/g, '\n');
}

export interface DocFrontmatter {
  type?: string;
  title?: string;
  description?: string;
  tags?: string[];
}

/**
 * Minimal YAML frontmatter parser covering exactly the forms this bundle uses:
 * plain scalars, folded blocks (`description: >-`), inline sequences
 * (`tags: [a, b]`) and block sequences (`- a`). A full YAML parser would be a new
 * runtime dependency in a hardened, dependency-minimal container for the sake of
 * four flat keys.
 *
 * Anything it cannot parse is skipped rather than thrown — a malformed header
 * must not cost us the page body, which is the part being searched.
 */
export function parseFrontmatter(input: string): { frontmatter: DocFrontmatter; body: string } {
  const raw = normalizeNewlines(input);
  if (!raw.startsWith('---')) return { frontmatter: {}, body: raw };
  const end = raw.indexOf('\n---', 3);
  if (end === -1) return { frontmatter: {}, body: raw };

  const header = raw.slice(raw.indexOf('\n') + 1, end);
  const body = raw.slice(raw.indexOf('\n', end + 1) + 1);
  const fm: Record<string, string | string[]> = {};

  const lines = header.split('\n');
  for (let i = 0; i < lines.length; i++) {
    const match = /^([A-Za-z_][\w-]*):\s*(.*)$/.exec(lines[i]);
    if (!match) continue;
    const key = match[1];
    let value = match[2].trim();

    // Folded / literal block scalar: consume the following indented lines.
    if (value === '>-' || value === '>' || value === '|' || value === '|-') {
      const parts: string[] = [];
      while (i + 1 < lines.length && /^\s+\S/.test(lines[i + 1])) {
        parts.push(lines[++i].trim());
      }
      fm[key] = parts.join(' ');
      continue;
    }

    // Block sequence: `tags:` followed by `- item` lines.
    if (value === '') {
      const items: string[] = [];
      while (i + 1 < lines.length && /^\s*-\s+/.test(lines[i + 1])) {
        items.push(lines[++i].replace(/^\s*-\s+/, '').trim().replace(/^["']|["']$/g, ''));
      }
      if (items.length > 0) fm[key] = items;
      continue;
    }

    // Inline sequence: `[a, b, c]`.
    if (value.startsWith('[') && value.endsWith(']')) {
      fm[key] = value
        .slice(1, -1)
        .split(',')
        .map((s) => s.trim().replace(/^["']|["']$/g, ''))
        .filter(Boolean);
      continue;
    }

    fm[key] = value.replace(/^["']|["']$/g, '');
  }

  const asString = (v: string | string[] | undefined): string | undefined =>
    typeof v === 'string' ? v : undefined;
  const asArray = (v: string | string[] | undefined): string[] | undefined =>
    Array.isArray(v) ? v : typeof v === 'string' && v ? [v] : undefined;

  return {
    frontmatter: {
      type: asString(fm.type),
      title: asString(fm.title),
      description: asString(fm.description),
      tags: asArray(fm.tags),
    },
    body,
  };
}

// ── Markdown normalization ───────────────────────────────────

/**
 * Strip GitBook-specific block syntax while keeping the prose inside it.
 * `{% hint style="info" %}` wrappers carry no meaning for retrieval but do add
 * tokens that dilute the embedding; the sentence inside them frequently IS the
 * answer, so the content is preserved and only the markers removed.
 */
function stripGitbookSyntax(text: string): string {
  return text
    .replace(/^\s*\{%\s*(end)?(hint|tabs|tab|content-ref|embed|code)[^%]*%\}\s*$/gim, '')
    .replace(/^\s*\{%\s*end\w+\s*%\}\s*$/gim, '')
    .replace(/\n{3,}/g, '\n\n')
    .trim();
}

/** URL/anchor-safe slug from heading text. */
export function slugify(text: string): string {
  return text
    .toLowerCase()
    .replace(/[`*_[\]()]/g, '')
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '')
    .slice(0, 60);
}

/**
 * Public GitBook URL for a bundle-relative path, or undefined when the file is
 * not published as a page.
 *
 * `index.md` is an OKF reserved file that the bundle deliberately keeps out of
 * SUMMARY.md, so GitBook never renders it — emitting a link would hand callers a
 * 404. Its content is still indexed (it is the documentation map); it just has no
 * citable URL.
 */
export function docUrl(relPath: string): string | undefined {
  const posix = relPath.split(sep).join('/');
  if (posix === 'index.md') return undefined;
  const withoutExt = posix.replace(/\.md$/, '');
  const path = withoutExt === 'README' ? '' : withoutExt.replace(/\/README$/, '');
  return path ? `${DOCS_BASE_URL}/${path}` : DOCS_BASE_URL;
}

// ── Sectioning ───────────────────────────────────────────────

interface Section {
  /** Heading trail below the page title, e.g. ['Data Processing', 'What data…']. */
  breadcrumb: string[];
  /** Anchor of the deepest markdown heading, if this section came from one. */
  anchor?: string;
  content: string;
}

/**
 * Remove HTML tags from a string. The pattern is applied repeatedly until the
 * string stops changing, so tag removal can never re-form a tag from the joined
 * remainder. A single `.replace(/<[^>]+>/g, '')` happens to be complete for this
 * pattern, but the looped form is the shape CodeQL accepts as a robust sanitizer
 * (js/incomplete-multi-character-sanitization) and is defensive against any
 * future change to the tag regex.
 */
function stripHtmlTags(input: string): string {
  let prev: string;
  let out = input;
  do {
    prev = out;
    out = out.replace(/<[^>]+>/g, '');
  } while (out !== prev);
  return out;
}

/**
 * Split a page body into heading-delimited sections.
 *
 * Handles both structures the bundle uses: markdown h2/h3 headings (the common
 * case — trust/security-faq.md carries 37 h3s, one per question) and
 * `<details><summary>Q</summary>…</details>` blocks (troubleshooting/faq.md),
 * where the summary is the question and therefore the natural section title.
 */
function splitIntoSections(body: string): Section[] {
  const sections: Section[] = [];
  const lines = body.split('\n');

  let h2: string | null = null;
  let h3: string | null = null;
  let detailsTitle: string | null = null;
  let buffer: string[] = [];
  let inFence = false;

  const flush = (anchorSource: string | null) => {
    const content = buffer.join('\n').trim();
    buffer = [];
    if (!content) return;
    const breadcrumb = [h2, detailsTitle ?? h3].filter((s): s is string => Boolean(s));
    sections.push({
      breadcrumb,
      anchor: anchorSource ? slugify(anchorSource) : undefined,
      content,
    });
  };

  for (const line of lines) {
    // Never interpret markdown inside a fenced code block as structure.
    if (/^\s*```/.test(line)) {
      inFence = !inFence;
      buffer.push(line);
      continue;
    }
    if (inFence) {
      buffer.push(line);
      continue;
    }

    const summary = /^\s*<summary>(.*?)<\/summary>\s*$/i.exec(line);
    if (summary) {
      flush(detailsTitle ?? h3 ?? h2);
      detailsTitle = stripHtmlTags(summary[1]).trim();
      continue;
    }
    if (/^\s*<\/details>\s*$/i.test(line)) {
      flush(null);
      detailsTitle = null;
      continue;
    }
    if (/^\s*<details>\s*$/i.test(line)) continue;

    const heading = /^(#{2,3})\s+(.*)$/.exec(line);
    if (heading) {
      flush(detailsTitle ?? h3 ?? h2);
      detailsTitle = null;
      const text = heading[2].replace(/[*_`]/g, '').trim();
      if (heading[1] === '##') {
        h2 = text;
        h3 = null;
      } else {
        h3 = text;
      }
      continue;
    }

    buffer.push(line);
  }
  flush(detailsTitle ?? h3 ?? h2);

  return sections;
}

/**
 * Split an oversized section at paragraph boundaries.
 *
 * Markdown tables are kept intact: a table split across chunks leaves a body of
 * `| --- |` rows with no header, which is both unreadable in a result and
 * meaningless to embed. Tables carry real answers in this bundle (the role matrix
 * in concepts/roles-and-permissions.md is one), so an over-long table is allowed
 * to exceed MAX_CHUNK_CHARS rather than be broken.
 */
function splitOversized(content: string): string[] {
  if (content.length <= MAX_CHUNK_CHARS) return [content];

  const blocks = content.split(/\n\s*\n/);
  const chunks: string[] = [];
  let current: string[] = [];
  let currentLen = 0;

  for (const block of blocks) {
    const isTable = /^\s*\|/.test(block);
    if (currentLen > 0 && currentLen + block.length > MAX_CHUNK_CHARS && !isTable) {
      chunks.push(current.join('\n\n'));
      current = [];
      currentLen = 0;
    }
    current.push(block);
    currentLen += block.length + 2;
  }
  if (current.length > 0) chunks.push(current.join('\n\n'));

  // A single block can still exceed the budget on its own — the changelog pages
  // are one unbroken bullet list per release with no blank lines, so paragraph
  // splitting cannot touch them. Left whole, everything past the model's ~256
  // tokens would be truncated away and become unsearchable, so fall back to
  // packing whole LINES. Contiguous table rows stay together (see above).
  const final: string[] = [];
  for (const chunk of chunks) {
    if (chunk.length <= MAX_CHUNK_CHARS) {
      final.push(chunk);
      continue;
    }
    let buf: string[] = [];
    let len = 0;
    for (const line of chunk.split('\n')) {
      const isTableRow = /^\s*\|/.test(line);
      if (len > 0 && len + line.length > MAX_CHUNK_CHARS && !isTableRow) {
        final.push(buf.join('\n'));
        buf = [];
        len = 0;
      }
      buf.push(line);
      len += line.length + 1;
    }
    if (buf.length > 0) final.push(buf.join('\n'));
  }

  // Fold a runt tail back into its predecessor.
  if (final.length > 1 && final[final.length - 1].length < MIN_CHUNK_CHARS) {
    const tail = final.pop()!;
    final[final.length - 1] += `\n\n${tail}`;
  }
  return final;
}

// ── File walking ─────────────────────────────────────────────

async function walkMarkdown(root: string, dir = root): Promise<string[]> {
  let entries: Dirent[];
  try {
    entries = await readdir(dir, { withFileTypes: true, encoding: 'utf-8' });
  } catch {
    return [];
  }
  const files: string[] = [];
  for (const entry of entries) {
    if (entry.isDirectory()) {
      if (SKIP_DIRS.has(entry.name)) continue;
      files.push(...(await walkMarkdown(root, join(dir, entry.name))));
    } else if (entry.name.endsWith('.md') && !SKIP_FILES.has(entry.name)) {
      files.push(relative(root, join(dir, entry.name)));
    }
  }
  return files;
}

// ── Public API ───────────────────────────────────────────────

/**
 * Load and chunk the customer documentation bundle at `docsRoot`.
 *
 * Returns an empty array when the directory is absent or holds no markdown —
 * a local checkout without the docs repo is a supported state, and the caller
 * then simply does not register `search_docs`. A missing corpus must never be
 * a boot failure.
 */
export async function loadDocsCorpus(docsRoot: string): Promise<SearchDocument[]> {
  const files = (await walkMarkdown(docsRoot)).sort();
  const docs: SearchDocument[] = [];
  const usedIds = new Set<string>();

  for (const relPath of files) {
    let raw: string;
    try {
      raw = await readFile(join(docsRoot, relPath), 'utf-8');
    } catch {
      continue;
    }

    const { frontmatter, body } = parseFrontmatter(raw);
    const posixPath = relPath.split(sep).join('/');

    // Title: frontmatter wins (the technical bundle carries it), then the page's
    // own h1 (the customer bundle's only source), then the filename.
    const h1 = /^#\s+(.+)$/m.exec(body);
    const title =
      frontmatter.title ??
      h1?.[1].replace(/[*_`]/g, '').trim() ??
      posixPath.replace(/\.md$/, '').split('/').pop()!;

    // Top-level directory doubles as the bundle's section taxonomy
    // (getting-started, concepts, portal-guide, …). Root files are 'general'.
    const section = posixPath.includes('/') ? posixPath.split('/')[0] : 'general';
    const url = docUrl(relPath);

    // Drop the h1 line itself — it is carried in every chunk's header instead.
    const withoutH1 = body.replace(/^#\s+.+$/m, '');
    const sections = splitIntoSections(stripGitbookSyntax(withoutH1));

    for (const sec of sections) {
      for (const piece of splitOversized(sec.content)) {
        const trail = [title, ...sec.breadcrumb].join(' › ');
        // The breadcrumb is prepended to the embedded text so a chunk lifted out
        // of the middle of a page still states which page and section it is from —
        // both for the embedding and for a human reading the tool result.
        if (piece.replace(/[^A-Za-z0-9]/g, '').length < MIN_BODY_CHARS) continue;
        const text = `${trail}\n\n${piece}`.trim();

        const base = `docs:${posixPath}#${slugify(sec.breadcrumb.join('-')) || 'intro'}`;
        let id = base;
        for (let n = 2; usedIds.has(id); n++) id = `${base}-${n}`;
        usedIds.add(id);

        docs.push({
          id,
          text,
          metadata: {
            type: 'doc',
            title,
            section,
            path: posixPath,
            heading: sec.breadcrumb.join(' › ') || null,
            docType: frontmatter.type ?? null,
            tags: frontmatter.tags ?? [],
            url: url ? (sec.anchor ? `${url}#${sec.anchor}` : url) : null,
          },
        });
      }
    }
  }

  return docs;
}

/** Distinct top-level sections present in a loaded corpus — drives the tool's filter enum. */
export function docSections(docs: SearchDocument[]): string[] {
  return [...new Set(docs.map((d) => String(d.metadata.section)))].sort();
}
