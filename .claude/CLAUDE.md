# Agent Directives

- **Plan first.** Enter plan mode for any non-trivial task (3+ steps or an architectural decision) and write the spec before building. If something goes sideways, stop and re-plan instead of pushing on.
- **Use subagents liberally** for research, exploration, and parallel analysis — one focus each — to keep the main context clean.
- **Verify before claiming done.** Run the tests, check the logs, show the evidence. Diff against `main` where behaviour could regress.
- **Fix bugs autonomously.** Given a report, a failing test, or an error, resolve it — no hand-holding.
- Read files over 500 LOC in chunks. Never treat one grep as proof of absence.

## Where knowledge lives

| Content | Location | Rule |
|---|---|---|
| Customer documentation | separate repo `autopilotmonitor-docs` (GitBook; baked into the MCP `search_docs` corpus) | Any change ⇒ MCP redeploy. Commit in that repo (`cd` first). |
| Technical concept docs (OKF bundle) | `internal/` — private submodule, entry point `internal/docs/index.md` | Read the index first. Concept docs only for invariants, mechanisms and architecture a later change could silently break. |
| Decisions, including "we will not build X" | `internal/decisions.md` | One entry per decision: date, decision, why, applies-to, status. |
| Working files, lessons, plans | `internal/work/` | Rules in `internal/CLAUDE.md`. |
| Agent review criteria | `internal/docs/agent/architecture-principles.md` | Check every agent change against it. |
| Private operator instructions | `internal/CLAUDE.md` | Imported at the end of this file. Absent for contributors without submodule access; nothing in it may be copied into this public repo. |
| Infrastructure and operator scripts | `infra/` — private submodule | Bicep plus deploy/backup/migration scripts. Scripts never carry credentials; secrets come from env or a gitignored file next to the script. |
| Skills | `.claude/commands/` — private submodule | Each skill's frontmatter says when to use it. |
| Claude auto-memory | outside the repo, per machine | Private notes only. Anything the project must keep goes into `internal/`. |

## Hard rules

- Agent projects stay on .NET Framework 4.8.
- .NET build warnings are errors; only the NU1901–NU1904 advisories remain warnings.
- Never put real customer domains, tenant names or tenant IDs into code, tests, commits, comments or public docs.
- Deploy order is Backend → Web → MCP. Backend deploys are `workflow_dispatch` only. Verify the deployed version before claiming anything is live.
- Submodules (`internal/`, `infra/`, `.claude/commands/`): commit inside the submodule first, then bump the gitlink here.
- Stage explicit file lists; never `git add -A` or a directory. Pushing is the user's call.
- New API responses need a typed DTO plus the parity fact. MCP vocabularies are generated from the wire types, never retyped.
- Every route belongs in the route policy catalog; every new event type goes into the MCP resource catalog.
- The about, terms and privacy pages under `src/Web/autopilot-monitor-web/app/` state facts customers rely on: verify every claim against the code before writing it and update them in the same change as the fact they describe.

## Code Quality

- Simplicity first: minimal, targeted changes. No temporary fixes — find the root cause.
- Where architecture is flawed, state is duplicated, or patterns are inconsistent, propose and implement the structural fix. Ask what a perfectionist reviewer would reject, and fix that too.
- On non-trivial changes, ask whether there is a more elegant way before presenting. Skip it for simple fixes — don't over-engineer.
- English for code comments and everything that carries knowledge.

Private half of these instructions (process, working files, technical docs, operations): @../internal/CLAUDE.md
