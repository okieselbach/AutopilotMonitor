# Agent Directives

- **Plan first.** Enter plan mode for any non-trivial task (3+ steps or an architectural decision) and write the spec before building. If something goes sideways, stop and re-plan instead of pushing on.
- **Use subagents liberally** for research, exploration, and parallel analysis — one focus each — to keep the main context clean.
- **Verify before claiming done.** Run the tests, check the logs, show the evidence. Diff against `main` where behaviour could regress.
- **Fix bugs autonomously.** Given a report, a failing test, or an error, resolve it — no hand-holding.
- **Capture corrections.** After any correction from the user, add a lesson to `internal/work/lessons.md` and update the rule block at its top. Read that rule block at session start.
- Read files over 500 LOC in chunks. Never treat one grep as proof of absence.

Codex reviews your output once you are done.

## Where knowledge lives

| Content | Location | Rule |
|---|---|---|
| Customer documentation | separate repo `autopilotmonitor-docs` (GitBook; baked into the MCP `search_docs` corpus) | Any change ⇒ MCP redeploy. Commit in that repo (`cd` first). |
| Technical concept docs (OKF bundle) | `internal/` — private submodule, entry point `internal/docs/index.md` | Read the index first. Concept docs only for invariants, mechanisms and architecture a later change could silently break. |
| Decisions, including "we will not build X" | `internal/decisions.md` | One entry per decision: date, decision, why, applies-to, status. A concept doc only when a mechanism needs explaining. |
| Working files | `internal/work/` | See the next section. |
| Agent review criteria | `internal/docs/agent/architecture-principles.md` | Check every agent change against it. |
| Private operator instructions | `internal/CLAUDE.md` | Imported at the end of this file. Absent for contributors without submodule access; nothing in it may be copied into this public repo. |
| Infrastructure | `infra/` — private submodule | |
| Skills | `.claude/commands/` — private submodule | |
| Claude auto-memory | outside the repo, per machine | Private notes only. Anything the project must keep goes into `internal/`. |

## Working files (`internal/work/`)

- `todo.md` — the running task only: checkable steps plus a Review section. Cleared when the next task starts.
- `plans/<slug>.md` — one file per larger piece of work. The first line carries the status (AKTIV / RELEASED / VERWORFEN); the file ends with a `## Offen` section.
- `backlog.md` — the one backlog: an "Ideen" section for raw ideas at the top, then deferred feature pieces, open questions, tech debt, user actions and the deploy/verify long tail. One line per item with what is missing, the source plan and the date. Items are deleted when done or when they get a plan file, never ticked.
- `archive/` — closed plans whose review is worth keeping.
- `lessons.md` — corrections from the user; rule block at the top, narrative below.

When → do:
- A plan is approved → copy it to `plans/<slug>.md`, put the steps into `todo.md`.
- A task closes → review into the plan file, leftovers as one-liners into `backlog.md`, clear `todo.md`. The commit message is the log.
- A backlog item is picked up → back into a plan file, removed from `backlog.md`.
- A decision is made, including a deliberate non-decision → entry in `internal/decisions.md`.
- Working files are committed inside `internal/` when a task closes, not on every tick.

## Hard rules

- Agent projects stay on .NET Framework 4.8.
- .NET build warnings are errors; only the NU1901–NU1904 advisories remain warnings.
- Never put real customer domains, tenant names or tenant IDs into code, tests, commits, comments or public docs.
- Deploy order is Backend → Web → MCP. Backend deploys are `workflow_dispatch` only. Verify the deployed version before claiming anything is live.
- Submodules (`internal/`, `infra/`, `.claude/commands/`): commit inside the submodule first, then bump the gitlink here.
- Stage explicit file lists; never `git add -A` or a directory. Pushing is the user's call.
- New API responses need a typed DTO plus the parity fact. MCP vocabularies are generated from the wire types, never retyped.
- Every route belongs in the route policy catalog; every new event type goes into the MCP resource catalog.

## Code Quality

- Simplicity first: minimal, targeted changes. No temporary fixes — find the root cause.
- Where architecture is flawed, state is duplicated, or patterns are inconsistent, propose and implement the structural fix. Ask what a perfectionist reviewer would reject, and fix that too.
- On non-trivial changes, ask whether there is a more elegant way before presenting. Skip it for simple fixes — don't over-engineer.

## Customer-Facing Claims

`src/Web/autopilot-monitor-web/app/{about,terms,privacy}/page.tsx` and `autopilotmonitor-docs/trust/*` state facts customers and security reviewers rely on. They drift silently.

- Verify every claim against the code before writing it — never carry one forward because it was already on the page.
- Update them in the same change as: runtime/framework versions, roles, notification providers, agent deployment or lifecycle, diagnostics payload, isolation/delegation model, retention caps, sub-processors, or any default governing what is collected.
- Describe only what customers can actually use; operator-only infrastructure is not a feature.
- Trust pages: durable phrasing over exact figures, and bump their "Last reviewed" date.

## Technical Docs — OKF Bundle (`internal/`, private submodule)

Contributor/AI-facing docs (customer docs live in the `autopilotmonitor-docs` repo). `internal/` is a **private** submodule (`okieselbach/Autopilot-Monitor-internal`) — this repo is public, the bundle is not. Start at `internal/docs/index.md` before opening individual files.

- Write a concept doc only for durable knowledge a later change could silently break: invariants, architecture decisions, non-obvious mechanisms — not for every feature. YAML frontmatter: `type` (mandatory, one of `Concept`, `Reference`, `Guide`), plus `title`, `description`, `resource`, `tags`, `timestamp`.
- Register it in `internal/docs/index.md`. There is no `log.md` — the commit message is the log. `index.md` is a reserved name.
- `internal/work/`, `internal/decisions.md` and `internal/CLAUDE.md` are not part of the bundle and are not listed in the index.
- English, structural markdown (`# Schema`, `# Examples`, `# Citations`). Links between docs are RELATIVE — never `/`-prefixed, that breaks GitHub navigation.

## Language

- English for knowledge: concept docs, decisions, both CLAUDE files, code comments.
- German is fine for working files under `internal/work/`.

Private half of these instructions (skills, private repositories, operations): @../internal/CLAUDE.md
