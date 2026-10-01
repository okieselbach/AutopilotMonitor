"use client";

import { useState } from "react";
import { AssistantPanel } from "./AssistantPanel";
import { QUESTIONS, SKINS, TOPICS, type Skin, type TopicId } from "./questions";
import { SEGMENT_BUTTON, SEGMENT_GROUP, SEGMENT_OFF, SEGMENT_ON } from "./segmented";

/**
 * The /ai question gallery: pick a topic and a question, see the tool calls and the answer, and
 * switch the client the answer is shown in. Every question text is in the static HTML; the
 * inactive topic lists are only hidden.
 */
export function QuestionGallery() {
  const [questionId, setQuestionId] = useState(QUESTIONS[0].id);
  const [skin, setSkin] = useState<Skin>("chat");
  const question = QUESTIONS.find(q => q.id === questionId) ?? QUESTIONS[0];
  const skinNote = SKINS.find(s => s.id === skin)?.note ?? "";

  const selectTopic = (topic: TopicId) => {
    const first = QUESTIONS.find(q => q.topic === topic);
    if (first) setQuestionId(first.id);
  };

  return (
    <div className="mt-10 grid grid-cols-1 items-start gap-6 lg:grid-cols-[minmax(0,0.85fr)_minmax(0,1.35fr)] lg:gap-8">
      <div className="min-w-0">
        <div role="tablist" aria-label="Question topics" className="flex flex-wrap gap-2">
          {TOPICS.map(topic => {
            const selected = topic.id === question.topic;
            return (
              <button
                key={topic.id}
                type="button"
                role="tab"
                aria-selected={selected}
                data-track={`topic:${topic.id}`}
                onClick={() => selectTopic(topic.id)}
                className={`whitespace-nowrap rounded-full border px-3.5 py-2.5 text-[13px] font-semibold leading-none transition-colors ${
                  selected
                    ? "border-[var(--lp-ink)] bg-[var(--lp-ink)] text-[var(--lp-surface)]"
                    : "border-[var(--lp-line)] bg-[var(--lp-surface)] text-[var(--lp-ink-soft)] hover:text-[var(--lp-ink)]"
                }`}
              >
                {topic.label}
              </button>
            );
          })}
        </div>

        {TOPICS.map(topic => (
          <div
            key={topic.id}
            role="tabpanel"
            aria-label={topic.label}
            className={topic.id === question.topic ? "mt-5 grid grid-cols-1 gap-2" : "hidden"}
          >
            {QUESTIONS.filter(q => q.topic === topic.id).map(q => {
              const pressed = q.id === question.id;
              return (
                <button
                  key={q.id}
                  type="button"
                  aria-pressed={pressed}
                  data-track={`question:${q.id}`}
                  onClick={() => setQuestionId(q.id)}
                  className={`flex w-full flex-col gap-1.5 rounded-[14px] border px-4 py-3.5 text-left transition-colors ${
                    pressed
                      ? "border-[var(--lp-accent-line)] bg-[var(--lp-accent-soft)]"
                      : "border-[var(--lp-line-soft)] bg-[var(--lp-surface)] hover:border-[var(--lp-accent-line)]"
                  }`}
                >
                  <span className="text-[15px] font-semibold leading-snug text-[var(--lp-ink)]">{q.text}</span>
                  <span
                    className={`font-mono text-[11.5px] ${pressed ? "text-[var(--lp-accent-ink)]" : "text-[var(--lp-ink-faint)]"}`}
                  >
                    {q.tools.map(t => (t.local ? "local search" : t.name)).join(" · ")}
                  </span>
                </button>
              );
            })}
          </div>
        ))}

        <p className="mt-6 text-sm leading-relaxed text-[var(--lp-ink-faint)]">
          {"Built-in prompts such as "}
          <span className="font-mono text-[12.5px] text-[var(--lp-ink-soft)]">investigate-failed-session</span>
          {" and "}
          <span className="font-mono text-[12.5px] text-[var(--lp-ink-soft)]">cve-exposure-audit</span>
          {" give your assistant a head start."}
        </p>
      </div>

      <div className="min-w-0">
        <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-3">
          <p className="text-sm text-[var(--lp-ink-soft)]">{skinNote}</p>
          <div role="group" aria-label="Show the answer in" className={SEGMENT_GROUP}>
            {SKINS.map(s => (
              <button
                key={s.id}
                type="button"
                aria-pressed={s.id === skin}
                data-track={`skin:${s.id}`}
                onClick={() => setSkin(s.id)}
                className={`${SEGMENT_BUTTON} ${s.id === skin ? SEGMENT_ON : SEGMENT_OFF}`}
              >
                {s.label}
              </button>
            ))}
          </div>
        </div>
        <div className="mt-4">
          <AssistantPanel skin={skin} question={question} />
        </div>
        <p className="mt-3 text-[13px] text-[var(--lp-ink-faint)]">Sample data. The tools and the shape of each answer are real.</p>
      </div>
    </div>
  );
}
