/**
 * The per-argument echo policy a tool declares at its withToolTelemetry call site governs BOTH
 * sinks that quote the arguments: the tool_call log line and the "Parameters used" block of an
 * error result (whose first lines the log quotes). Without this, a clear-text config value masked
 * in the args summary still reached the container log through the error text.
 */
import { describe, it, expect } from 'vitest';
import { toolError } from '../tools/error-handler.js';
import { createToolCallContext, runWithToolCallContext } from '../client.js';

const TENANT = 'a1b2c3d4-e5f6-7890-abcd-ef1234567890';
const SECRET = 'https://hooks.example/services/secret-token-123';

function errorText(result: unknown): string {
  return (result as { content: Array<{ text: string }> }).content[0].text;
}

describe('toolError — parameter echo honours the call\'s ArgPolicy', () => {
  it("renders only the field NAMES of a 'keys' argument, so no config value enters the error text", () => {
    const args = { tenantId: TENANT, fields: { teamsWebhookUrl: SECRET, dataRetentionDays: 4242 }, reason: 'rotate' };
    const result = runWithToolCallContext(
      createToolCallContext('update_tenant_config', 'cid', { fields: 'keys' }),
      () => toolError('update_tenant_config', args, new Error('boom')),
    );
    const text = errorText(result);
    expect(text).toContain('**Parameters used**');
    expect(text).toContain('fields: teamsWebhookUrl,dataRetentionDays');
    expect(text).toContain('reason: rotate');
    expect(text).not.toContain(SECRET);
    expect(text).not.toContain('4242');
  });

  it("omits a 'drop' argument entirely", () => {
    const result = runWithToolCallContext(
      createToolCallContext('t', 'cid', { secret: 'drop' }),
      () => toolError('t', { secret: 'hunter2', q: 'x' }, new Error('boom')),
    );
    expect(errorText(result)).not.toContain('hunter2');
    expect(errorText(result)).not.toContain('secret');
    expect(errorText(result)).toContain('q: x');
  });

  it('renders values verbatim (size-capped) without a policy — the pre-existing behaviour', () => {
    const text = errorText(toolError('search_sessions', { status: 'failed', pageSize: 50, days: null }, new Error('boom')));
    expect(text).toContain('status: failed');
    expect(text).toContain('pageSize: 50');
    expect(text).not.toContain('days');
  });
});
