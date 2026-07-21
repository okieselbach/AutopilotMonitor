## Workflow Orchestration
# Agent Directives: Mechanical Overrides

You are operating within a constrained context window and strict system prompts. To produce production-grade code, you MUST adhere to these overrides:



### 1. Plan Mode Default
- Enter plan mode for ANY non-trivial task (3+ steps or architectural decisions)
- If something goes sideways, STOP and re-plan immediately — don't keep pushing
- Use plan mode for specification steps, not just building
- Write detailed specs upfront to reduce ambiguity

### 2. Subagent Strategy
- Use subagents liberally to keep main context window clean
- Offload research, exploration, and parallel analysis to subagents
- For complex problems, throw more compute at it via subagents
- One tack per subagent for focused execution

### 3. Self-Improvement Loop
- After ANY correction from the user: update `tasks/lessons.md` with the pattern
- Write rules for yourself that prevent the same mistake
- Ruthlessly iterate on these lessons until mistake rate drops
- Review lessons at session start for relevant project

### 4. Verification Before Done
- Never mark a task complete without proving it works
- Diff behavior between main and your changes when relevant
- Ask yourself: "Would a staff engineer approve this?"
- Run tests, check logs, demonstrate correctness

### 5. Demand Elegance (Balanced)
- For non-trivial changes: pause and ask "is there a more elegant way?"
- If a fix feels hacky: "Knowing everything I know now, implement the elegant solution"
- Skip this for simple, obvious fixes — don't over-engineer
- Challenge your own work before presenting it

### 6. Autonomous Bug Fixing
- When given a bug report: just fix it. Don't ask for hand-holding
- Point at logs, errors, failing tests — then resolve them
- Zero context switching required from the user
- Go fix failing CI tests without being told how



### Tools Execution
- Read any file over 500 LOC in chunks using offset and limit parameters.
- Do not assume a single grep caught everything.

### Code Quality
- If architecture is flawed, state is duplicated, or patterns are inconsistent - propose and implement structural fixes. Ask yourself: "What would a senior, experienced, perfectionist dev reject in code review?" Fix all of it.


Codex will review your output once you are done!


## Task Management

1. **Plan First**: Write plan to `tasks/todo.md` with checkable items
2. **Verify Plan**: Check in before starting implementation
3. **Track Progress**: Mark items complete as you go
4. **Explain Changes**: High-level summary at each step
5. **Document Results**: Add review section to `tasks/todo.md`
6. **Capture Lessons**: Update `tasks/lessons.md` after corrections

## Core Principles

- **Simplicity First**: Make every change as simple as possible. Impact minimal code.
- **No Laziness**: Find root causes. No temporary fixes. Senior developer standards.
- **Minimal Impact**: Changes should only touch what's necessary. Avoid introducing bugs.

## Customer-Facing Claims

`src/Web/autopilot-monitor-web/app/{about,terms,privacy}/page.tsx` and `autopilotmonitor-docs/trust/*` state facts customers and security reviewers rely on. They drift silently.

- Verify every claim against the code before writing it — never carry one forward because it was already on the page.
- Update them in the same change as: runtime/framework versions, roles, notification providers, agent deployment or lifecycle, diagnostics payload, isolation/delegation model, retention caps, sub-processors, or any default governing what is collected.
- Trust pages: durable phrasing over exact figures, and bump their "Last reviewed" date. Never advertise operator-only infrastructure (Telegram ops alerts) as a customer feature.

## Technical Docs — OKF Bundle (`docs/`)

Contributor/AI-facing docs (customer docs live in the `autopilotmonitor-docs` repo). Start at `docs/index.md` before opening individual files.

- New durable knowledge (architecture decisions, flows, non-obvious mechanisms) → an OKF concept doc with YAML frontmatter: `type` (mandatory), plus `title`, `description`, `resource`, `tags`, `timestamp`.
- Register it in `docs/index.md`, note the change in `docs/log.md` (ISO date). `index.md` and `log.md` are reserved names.
- English, structural markdown (`# Schema`, `# Examples`, `# Citations`). Links between docs are RELATIVE — never `/`-prefixed, that breaks GitHub navigation.