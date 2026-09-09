/**
 * revert_tenant_config takes an explicit backupId — no "latest" default. Every revert snapshots
 * the current state first, so a retried revert without an id would restore the snapshot the
 * previous revert had just created and undo it. With a fixed id the same call is a no-op twice.
 */
import { describe, it, expect, vi } from 'vitest';
import { z } from 'zod';

vi.mock('../client.js', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../client.js')>()),
  apiFetch: vi.fn(),
}));

import { registerAdminTools } from '../tools/admin.js';

interface ToolDef { description: string; inputSchema: Record<string, z.ZodTypeAny> }

function definitionOf(tool: string): ToolDef {
  let found: ToolDef | undefined;
  const fake = { registerTool: (name: string, def: ToolDef) => { if (name === tool) found = def; } };
  registerAdminTools(fake as never, true, true, false);
  if (!found) throw new Error(`${tool} not registered`);
  return found;
}

const TENANT = 'a1b2c3d4-e5f6-7890-abcd-ef1234567890';

describe('revert_tenant_config — backupId is required', () => {
  it('rejects a revert without backupId or with an empty one; accepts a named snapshot', () => {
    const schema = z.object(definitionOf('revert_tenant_config').inputSchema);
    expect(schema.safeParse({ tenantId: TENANT, reason: 'undo' }).success).toBe(false);
    expect(schema.safeParse({ tenantId: TENANT, reason: 'undo', backupId: '' }).success).toBe(false);
    expect(schema.safeParse({ tenantId: TENANT, reason: 'undo', backupId: '20260910T000000Z_abcd1234' }).success).toBe(true);
  });

  it('explains the rule in the description and no longer advertises a latest default', () => {
    const { description } = definitionOf('revert_tenant_config');
    expect(description).toContain('backupId is required');
    expect(description).not.toContain('latest by default');
  });
});
