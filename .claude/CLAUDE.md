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

Four public surfaces make statements customers and security reviewers rely on. They drift silently because nobody opens them during a refactor — treat them as part of the change, not as documentation to update later.

- **`src/Web/autopilot-monitor-web/app/about/page.tsx`** — marketing and tech-stack claims (framework and runtime versions, roles, integrations, agent deployment and lifecycle, diagnostics contents, isolation model).
- **`app/terms/page.tsx`** and **`app/privacy/page.tsx`** — plans and contracting, what is collected, retention, access, sub-processors, data residency.
- **`autopilotmonitor-docs/trust/security-faq.md`** and **`trust/subprocessors.md`** — the technical detail behind the two pages above; that repo's `index.md` carries its own sync rule and a "Last reviewed" date.

Rules:

- Changing a runtime or framework version, a role, a notification provider, an agent deployment or lifecycle behaviour, the diagnostics payload, the isolation or delegation model, retention caps, sub-processors, or a **default that governs what is collected** means updating the affected surfaces in the same change.
- **Verify claims against the code before writing them** — never carry a claim forward because it was already on the page. Stale claims found in July 2026 included a ".NET 8" backend that was .NET 10, a non-existent "Next.js 18", ETL log collection that never happened, and "each tenant runs its own isolated instance" for a shared multi-tenant service.
- Prefer durable phrasing over exact figures on the trust pages, so routine tuning does not invalidate a published statement.
- Do not advertise operator-only infrastructure (e.g. Telegram ops alerting) as a customer feature.

## Technical Docs — OKF Knowledge Bundle (`docs/`)

`docs/` is an [Open Knowledge Format (OKF) v0.1](https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md) knowledge bundle for contributor/AI-facing technical documentation (customer docs live in the separate autopilotmonitor-docs repo).

- **Consume first**: start at `docs/index.md` for progressive disclosure before opening individual documents.
- **When new durable technical knowledge emerges** (architecture decisions, flows, non-obvious mechanisms), capture it as an OKF concept document in `docs/` — markdown with YAML frontmatter: `type` (mandatory), plus `title`, `description`, `resource`, `tags`, `timestamp` (recommended).
- **Maintain the bundle**: add every new document to `docs/index.md`, note changes in `docs/log.md` (grouped by ISO date). Use standard RELATIVE markdown links between docs — never the OKF `/`-prefixed bundle-absolute form (GitHub resolves those from the repo root and navigation breaks). `index.md` and `log.md` are reserved filenames.
- Documents are English, structural markdown over prose (`# Schema`, `# Examples`, `# Citations` sections where applicable).