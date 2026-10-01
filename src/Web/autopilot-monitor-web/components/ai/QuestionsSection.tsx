import { ArrowDownIcon } from "./icons";
import { QuestionGallery } from "./QuestionGallery";

export function QuestionsSection() {
  return (
    <section id="questions" data-track-section="questions" className="scroll-mt-20 px-6 py-20 sm:py-24">
      <div className="mx-auto max-w-7xl">
        <div className="max-w-2xl">
          <p className="text-xs font-semibold uppercase tracking-[0.22em] text-[var(--lp-accent-ink)]">Try it</p>
          <h2 className="mt-3 text-balance text-3xl font-bold tracking-tight text-[var(--lp-ink)] sm:text-4xl">
            Questions teams ask every day.
          </h2>
          <p className="mt-4 text-lg leading-relaxed text-[var(--lp-ink-soft)]">
            Pick a question to see which tools the assistant calls and what comes back. All answers use sample data.
          </p>
          <a
            href="#architecture"
            data-track="connection_link"
            className="mt-4 inline-flex items-center gap-1.5 text-[15px] font-semibold text-[var(--lp-accent-ink)] hover:opacity-80"
          >
            How the connection works
            <ArrowDownIcon />
          </a>
        </div>
        <QuestionGallery />
      </div>
    </section>
  );
}
