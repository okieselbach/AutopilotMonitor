/**
 * Build identity of THIS server process — one place for /health, the boot line, the MCP
 * handshake and the get_deployment_state tool (which reports the MCP's live state from
 * in-process values: the process answering the call IS the live deployment).
 */
import { resolve, dirname } from 'node:path';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const __dirname = dirname(fileURLToPath(import.meta.url));

const pkg = JSON.parse(readFileSync(resolve(__dirname, '..', 'package.json'), 'utf-8')) as { version: string };

// Version contract shared with the agent and the backend: <major>.<minor>.<build>,
// where major.minor is curated in package.json and <build> is reserved per build
// from the counter blob versions/mcp.txt and baked in by deploy-mcp.yml. Without
// the env var (any local run) the package.json version stands as-is, which is how
// /health and the MCP handshake tell a workstation from a deployed image.
const BUILD_NUMBER = process.env.BUILD_NUMBER ?? '';
export const SERVER_VERSION: string = BUILD_NUMBER
  ? `${pkg.version.split('.').slice(0, 2).join('.')}.${BUILD_NUMBER}`
  : pkg.version;

// Commit of THIS repo the image was built from. Counterpart to DOCS_COMMIT below:
// that one tracks the documentation bundle, this one the server itself.
export const BUILD_COMMIT = process.env.BUILD_COMMIT ?? 'unknown';
export const BUILD_UTC = process.env.BUILD_UTC ?? 'unknown';

// Commit of the docs bundle this image was built from. The docs repo changes
// independently of this one, so a deployed image can silently serve stale
// documentation; surfacing the SHA on /health makes that checkable instead of a
// thing to remember.
export const DOCS_COMMIT = process.env.DOCS_COMMIT ?? 'unknown';
