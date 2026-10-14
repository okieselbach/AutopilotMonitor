"use client";

import { RuleInlines, RuleText } from "@/components/findings/RuleText";
import type { StepsView } from "../lib/steps";

/** What to do: the steps of the finding that explains the outcome, filled in with this session's values. */
export default function StepsSection({ steps, onCopy, copied }: { steps: StepsView; onCopy: () => void; copied: boolean }) {
  // Steps are numbered across the groups.
  const offsets = steps.groups.map((_, i) => steps.groups.slice(0, i).reduce((sum, g) => sum + g.steps.length, 0));
  return (
    <section className="rounded-lg bg-white p-5 shadow sm:p-6" aria-labelledby="steps-title">
      <div className="flex flex-wrap items-center justify-between gap-2">
        <h2 id="steps-title" className="text-lg font-semibold text-gray-900">
          What to do
        </h2>
        <button
          type="button"
          onClick={onCopy}
          className="inline-flex items-center gap-1.5 rounded-md border border-gray-300 bg-white px-2.5 py-1.5 text-xs font-medium text-gray-700 hover:bg-gray-50"
        >
          {copied ? "Copied" : "Copy steps"}
        </button>
      </div>
      <div className="mt-4 space-y-5">
        {steps.groups.map((group, i) => (
          <div key={i}>
            <RuleText blocks={group.title} className="text-sm font-semibold text-gray-900" />
            <ol className="mt-2 space-y-3">
              {group.steps.map((step, j) => (
                <li key={j} className="grid grid-cols-[1.75rem_minmax(0,1fr)] gap-3">
                  <span className="flex h-7 w-7 items-center justify-center rounded-full bg-gray-100 text-sm font-semibold text-gray-700">{offsets[i] + j + 1}</span>
                  <p className="pt-1 text-sm text-gray-700">
                    <RuleInlines inlines={step} />
                  </p>
                </li>
              ))}
            </ol>
          </div>
        ))}
      </div>
      <p className="mt-4 text-xs text-gray-500">
        From the finding “{steps.source.result.ruleTitle}”, filled in with this session’s values.
      </p>
    </section>
  );
}
