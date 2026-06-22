/**
 * Shared test helpers for MCP integration tests.
 *
 * These tests call the real backend API — they require a valid Bearer token
 * provided via the AUTOPILOT_API_TOKEN environment variable.
 *
 * Usage:
 *   AUTOPILOT_API_TOKEN="eyJ..." npm test
 */

import { API_BASE_URL } from '../config.js';

const BASE_URL = API_BASE_URL;
const TOKEN = process.env.AUTOPILOT_API_TOKEN ?? '';

export function getToken(): string {
  if (!TOKEN) {
    throw new Error(
      'AUTOPILOT_API_TOKEN environment variable is required.\n' +
      'Get a token from the browser (F12 → Network → copy Authorization header value) and run:\n' +
      '  AUTOPILOT_API_TOKEN="eyJ..." npm test',
    );
  }
  return TOKEN;
}

export function getBaseUrl(): string {
  return BASE_URL;
}

interface ApiFetchOptions extends RequestInit {
  expectStatus?: number;
}

/**
 * Lightweight fetch wrapper for integration tests.
 * Returns parsed JSON on success, throws on unexpected status.
 */
export async function apiFetch<T = unknown>(path: string, options: ApiFetchOptions = {}): Promise<T> {
  const { expectStatus, ...fetchOpts } = options;
  const url = `${BASE_URL}${path}`;
  const headers: Record<string, string> = {
    'Content-Type': 'application/json',
    'Authorization': `Bearer ${getToken()}`,
    'X-Client-Source': 'mcp-test',
    ...((fetchOpts.headers as Record<string, string>) ?? {}),
  };

  const res = await fetch(url, { ...fetchOpts, headers, signal: AbortSignal.timeout(25_000) });

  if (expectStatus !== undefined) {
    if (res.status !== expectStatus) {
      const body = await res.text().catch(() => '(no body)');
      throw new Error(`Expected status ${expectStatus}, got ${res.status}: ${body.slice(0, 300)}`);
    }
    // For non-2xx expected statuses, try to parse the JSON body anyway
    const text = await res.text();
    try {
      return JSON.parse(text) as T;
    } catch {
      return text as unknown as T;
    }
  }

  if (!res.ok) {
    const body = await res.text().catch(() => '(no body)');
    throw new Error(`API ${res.status}: ${body.slice(0, 500)}`);
  }

  return res.json() as Promise<T>;
}

/** Build query string from params, skipping null/undefined values. */
export function buildQuery(params: Record<string, string | number | boolean | undefined | null>): string {
  const p = new URLSearchParams();
  for (const [k, v] of Object.entries(params)) {
    if (v != null && v !== '') p.set(k, String(v));
  }
  const s = p.toString();
  return s ? `?${s}` : '';
}
