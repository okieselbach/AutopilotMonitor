import type { RulePresentation } from "@/lib/generated/wire-types.generated";
import type { ResolvedPresentation } from "@/lib/rulePresentation";
import { parseInline } from "@/lib/ruleText";
import { RuleInlines } from "./RuleText";

const ROLE_LABELS: Record<string, string> = {
  outcome: "Outcome",
  cause: "Cause",
  context: "Context",
  advisory: "Advisory",
};

// Neutral outline pill (D-100): the role is a label, not a state.
const PILL = "inline-flex rounded-full border border-gray-300 px-2 py-0.5 text-[11px] font-medium text-gray-600";

/** A rule's presentation block as written: the templates, tokens shown as tokens (rule card). */
export function PresentationTemplateView({ presentation }: { presentation: RulePresentation }) {
  return (
    <div className="space-y-2 rounded-lg border border-gray-200 bg-gray-50 p-3 text-sm">
      <div className="flex flex-wrap items-center gap-2">
        <span className={PILL}>{ROLE_LABELS[presentation.role] ?? presentation.role}</span>
        {presentation.summary && (
          <span className="text-gray-800">
            <RuleInlines inlines={parseInline(presentation.summary)} />
          </span>
        )}
      </div>
      {presentation.subject && (
        <p className="text-xs text-gray-500">
          About a {presentation.subject.kind}:{" "}
          <RuleInlines inlines={parseInline(presentation.subject.name ?? presentation.subject.key)} />
        </p>
      )}
      {presentation.facts && presentation.facts.length > 0 && (
        <dl className="grid grid-cols-[auto_minmax(0,1fr)] gap-x-3 gap-y-1 text-xs">
          {presentation.facts.map((fact, i) => (
            <div key={i} className="contents">
              <dt className="text-gray-500">{fact.label}</dt>
              <dd className="break-words text-gray-800">
                <RuleInlines inlines={parseInline(fact.value)} />
                {fact.format && fact.format !== "text" && <span className="ml-2 text-gray-400">{fact.format}</span>}
              </dd>
            </div>
          ))}
        </dl>
      )}
    </div>
  );
}

/** A presentation resolved against one session's evidence (dry-run preview). */
export function PresentationPreview({ resolved, notRecorded }: { resolved: ResolvedPresentation; notRecorded: string[] }) {
  return (
    <div className="space-y-2 rounded-lg border border-gray-200 bg-white p-3 text-sm">
      <div className="flex flex-wrap items-center gap-2">
        <span className={PILL}>{ROLE_LABELS[resolved.role] ?? resolved.role}</span>
        {resolved.summary && <span className="font-medium text-gray-900">{resolved.summary}</span>}
      </div>
      {resolved.subject && (
        <p className="text-xs text-gray-500">
          About a {resolved.subject.kind}: <span className="text-gray-800">{resolved.subject.name}</span>
          {resolved.subject.name !== resolved.subject.key && <span className="ml-1 font-mono text-gray-400">{resolved.subject.key}</span>}
        </p>
      )}
      {resolved.facts.length > 0 && (
        <dl className="grid grid-cols-[auto_minmax(0,1fr)] gap-x-3 gap-y-1 text-xs">
          {resolved.facts.map((fact, i) => (
            <div key={i} className="contents">
              <dt className="text-gray-500">{fact.label}</dt>
              <dd className={`break-words text-gray-900 ${fact.format === "code" || fact.format === "errorCode" ? "font-mono" : ""}`}>{fact.value}</dd>
            </div>
          ))}
        </dl>
      )}
      {notRecorded.length > 0 && <p className="text-xs text-gray-500">Not recorded in this session: {notRecorded.join(", ")}</p>}
    </div>
  );
}
