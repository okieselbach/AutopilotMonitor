/**
 * Pure functions behind scripts/whats-new/build-whats-new.js — no I/O, so the
 * markdown → payload transformation is unit-tested in isolation.
 *
 * The two customer changelogs in the docs repo (changelog/platform-changelog.md,
 * changelog/agent-changelog.md) are the ONLY authoritative source of "What's new".
 * This module reads exactly the authoring format the `/changelog` skill produces —
 * `## <Period>` blocks with `* ` bullets — and turns it into the JSON the portal
 * panel renders. Nothing here adds meaning the markdown does not carry; the only
 * derived facts are the per-bullet publish date (from `git blame`, supplied by the
 * caller), the absolute docs URL of every relative link, and the order of the bullets
 * within a period.
 *
 * Ordering: the changelogs are authored newest-first, but related bullets are grouped by
 * topic, so a new entry can end up below older ones in the file. The panel counts unseen
 * entries by date, so a reader could not find the bullet the counter meant. The payload
 * therefore sorts each period's bullets by publish date, newest first — every unseen entry
 * sits at the top of its period and the badge is countable. The docs page keeps its
 * authored order; it shows no dates.
 */

const SCHEMA_VERSION = 1;

/** Newest blocks per channel that make it into the payload. */
const MAX_PERIODS = 3;
/** Hard cap per channel so a busy quarter cannot balloon the file. */
const MAX_ENTRIES = 60;

const CHANNEL_FILES = {
  platform: "changelog/platform-changelog.md",
  agent: "changelog/agent-changelog.md",
};

// ── Markdown ─────────────────────────────────────────────────

/** CRLF/CR → LF. The docs repo is authored on Windows; `.` never matches `\r`. */
function normalizeNewlines(text) {
  return text.replace(/\r\n?/g, "\n");
}

/**
 * @typedef {{ text: string, line: number }} Bullet
 * @typedef {{ period: string, bullets: Bullet[] }} Block
 * @typedef {{ id: string, addedUtc: string, period: string, title: string | null, body: string, link: string | null }} Entry
 * @typedef {{ docsUrl: string, entries: Entry[] }} ChannelData
 */

/**
 * Parses one changelog file into `{ period, bullets: [{ text, line }] }[]` in file
 * order (newest first, as the authoring convention keeps it). `line` is the 1-based
 * line number of the bullet's first line in the ORIGINAL file — the key `git blame`
 * dates are looked up by. Continuation lines (indented, not a new bullet) are folded
 * into the bullet with a single space.
 *
 * Everything before the first `## ` heading (frontmatter, intro, badges) is skipped.
 * @returns {Block[]}
 */
function parseChangelog(markdown) {
  const lines = normalizeNewlines(markdown).split("\n");
  /** @type {Block[]} */
  const blocks = [];
  /** @type {Block | null} */
  let current = null;
  /** @type {Bullet | null} */
  let bullet = null;

  const flushBullet = () => {
    if (bullet && current) current.bullets.push(bullet);
    bullet = null;
  };

  for (let i = 0; i < lines.length; i++) {
    const raw = lines[i];
    const heading = /^##\s+(.+?)\s*$/.exec(raw);
    if (heading) {
      flushBullet();
      current = { period: heading[1], bullets: [] };
      blocks.push(current);
      continue;
    }
    if (!current) continue;

    const item = /^\*\s+(.*)$/.exec(raw);
    if (item) {
      flushBullet();
      bullet = { text: item[1].trim(), line: i + 1 };
      continue;
    }
    if (bullet && /^\s+\S/.test(raw)) {
      bullet.text += " " + raw.trim();
      continue;
    }
    flushBullet();
  }
  flushBullet();
  return blocks;
}

/**
 * Platform bullets lead with a bold title and an em dash: `**Title** — Body`.
 * Agent bullets are a single sentence. Returns `{ title, body }`, title null when
 * the bullet has no bold lead.
 */
function splitTitle(text) {
  const m = /^\*\*(.+?)\*\*\s*(?:—|–|-)\s*(.*)$/s.exec(text);
  if (m) return { title: m[1].trim(), body: m[2].trim() };
  return { title: null, body: text.trim() };
}

// ── GitBook URL map ──────────────────────────────────────────

/**
 * GitBook publishes a page under `<group-slug>/<parent-slugs…>/<page-slug>`, where the
 * group slug is the slugified `## ` heading of SUMMARY.md (not the folder name:
 * `troubleshooting/` → `troubleshooting-and-support`), a page slug is its file name
 * without `.md`, and a `README.md` takes its folder's name. `docsPaths.ts` in the
 * portal pins the same shape.
 */
function slugifyHeading(heading) {
  return heading
    .toLowerCase()
    .replace(/&/g, " and ")
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");
}

