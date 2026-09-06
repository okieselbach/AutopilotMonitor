/**
 * The error-code catalog module: loading the Shared resource, every input form of
 * lookupErrorCode (hex, bare hex, signed/unsigned decimal, MSI exit code, symbol,
 * enforcement state), the HRESULT_FROM_WIN32 derivation, and the lexical documents the
 * search_knowledge fallback scans. Pure unit tests over the committed catalog file.
 */
import { describe, it, expect } from 'vitest';
import { mkdtempSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import {
  DEFAULT_ERROR_CODES_PATH,
  getErrorCodeCatalog,
  readErrorCodeCatalog,
  lookupErrorCode,
  errorCodeSearchDocuments,
} from '../error-code-catalog.js';
import { scanLexical } from '../search-provider.js';

describe('error-code catalog loading', () => {
  it('loads the schemaVersion-2 Shared resource with symbol and state indexes', () => {
    const catalog = getErrorCodeCatalog();
    expect(DEFAULT_ERROR_CODES_PATH).toMatch(/error-codes\.json$/);
    expect(catalog.size).toBeGreaterThan(400);
    expect(catalog.enforcementStates.size).toBeGreaterThan(40);
    expect(catalog.bySymbol.get('error_install_failure')).toBe('1603');
    expect(catalog.statesByName.get('success')?.value).toBe('1000');
  });

  it('fails loudly on a missing file or a foreign schema version', () => {
    expect(() => readErrorCodeCatalog(join(tmpdir(), 'does-not-exist-error-codes.json'))).toThrow();
    const dir = mkdtempSync(join(tmpdir(), 'ecc-'));
    const v1 = join(dir, 'error-codes.json');
    writeFileSync(v1, JSON.stringify({ schemaVersion: 1, entries: { '1603': { description: 'x', confidence: 'high', source: 'MS Learn Docs' } } }));
    expect(() => readErrorCodeCatalog(v1)).toThrow(/schemaVersion 1, expected 2/);
  });
});

describe('lookupErrorCode', () => {
  it('resolves hex in any spelling', () => {
    for (const input of ['0x87D30067', '0x87d30067', '87D30067', '0X87D30067']) {
      const r = lookupErrorCode(input);
      expect(r.found, input).toBe(true);
      if (r.found && 'symbol' in r) {
        expect(r.key).toBe('0x87d30067');
        expect(r.normalizedHex).toBe('0x87d30067');
        expect(r.decimal).toBe(-2016214937);
        expect(r.symbol).toBe('UnzipError');
        expect(r.category).toBe('intune-win32');
        expect(r.confidence).toBe('medium');
        expect(r.source).toMatch(/^ime:/);
      }
    }
  });

  it('resolves the signed decimal the IME logs print, and the unsigned form', () => {
    const signed = lookupErrorCode('-2016214937');
    expect(signed.found && 'key' in signed && signed.key).toBe('0x87d30067');
    const unsigned = lookupErrorCode('2278752359');
    expect(unsigned.found && 'key' in unsigned && unsigned.key).toBe('0x87d30067');
  });

  it('resolves an MSI exit code and reports its HRESULT_FROM_WIN32 form', () => {
    const r = lookupErrorCode('1603');
    expect(r.found).toBe(true);
    if (r.found && 'symbol' in r) {
      expect(r.symbol).toBe('ERROR_INSTALL_FAILURE');
      expect(r.normalizedHex).toBe('0x80070643');
      expect(r.decimal).toBe(1603);
      expect(r.category).toBe('msi');
      expect(r.derivedFromWin32).toBeUndefined();
    }
  });

  it('derives the MSI entry from a 0x8007xxxx value and says so', () => {
    const r = lookupErrorCode('0x80070643');
    expect(r.found).toBe(true);
    if (r.found && 'symbol' in r) {
      expect(r.key).toBe('1603');
      expect(r.derivedFromWin32).toBe(1603);
      expect(r.description).not.toContain('1603');
    }
    expect(lookupErrorCode('0x80070000').found).toBe(false);
    const direct = lookupErrorCode('0x80070005');
    expect(direct.found && 'derivedFromWin32' in direct ? direct.derivedFromWin32 : undefined).toBeUndefined();
  });

  it('resolves symbols case-insensitively', () => {
    const r = lookupErrorCode('error_install_failure');
    expect(r.found && 'key' in r && r.key).toBe('1603');
    expect(lookupErrorCode('WU_E_ALL_UPDATES_FAILED').found).toBe(true);
  });

  it('resolves enforcement states by name and by number', () => {
    const byName = lookupErrorCode('NotAttemptedDependencyWithFailure');
    expect(byName.found && 'enforcementState' in byName && byName.enforcementState.value).toBe('6001');
    const byNumber = lookupErrorCode('6001');
    expect(byNumber.found && 'enforcementState' in byNumber && byNumber.enforcementState.name).toBe('NotAttemptedDependencyWithFailure');
  });

  it('flags the IME MSI retry list', () => {
    const r = lookupErrorCode('0x80070005');
    expect(r.found && 'imeRetriesDuringEsp' in r && r.imeRetriesDuringEsp).toBe(true);
  });

  it('answers a structured miss', () => {
    const unknown = lookupErrorCode('0xDEADBEEF');
    expect(unknown).toMatchObject({ found: false, input: '0xDEADBEEF', normalizedHex: '0xdeadbeef' });
    expect(lookupErrorCode('NOT_A_SYMBOL').found).toBe(false);
    expect(lookupErrorCode('42').found).toBe(false);   // installer-defined exit code
    expect(lookupErrorCode('').found).toBe(false);
    expect(lookupErrorCode('hello world').found).toBe(false);
  });
});

describe('errorCodeSearchDocuments', () => {
  it('yields one lexical document per entry, typed error-code, matchable by a hex needle', () => {
    const docs = errorCodeSearchDocuments();
    expect(docs.length).toBe(getErrorCodeCatalog().size);
    expect(docs.every((d) => d.metadata.type === 'error-code')).toBe(true);
    const hits = scanLexical(docs, ['80d02002']);
    expect(hits.map((h) => h.id)).toContain('error-code:0x80d02002');
    expect(String(hits[0].metadata.title)).toMatch(/^0x80D02002/);
  });
});
