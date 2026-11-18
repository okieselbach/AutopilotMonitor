/**
 * Cloudflare Worker: download.<your-domain>/agent/*  ->  <storage account>.blob.core.windows.net/agent/*
 *
 * Why a Worker instead of a plain proxied CNAME: Azure Blob only answers for its own hostnames, and
 * a storage custom domain cannot serve HTTPS. A Worker fetches the blob URL itself, so the device
 * talks HTTPS to Cloudflare and Cloudflare talks HTTPS to Azure. It replaces the Front Door alias
 * upstream uses (download.autopilotmonitor.com).
 *
 * Deliberately narrow: GET/HEAD only, only /agent/<file>, only the file types the bootstrap uses,
 * no path tricks, no caching (version.json and the scripts rotate; upstream sets no-cache on them).
 *
 * Config: variable BLOB_ORIGIN = https://stautopilotmonitorprod.blob.core.windows.net  (see wrangler.jsonc)
 */
const ALLOWED_FILE = /^[A-Za-z0-9._-]+\.(zip|json|ps1|msi|cat)$/;

export default {
  async fetch(request, env) {
    if (request.method !== 'GET' && request.method !== 'HEAD') {
      return new Response('Method not allowed', { status: 405, headers: { Allow: 'GET, HEAD' } });
    }
    const url = new URL(request.url);
    const m = /^\/agent\/([^/]+)$/.exec(url.pathname);
    if (!m || !ALLOWED_FILE.test(m[1])) return new Response('Not found', { status: 404 });

    const origin = new URL(`/agent/${m[1]}`, env.BLOB_ORIGIN);
    // Forward only what a download needs; never forward cookies or auth headers.
    const fwd = new Headers();
    for (const h of ['range', 'if-none-match', 'if-modified-since', 'accept-encoding']) {
      const v = request.headers.get(h);
      if (v) fwd.set(h, v);
    }
    const upstream = await fetch(origin.toString(), { method: request.method, headers: fwd, redirect: 'manual', cf: { cacheTtl: 0 } });

    const out = new Headers();
    for (const h of ['content-type', 'content-length', 'content-range', 'accept-ranges', 'etag', 'last-modified', 'content-md5', 'content-disposition', 'content-encoding']) {
      const v = upstream.headers.get(h);
      if (v) out.set(h, v);
    }
    out.set('cache-control', 'no-store');
    out.set('x-content-type-options', 'nosniff');
    return new Response(request.method === 'HEAD' ? null : upstream.body, { status: upstream.status, headers: out });
  },
};
