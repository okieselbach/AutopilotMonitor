#!/usr/bin/env node
/**
 * Seed the three Table Storage rows a brand-new self-hosted platform needs before anyone can
 * use the portal. Reconstructed from the backend source (GlobalAdminService, AdminIdentityBindingService,
 * TableConfigRepository.ConvertToAdminTableEntity), because upstream's own seed tooling lives in
 * the private infra repo.
 *
 *   1. GlobalAdmins / <upn>               : the platform operator (you). Chicken-and-egg otherwise:
 *                                           "add a Global Admin" is itself a Global-Admin-only API.
 *   2. AdminIdentityBindings / <upn>      : pins the UPN to your home tenant (and object id). Without it
 *                                           the backend refuses the cross-tenant role (token tid/oid check).
 *   3. AdminConfiguration / GlobalConfig,config : AutoApproveNewTenants=true so your own tenant is
 *                                           activated on first sign-in instead of waiting for approval.
 *                                           Merge only: every other column keeps its code default.
 *
 * Auth: DefaultAzureCredential (run `az login` first). Needs "Storage Table Data Contributor" on the
 * data account (granted to the deployer by bicep param deployerObjectId). Shared-key access is OFF.
 *
 *   node 03-seed-platform.mjs --account stautopilotmonitorprod \
 *        --upn admin@clefsoft.co.uk --tenant-id <home-tenant-guid> --object-id <your-user-object-id>
 *
 * Test mode: --connection-string "UseDevelopmentStorage=true" (Azurite).
 * Add --dry-run to print what would be written.
 */
import { TableClient, TableServiceClient } from '@azure/data-tables';
import { DefaultAzureCredential } from '@azure/identity';

const argv = process.argv.slice(2);
const opt = (n) => { const i = argv.indexOf(`--${n}`); return i >= 0 ? argv[i + 1] : undefined; };
const flag = (n) => argv.includes(`--${n}`);

const account = opt('account');
const conn = opt('connection-string');
const upn = opt('upn')?.trim().toLowerCase();
const tenantId = opt('tenant-id')?.trim().toLowerCase();
const objectId = opt('object-id')?.trim().toLowerCase();
const addedBy = 'Initial Setup';
const dry = flag('dry-run');
const autoApprove = !flag('no-auto-approve');

const guid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const problems = [];
if (!account && !conn) problems.push('--account <storage account name> (or --connection-string for Azurite)');
if (!upn || !/^[^@\s]+@[^@\s]+\.[^@\s]+$/.test(upn)) problems.push('--upn <your UPN>');
if (!tenantId || !guid.test(tenantId)) problems.push('--tenant-id <home tenant GUID>');
if (objectId && !guid.test(objectId)) problems.push('--object-id must be a GUID');
if (problems.length) {
  console.error('Missing/invalid:\n  ' + problems.join('\n  '));
  process.exit(2);
}

const credential = conn ? undefined : new DefaultAzureCredential();
const url = account ? `https://${account}.table.core.windows.net` : undefined;
const client = (table) => conn
  ? TableClient.fromConnectionString(conn, table, { allowInsecureConnection: true })
  : new TableClient(url, table, credential);

const now = new Date();

const globalAdmin = {
  partitionKey: 'GlobalAdmins',
  rowKey: upn,
  Upn: upn,
  IsEnabled: true,
  AddedDate: now,
  AddedBy: addedBy,
  Role: 'GlobalAdmin',
};

const binding = {
  partitionKey: 'Bindings',
  rowKey: upn,
  Upn: upn,
  TenantId: tenantId,
  BoundBy: addedBy,
  BoundDate: now,
  // Only written when supplied. Merge mode below means a re-run WITHOUT --object-id never
  // erases an object id that is already pinned. ObjectIdPinnedDate is nullable in the backend.
  ...(objectId ? { ObjectId: objectId, ObjectIdPinnedDate: now } : {}),
};

const adminConfig = {
  partitionKey: 'GlobalConfig',
  rowKey: 'config',
  AutoApproveNewTenants: autoApprove,
  LastUpdated: now,
  UpdatedBy: addedBy,
};

const plan = [
  ['GlobalAdmins', globalAdmin, 'Replace'],
  ['AdminIdentityBindings', binding, 'Merge'],
  ['AdminConfiguration', adminConfig, 'Merge'],
];

for (const [table, entity, mode] of plan) {
  console.log(`${dry ? '[dry-run] ' : ''}${mode.padEnd(7)} ${table.padEnd(22)} ${entity.partitionKey}/${entity.rowKey}`);
  if (dry) continue;
  const c = client(table);
  await c.createTable().catch((e) => { if (e.statusCode !== 409) throw e; }); // the app also creates tables at start-up
  await c.upsertEntity(entity, mode);
}

if (!dry) {
  // Read back what the backend will read.
  const ga = await client('GlobalAdmins').getEntity('GlobalAdmins', upn);
  const bd = await client('AdminIdentityBindings').getEntity('Bindings', upn);
  const cfg = await client('AdminConfiguration').getEntity('GlobalConfig', 'config');
  console.log('\nVerified read-back:');
  console.log(`  GlobalAdmins          Role=${ga.Role} IsEnabled=${ga.IsEnabled}`);
  console.log(`  AdminIdentityBindings TenantId=${bd.TenantId} ObjectId=${bd.ObjectId || '(unpinned, pinned on first sign-in)'}`);
  console.log(`  AdminConfiguration    AutoApproveNewTenants=${cfg.AutoApproveNewTenants}`);
}
console.log('\nDone. Sign in to the portal with that account; the first member of your tenant also becomes Tenant Admin.');