function pageSlug(filePath) {
  const parts = filePath.split("/");
  const file = parts[parts.length - 1];
  if (/^readme\.md$/i.test(file)) return parts.length >= 2 ? parts[parts.length - 2] : "";
  return file.replace(/\.md$/i, "");
}

/**
 * SUMMARY.md → Map<repo-relative file path, url path>. Root pages (before the first
 * group) map to `/<slug>`, `README.md` to `/`. Nesting follows the list indentation:
 * a deeper bullet is published under its parent's URL.
 */
function buildSummaryUrlMap(summaryMarkdown) {
  const map = new Map();
  let groupSlug = null;
  // Slugs of the ancestors at each indentation depth for the current group.
  const stack = [];

  for (const raw of normalizeNewlines(summaryMarkdown).split("\n")) {
    const heading = /^##\s+(.+?)\s*$/.exec(raw);
    if (heading) {
      groupSlug = slugifyHeading(heading[1]);
      stack.length = 0;
      continue;
    }
    const item = /^(\s*)\*\s+\[[^\]]*\]\(([^)]+)\)/.exec(raw);
    if (!item) continue;
    const depth = Math.floor(item[1].replace(/\t/g, "  ").length / 2);
    const file = item[2].trim();
    const slug = pageSlug(file);
    stack.length = depth;
    stack[depth] = slug;

    let url;
    if (groupSlug === null) {
      url = /^readme\.md$/i.test(file) ? "/" : `/${slug}`;
    } else {
      url = "/" + [groupSlug, ...stack.slice(0, depth + 1)].join("/");
    }
    map.set(file, url);
  }
  return map;
}

/** Folder-name → group-slug fallback for pages SUMMARY.md does not list. */
function buildFolderSlugMap(urlMap) {
  const folders = new Map();
  for (const [file, url] of urlMap) {
    const slash = file.indexOf("/");
    if (slash < 0) continue;
    const folder = file.slice(0, slash);
    const group = url.split("/")[1];
    if (folder && group && !folders.has(folder)) folders.set(folder, group);
  }
  return folders;
}

/** Resolves `../a/b.md` against `changelog/` without touching the filesystem. */
function resolveRelative(fromDir, href) {
  const parts = (fromDir ? fromDir.split("/") : []).filter(Boolean);
  for (const seg of href.split("/")) {
    if (seg === "..") parts.pop();
    else if (seg && seg !== ".") parts.push(seg);
  }
  return parts.join("/");
}

/**
 * Turns one markdown link target from a changelog bullet into an absolute URL, or
 * null when the target is neither an https URL nor a known docs page. `#anchor`
 * is carried over verbatim (GitBook heading slugs match the markdown anchors).
 */
function resolveLink(href, ctx) {
  const trimmed = href.trim();
  if (/^https:\/\//i.test(trimmed)) return trimmed;
  if (/^[a-z][a-z0-9+.-]*:/i.test(trimmed)) return null; // http:, mailto:, etc.

  const hashIndex = trimmed.indexOf("#");
  const pathPart = hashIndex >= 0 ? trimmed.slice(0, hashIndex) : trimmed;
  const anchor = hashIndex >= 0 ? trimmed.slice(hashIndex) : "";
  if (!pathPart) return null; // same-page anchor — meaningless outside the docs page

  const file = resolveRelative(ctx.fromDir, pathPart);
  let urlPath = ctx.urlMap.get(file);
  if (!urlPath) {
    const slash = file.indexOf("/");
    if (slash < 0) return null;
    const group = ctx.folderSlugMap.get(file.slice(0, slash));
    if (!group) return null;
    const rest = file
      .slice(slash + 1)
      .replace(/\/?readme\.md$/i, "")
      .replace(/\.md$/i, "");
    urlPath = "/" + [group, rest].filter(Boolean).join("/");
  }
  return `${ctx.docsUrl}${urlPath}${anchor}`;
}

/**
 * Rewrites every `[label](target)` in the bullet to an absolute URL; links that
 * cannot be resolved lose their brackets and keep the label. Returns the rewritten
 * text and the first resolved URL (the panel's "Read update" target).
 */
function rewriteLinks(text, ctx) {
  let first = null;
  const rewritten = text.replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, (_m, label, target) => {
    const url = resolveLink(target, ctx);
    if (!url) return label;
    if (!first) first = url;
    return `[${label}](${url})`;
  });
  return { text: rewritten, link: first };
}

// ── Blame ────────────────────────────────────────────────────

/**
 * `git blame --line-porcelain` → Map<1-based line, epoch seconds (author-time)>.
 * Uncommitted lines carry the all-zero hash and no usable time; they map to null.
 */
