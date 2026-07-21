/**
 * Unit tests for the customer-documentation corpus loader.
 *
 * The bundle is authored on Windows and ships CRLF, which is the single most
 * dangerous property of this input: in JavaScript `.` does not match `\r`, so a
 * pattern like /^(#{2,3})\s+(.*)$/ silently matches NOTHING on a CRLF file. The
 * failure is not a crash — headings and frontmatter just stop being recognized
 * and every page degrades to unlabelled paragraph splits. Several tests below
 * therefore run the CRLF form on purpose.
 */
import { describe, it, expect } from 'vitest';
import { mkdtemp, mkdir, writeFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { loadDocsCorpus, parseFrontmatter, docUrl, slugify, docSections } from '../docs-corpus.js';

async function withBundle<T>(
  files: Record<string, string>,
  fn: (root: string) => Promise<T>,
): Promise<T> {
  const root = await mkdtemp(join(tmpdir(), 'docs-corpus-'));
  try {
    for (const [rel, content] of Object.entries(files)) {
      const full = join(root, rel);
      await mkdir(join(full, '..'), { recursive: true });
      await writeFile(full, content, 'utf-8');
    }
    return await fn(root);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
}

describe('parseFrontmatter', () => {
  it('reads scalars, folded blocks and inline sequences', () => {
    const { frontmatter, body } = parseFrontmatter(
      ['---', 'type: Concept', 'tags: [security, gdpr]', 'description: >-', '  First line', '  second line', '---', '', '# Title', 'Body.'].join('\n'),
    );
    expect(frontmatter.type).toBe('Concept');
    expect(frontmatter.tags).toEqual(['security', 'gdpr']);
    expect(frontmatter.description).toBe('First line second line');
    expect(body).toContain('# Title');
    expect(body).not.toContain('type: Concept');
  });

  it('reads block sequences', () => {
    const { frontmatter } = parseFrontmatter(['---', 'type: Guide', 'tags:', '  - agent', '  - intune', '---', 'x'].join('\n'));
    expect(frontmatter.tags).toEqual(['agent', 'intune']);
  });

  it('parses CRLF frontmatter identically to LF', () => {
    const lf = ['---', 'type: Concept', 'tags: [a, b]', '---', '', '# T', 'x'].join('\n');
    expect(parseFrontmatter(lf.replace(/\n/g, '\r\n'))).toEqual(parseFrontmatter(lf));
  });

  it('returns the whole input as body when there is no frontmatter', () => {
    const { frontmatter, body } = parseFrontmatter('# Just a title\n\nText.');
    expect(frontmatter).toEqual({ type: undefined, title: undefined, description: undefined, tags: undefined });
    expect(body).toBe('# Just a title\n\nText.');
  });
});

describe('docUrl', () => {
  it('maps a page path to its published URL', () => {
    expect(docUrl('trust/security-faq.md')).toBe('https://docs.autopilotmonitor.com/trust/security-faq');
  });

  it('collapses README.md to its folder', () => {
    expect(docUrl('rules/analyze-rules/README.md')).toBe('https://docs.autopilotmonitor.com/rules/analyze-rules');
    expect(docUrl('README.md')).toBe('https://docs.autopilotmonitor.com');
  });

  it('has no URL for index.md — GitBook never publishes the OKF catalog', () => {
    expect(docUrl('index.md')).toBeUndefined();
  });
});

describe('slugify', () => {
  it('strips markdown and punctuation', () => {
    expect(slugify('Is the device\'s **IP address** stored?')).toBe('is-the-device-s-ip-address-stored');
  });
});

describe('loadDocsCorpus', () => {
  const page = (fm: string, body: string) => `---\n${fm}\n---\n\n${body}`;

  it('splits on h2/h3 headings and carries a breadcrumb', async () => {
    const chunks = await withBundle(
      {
        'concepts/roles.md': page(
          'type: Concept\ntags: [roles]',
          ['# Roles & Permissions', '', 'Intro prose that is long enough to survive the minimum-length filter for chunks.', '', '## Tenant roles', '', 'A tenant admin can do everything, including managing team members and enabling admin mode.', '', '### Operator', '', 'An operator monitors day to day and cannot perform destructive operations at all.'].join('\n'),
        ),
      },
      (root) => loadDocsCorpus(root),
    );

    const headings = chunks.map((c) => c.metadata.heading);
    expect(headings).toContain('Tenant roles');
    expect(headings).toContain('Tenant roles › Operator');
    // The page title is prepended to every chunk so a mid-page hit is self-describing.
    expect(chunks.every((c) => c.text.startsWith('Roles & Permissions'))).toBe(true);
    expect(chunks[0].metadata.section).toBe('concepts');
    expect(chunks[0].metadata.tags).toEqual(['roles']);
  });

  it('produces the SAME chunks for CRLF input as for LF', async () => {
    const body = ['# Guide', '', 'Intro paragraph long enough to be kept as its own indexed chunk here.', '', '## Step one', '', 'Do the first thing, which needs a sentence long enough to pass the minimum.', '', '## Step two', '', 'Then do the second thing, also written out at a reasonable length for this.'].join('\n');
    const src = page('type: Guide\ntags: [setup]', body);

    const lf = await withBundle({ 'getting-started/g.md': src }, (r) => loadDocsCorpus(r));
    const crlf = await withBundle({ 'getting-started/g.md': src.replace(/\n/g, '\r\n') }, (r) => loadDocsCorpus(r));

    expect(crlf).toEqual(lf);
    expect(lf.map((c) => c.metadata.heading)).toEqual([null, 'Step one', 'Step two']);
    expect(lf.some((c) => c.text.includes('\r'))).toBe(false);
  });

  it('treats <details>/<summary> blocks as sections', async () => {
    const chunks = await withBundle(
      {
        'troubleshooting/faq.md': page(
          'type: FAQ\ntags: [faq]',
          ['# FAQ', '', '## General', '', '<details>', '', '<summary>Is Autopilot Monitor free?</summary>', '', 'Yes, the Community plan is free and stays free for everyone who wants to use it.', '', '</details>'].join('\n'),
        ),
      },
      (root) => loadDocsCorpus(root),
    );
    expect(chunks.map((c) => c.metadata.heading)).toContain('General › Is Autopilot Monitor free?');
  });

  it('strips inline HTML tags from a <summary> title, leaving no angle brackets', async () => {
    const chunks = await withBundle(
      {
        'troubleshooting/faq.md': page(
          'type: FAQ\ntags: [faq]',
          ['# FAQ', '', '## General', '', '<details>', '', '<summary>Does <b>keep-awake</b> work during <i>ESP</i>?</summary>', '', 'Keep-awake is an opt-in behavior during the user ESP phase for supported devices.', '', '</details>'].join('\n'),
        ),
      },
      (root) => loadDocsCorpus(root),
    );
    const headings = chunks.map((c) => c.metadata.heading);
    expect(headings).toContain('General › Does keep-awake work during ESP?');
    expect(headings.every((h) => h == null || (!h.includes('<') && !h.includes('>')))).toBe(true);
  });

  it('never emits a heading from inside a fenced code block', async () => {
    const chunks = await withBundle(
      {
        'reference/cli.md': page(
          'type: Reference\ntags: [cli]',
          ['# CLI', '', '## Usage', '', '```powershell', '# This is a shell comment, not a heading', '## Neither is this', '```', '', 'Explanatory prose after the block so the section is comfortably long enough.'].join('\n'),
        ),
      },
      (root) => loadDocsCorpus(root),
    );
    expect(chunks.map((c) => c.metadata.heading)).toEqual(['Usage']);
  });

  it('skips SUMMARY.md and log.md but keeps index.md', async () => {
    const chunks = await withBundle(
      {
        'SUMMARY.md': '# Table of contents\n\n* [A](a.md)\n',
        'log.md': '# Log\n\nSome change record entry that is long enough to be a chunk on its own.\n',
        'index.md': page('okf_version: "0.1"', '# Bundle\n\nThe documentation map listing every page available in this bundle here.'),
      },
      (root) => loadDocsCorpus(root),
    );
    const paths = [...new Set(chunks.map((c) => c.metadata.path))];
    expect(paths).toEqual(['index.md']);
    // index.md is indexed for its content but is not a published page.
    expect(chunks[0].metadata.url).toBeNull();
  });

  it('splits an oversized unbroken list so its tail is not truncated away', async () => {
    const items = Array.from({ length: 60 }, (_, i) => `* Release note number ${i} describing a change that shipped in this release.`);
    const chunks = await withBundle(
      { 'changelog/platform.md': page('type: Changelog\ntags: [changelog]', ['# Platform Changelog', '', '## July 2026', '', ...items].join('\n')) },
      (root) => loadDocsCorpus(root),
    );
    expect(chunks.length).toBeGreaterThan(1);
    expect(chunks.every((c) => c.text.length <= 1700)).toBe(true);
    // The final item must still be reachable — that is the whole point of splitting.
    expect(chunks.some((c) => c.text.includes('number 59'))).toBe(true);
  });

  it('keeps a markdown table intact rather than splitting mid-table', async () => {
    const rows = Array.from({ length: 40 }, (_, i) => `| Setting ${i} | A reasonably wordy description of what this particular setting controls. |`);
    const chunks = await withBundle(
      { 'reference/settings.md': page('type: Reference\ntags: [settings]', ['# Settings', '', '## All settings', '', '| Name | Description |', '| --- | --- |', ...rows].join('\n')) },
      (root) => loadDocsCorpus(root),
    );
    const withTable = chunks.filter((c) => c.text.includes('| Setting '));
    expect(withTable).toHaveLength(1);
    expect(withTable[0].text).toContain('| Setting 39 |');
  });

  it('assigns unique, stable ids across repeated loads', async () => {
    const files = {
      'a/one.md': page('type: Guide\ntags: [x]', '# One\n\n## Same\n\nFirst body text that is long enough to be indexed as a chunk.\n\n## Same\n\nSecond body text that is also long enough to be indexed here.'),
    };
    const first = await withBundle(files, (r) => loadDocsCorpus(r));
    const second = await withBundle(files, (r) => loadDocsCorpus(r));
    expect(new Set(first.map((c) => c.id)).size).toBe(first.length);
    expect(first.map((c) => c.id)).toEqual(second.map((c) => c.id));
  });

  it('returns an empty corpus for a missing directory instead of throwing', async () => {
    await expect(loadDocsCorpus(join(tmpdir(), 'definitely-not-here-9d2f'))).resolves.toEqual([]);
  });
});

describe('docSections', () => {
  it('lists distinct top-level areas, sorted', async () => {
    const chunks = await withBundle(
      {
        'trust/a.md': '# A\n\nSome prose that is definitely long enough to become an indexed chunk.',
        'concepts/b.md': '# B\n\nMore prose that is definitely long enough to become an indexed chunk.',
        'root.md': '# R\n\nRoot level prose that is long enough to become an indexed chunk too.',
      },
      (root) => loadDocsCorpus(root),
    );
    expect(docSections(chunks)).toEqual(['concepts', 'general', 'trust']);
  });
});
