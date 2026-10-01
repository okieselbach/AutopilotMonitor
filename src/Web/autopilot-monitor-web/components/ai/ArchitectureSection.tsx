import { DataPath } from "./DataPath";
import { CheckChip } from "./icons";

/** Each line is backed by the MCP access model: READ_ONLY tool annotations, Entra OAuth, portal-role authorization per request (D-239, D-283). */
const GUARDRAILS = [
  { title: "Read-only tools", text: "Your assistant reads. Sessions, rules and settings stay untouched." },
  { title: "Microsoft sign-in", text: "Work accounts through Microsoft Entra ID. There are no API keys to hand out." },
  { title: "Your portal role", text: "The assistant sees exactly what the person's role allows in the portal." },
  { title: "Access follows the role", text: "Remove a role or block an account, and access ends within minutes." },
];

export function ArchitectureSection() {
  return (
    <section
      id="architecture"
      data-track-section="architecture"
      className="scroll-mt-20 border-t border-[var(--lp-line-soft)] px-6 py-20 sm:py-24"
    >
      <div className="mx-auto max-w-7xl">
        <div className="max-w-2xl">
          <p className="text-xs font-semibold uppercase tracking-[0.22em] text-[var(--lp-accent-ink)]">Bring your own AI</p>
          <h2 className="mt-3 text-balance text-3xl font-bold tracking-tight text-[var(--lp-ink)] sm:text-4xl">
            We provide the data. You pick the AI.
          </h2>
          <p className="mt-4 text-lg leading-relaxed text-[var(--lp-ink-soft)]">
            MCP is an open standard for connecting AI assistants to data. Use the assistant your organization already
            approved, wherever its model runs.
          </p>
        </div>

        <DataPath />

        <ul className="mt-10 grid grid-cols-1 gap-x-8 gap-y-6 sm:grid-cols-2 lg:grid-cols-4">
          {GUARDRAILS.map(item => (
            <li key={item.title} className="flex items-start gap-3">
              <CheckChip />
              <div>
                <h3 className="text-[15px] font-semibold text-[var(--lp-ink)]">{item.title}</h3>
                <p className="mt-1 text-sm leading-relaxed text-[var(--lp-ink-soft)]">{item.text}</p>
              </div>
            </li>
          ))}
        </ul>
      </div>
    </section>
  );
}