function parseBlamePorcelain(output) {
  const map = new Map();
  let line = null;
  let time = null;
  let uncommitted = false;
  for (const raw of normalizeNewlines(output).split("\n")) {
    const header = /^([0-9a-f]{40}) \d+ (\d+)(?: \d+)?$/.exec(raw);
    if (header) {
      line = Number(header[2]);
      time = null;
      uncommitted = /^0+$/.test(header[1]);
      continue;
    }
    if (line === null) continue;
    const at = /^author-time (\d+)$/.exec(raw);
    if (at) {
      time = Number(at[1]);
      continue;
    }
    if (raw.startsWith("\t")) {
      map.set(line, uncommitted ? null : time);
      line = null;
    }
  }
  return map;
}

// ── Payload ──────────────────────────────────────────────────

/** FNV-1a 32-bit over the normalized text — a stable React key across regenerations. */
function stableId(text) {
  let h = 0x811c9dc5;
  for (let i = 0; i < text.length; i++) {
    h ^= text.charCodeAt(i);
    h = Math.imul(h, 0x01000193) >>> 0;
  }
  return h.toString(16).padStart(8, "0");
}

function toIso(epochSeconds, fallbackIso) {
  if (epochSeconds === null || epochSeconds === undefined) return fallbackIso;
  return new Date(epochSeconds * 1000).toISOString();
}

/**
 * Builds one channel's entry list from markdown + blame dates.
 * `nowIso` dates bullets git has not committed yet (local runs on a dirty tree).
 * Period blocks keep their file order; the bullets inside one block are ordered by
 * publish date, newest first — see the ordering note at the top of this file.
 * @returns {Entry[]}
 */
function buildChannelEntries(markdown, blameByLine, ctx, nowIso) {
  const blocks = parseChangelog(markdown).slice(0, MAX_PERIODS);
  /** @type {Entry[]} */
  const entries = [];
  for (const block of blocks) {
    /** @type {Entry[]} */
    const blockEntries = [];
    for (const bullet of block.bullets) {
      const { title, body } = splitTitle(bullet.text);
      const { text, link } = rewriteLinks(body, ctx);
      blockEntries.push({
        id: stableId(`${block.period}|${bullet.text}`),
        addedUtc: toIso(blameByLine.get(bullet.line), nowIso),
        period: block.period,
        title,
        body: text,
        link,
      });
    }
    // Newest first inside the period; ties keep the authored order, so the bullets of one
    // commit stay in the sequence they were written in.
    blockEntries.sort((a, b) => Date.parse(b.addedUtc) - Date.parse(a.addedUtc));
    for (const entry of blockEntries) {
      if (entries.length >= MAX_ENTRIES) return entries;
      entries.push(entry);
    }
  }
  return entries;
}

/**
 * @param input.docsUrl        e.g. "https://docs.autopilotmonitor.com"
 * @param input.docsCommit     short SHA of the docs checkout, or null
 * @param input.summary        SUMMARY.md text
 * @param input.channels       { platform: { markdown, blame }, agent: { markdown, blame } }
 *                             where `blame` is the raw `git blame --line-porcelain` output
 * @param input.now            Date used for generatedUtc and uncommitted bullets
 */
function buildPayload(input) {
  const now = input.now ?? new Date();
  const nowIso = now.toISOString();
  const urlMap = buildSummaryUrlMap(input.summary);
  const ctx = {
    docsUrl: input.docsUrl.replace(/\/+$/, ""),
    fromDir: "changelog",
    urlMap,
    folderSlugMap: buildFolderSlugMap(urlMap),
  };

  /** @param {"platform" | "agent"} channel @returns {ChannelData} */
  const buildChannel = channel => {
    const source = input.channels[channel];
    const file = CHANNEL_FILES[channel];
    return {
      docsUrl: `${ctx.docsUrl}${urlMap.get(file) ?? "/" + file.replace(/\.md$/i, "")}`,
      entries: buildChannelEntries(source.markdown, parseBlamePorcelain(source.blame), ctx, nowIso),
    };
  };

  return {
    schemaVersion: SCHEMA_VERSION,
    generatedUtc: nowIso,
    docsCommit: input.docsCommit ?? null,
    channels: { platform: buildChannel("platform"), agent: buildChannel("agent") },
  };
}

/** The payload a build without a docs checkout ships: valid shape, no entries. */
function emptyPayload(docsUrl, now = new Date()) {
  const base = docsUrl.replace(/\/+$/, "");
  return {
    schemaVersion: SCHEMA_VERSION,
    generatedUtc: now.toISOString(),
    docsCommit: null,
    channels: {
      platform: { docsUrl: `${base}/changelog/platform-changelog`, entries: [] },
      agent: { docsUrl: `${base}/changelog/agent-changelog`, entries: [] },
    },
  };
}

module.exports = {
  SCHEMA_VERSION,
  CHANNEL_FILES,
  normalizeNewlines,
  parseChangelog,
  splitTitle,
  slugifyHeading,
  buildSummaryUrlMap,
  resolveLink,
  rewriteLinks,
  parseBlamePorcelain,
  stableId,
  buildPayload,
  emptyPayload,
};
