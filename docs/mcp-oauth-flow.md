# MCP OAuth Flow — Who Authenticates Where

**Status:** Reference · **Owner:** MCP Server · **Code:** [`src/McpServer/autopilot-monitor-mcp/src/oauth.ts`](../../src/McpServer/autopilot-monitor-mcp/src/oauth.ts)

## Why this document exists

Connecting an AI client (Claude Desktop, claude.ai, VS Code, ChatGPT) to the
Autopilot Monitor MCP server involves **two unrelated identities** and **three
parties plus Entra ID**. This regularly confuses users during setup, because
the account signed in to the AI product and the account used for Autopilot
Monitor are usually different people-in-one: e.g. **Luke** uses his normal
account `luke` for his Claude subscription, but his admin account `adm.luke`
for Autopilot Monitor.

The two identities never mix:

* **`luke` (AI-product account)** — only authenticates Luke against Claude
  itself. Anthropic needs it to associate the OAuth callback with the right
  Claude user and connector. The MCP server never sees this identity.
* **`adm.luke` (Entra ID work account)** — the identity *inside* the token.
  Every MCP tool call is authorized as `adm.luke`; roles and tenant scope
  derive from it. Anthropic never sees this password, only the issued tokens.

## The parties

1. **AI client + its backend** (e.g. Claude Desktop + claude.ai) — the OAuth
   *client*. For hosted surfaces the token exchange and token storage happen
   in the vendor's backend, not on the user's machine.
2. **MCP server** (Container App) — acts as a stateless **OAuth proxy /
   authorization-server façade** in front of Entra ID. It is *not* an IdP:
   it registers clients dynamically (RFC 7591, HMAC-signed stateless
   `client_id`), enforces the redirect-URI allowlist and PKCE, and exchanges
   codes at Entra using its confidential client secret.
3. **Entra ID** — the real identity provider. This is where `adm.luke` lives.

## Full sequence

```
Claude Desktop        Browser (user's           MCP server              Entra ID
+ claude.ai backend   DEFAULT browser!)         (OAuth proxy)
     │                      │                        │                      │
 1.  │─ POST /mcp ──────────┼───────────────────────▶│ 401 + WWW-Auth       │
 2.  │─ Discovery ──────────┼───────────────────────▶│ RFC 9728/8414 docs   │
 3.  │─ POST /oauth/register┼───────────────────────▶│ client_id (HMAC-     │
     │                      │                        │ signed, embeds the   │
     │                      │                        │ registered callback) │
     │                      │                        │                      │
 4.  │─ opens browser ─────▶│─ GET /oauth/authorize ▶│─ 302 to Entra ──────▶│
     │                      │  (PKCE challenge)      │  app-reg client_id,  │
     │                      │                        │  prompt=select_acct  │
     │                      │                        │                      │
 5.  │                      │◀── Microsoft sign-in: account picker ────────│
     │                      │    ➜ user picks adm.luke HERE                │
     │                      │                        │                      │
 6.  │                      │◀─ 302 /oauth/callback?code=... ──────────────│
     │                      │─ code ────────────────▶│ verifies HMAC state, │
     │                      │                        │ re-checks allowlist  │
     │                      │◀─ 302 claude.ai/api/mcp/auth_callback?code=..│
     │                      │                        │                      │
     │◀═ browser hits claude.ai ═════╗               │                      │
     │   ➜ the browser MUST have an  │               │                      │
     │     active claude.ai session  │               │                      │
     │     as luke — otherwise       │               │                      │
     │     Anthropic cannot map the  │               │                      │
     │     code to a user/connector  │               │                      │
     │     and the connect fails ════╝               │                      │
     │                      │                        │                      │
 7.  │─ Anthropic backend: POST /oauth/token ───────▶│─ + client_secret ───▶│
     │   (code + PKCE verifier)                      │◀─ access + refresh ──│
     │◀─ tokens (stored server-side at Anthropic, per connector)            │
     │                      │                        │                      │
 8.  │─ POST /mcp + Bearer ─┼───────────────────────▶│ validates Entra JWT  │
     │                      │                        │ ➜ acts as adm.luke   │
```

## Key properties

* **Step 5 vs. step 6 is the double sign-in.** The whole browser leg runs in
  the user's *default* browser. That one browser profile needs **both** an
  active session at the AI product (`luke` at claude.ai — same account as in
  the desktop app) *and* the Entra sign-in as `adm.luke`. `prompt=
  select_account` forces the Entra account picker so an existing SSO session
  for the wrong account cannot be silently reused.
* **Step 6 is authenticated at the vendor.** `claude.ai/api/mcp/auth_callback`
  is an authenticated Anthropic endpoint; the authorization code is bound to
  the Claude user via the browser's claude.ai session cookies. No claude.ai
  session (or a different browser profile) → the code lands on a login page
  and the connect stalls, even though the Entra half succeeded. This is the
  single most common setup failure.
* **The MCP server is a pure intermediary.** Its client secret never leaves
  the server; the AI client never talks to Entra directly. The redirect
  target is checked twice (authorize + callback) against a strict allowlist
  of exact vendor callback URIs, and the proxy state that transits the
  user-agent is HMAC-signed with a 10-minute lifetime.
* **PKCE is end-to-end.** The AI client generates the verifier; the proxy
  forwards the challenge (step 4) and the verifier (step 7) untouched, and
  Entra validates the pair. The proxy additionally *requires* S256 PKCE and
  rejects downgrades.
* **Tokens are Entra tokens for `adm.luke`** with audience
  `api://<client_id>/access_as_user`. For hosted surfaces they are stored in
  the vendor's backend per connector (which is why the connector then works
  across desktop, web and mobile). Renewal is a silent `refresh_token` grant
  through the same `/oauth/token` proxy — no account picker again.
* **Native CLIs skip step 6's hosted callback.** Claude Code, Gemini CLI and
  VS Code's loopback flow use RFC 8252 `http://localhost:<port>` /
  `http://127.0.0.1:<port>` redirects instead; there the token exchange runs
  on the user's machine and no vendor-side session is involved.

## One-line summary

`luke` authenticates the *return path* of the authorization code to the AI
vendor (step 6); `adm.luke` is the identity *inside* the token that the MCP
server authorizes on every tool call (step 8).
