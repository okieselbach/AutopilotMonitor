# Agent Directives

- **Plan first.** Enter plan mode for any non-trivial task (3+ steps or an architectural decision) and write the spec before building. If something goes sideways, stop and re-plan instead of pushing on.
- **Use subagents liberally** for research, exploration, and parallel analysis — one focus each — to keep the main context clean.
- **Verify before claiming done.** Run the tests, check the logs, show the evidence. Diff against `main` where behaviour could regress.
- **Fix bugs autonomously.** Given a report, a failing test, or an error, resolve it — no hand-holding.
- **Capture corrections.** After any correction from the user, record the pattern and a preventing rule in `tasks/lessons.md`; review it at session start.
- Read files over 500 LOC in chunks. Never treat one grep as proof of absence.

Codex reviews your output once you are done.

## Code Quality

- Simplicity first: minimal, targeted changes. No temporary fixes — find the root cause.
- Where architecture is flawed, state is duplicated, or patterns are inconsistent, propose and implement the structural fix. Ask what a perfectionist reviewer would reject, and fix that too.
- On non-trivial changes, ask whether there is a more elegant way before presenting. Skip it for simple fixes — don't over-engineer.

## Task Management

Keep the plan in `tasks/todo.md` as checkable items, confirm it before implementing, tick items off as you go, and add a review section when done.

## Customer-Facing Claims

`src/Web/autopilot-monitor-web/app/{about,terms,privacy}/page.tsx` and `autopilotmonitor-docs/trust/*` state facts customers and security reviewers rely on. They drift silently.

- Verify every claim against the code before writing it — never carry one forward because it was already on the page.
- Update them in the same change as: runtime/framework versions, roles, notification providers, agent deployment or lifecycle, diagnostics payload, isolation/delegation model, retention caps, sub-processors, or any default governing what is collected.
- Describe only what customers can actually use; operator-only infrastructure is not a feature.
- Trust pages: durable phrasing over exact figures, and bump their "Last reviewed" date.

## Technical Docs — OKF Bundle (`docs/`)

Contributor/AI-facing docs (customer docs live in the `autopilotmonitor-docs` repo). Start at `docs/index.md` before opening individual files.

- New durable knowledge (architecture decisions, flows, non-obvious mechanisms) → an OKF concept doc with YAML frontmatter: `type` (mandatory), plus `title`, `description`, `resource`, `tags`, `timestamp`.
- Register it in `docs/index.md`, note the change in `docs/log.md` (ISO date). `index.md` and `log.md` are reserved names.
- English, structural markdown (`# Schema`, `# Examples`, `# Citations`). Links between docs are RELATIVE — never `/`-prefixed, that breaks GitHub navigation.
