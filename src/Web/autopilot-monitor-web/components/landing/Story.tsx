import { Reveal } from "./Reveal";
import { StoryAnalysis } from "./StoryAnalysis";
import { StoryFleet } from "./StoryFleet";

function ActText({
  time,
  title,
  children,
}: {
  time: string;
  title: string;
  children: React.ReactNode;
}) {
  return (
    <div>
      <span className="inline-block font-mono text-[11px] font-semibold px-2.5 py-1 rounded-md bg-[var(--lp-accent-soft)] text-[var(--lp-accent-ink)]">
        {time}
      </span>
      <h3 className="mt-3 text-2xl sm:text-3xl font-bold tracking-tight text-[var(--lp-ink)]">{title}</h3>
      <div className="mt-3 text-[15px] text-[var(--lp-ink-soft)] leading-relaxed space-y-3">{children}</div>
    </div>
  );
}

/** Act 1 visual — the session pops up on the dashboard. */
function SessionAppeared() {
  return (
    <div className="rounded-2xl border border-[var(--lp-line)] bg-[var(--lp-surface)] shadow-xl shadow-black/[0.06] overflow-hidden">
      <div className="flex items-center justify-between px-4 py-2.5 border-b border-[var(--lp-line-soft)] bg-[var(--lp-surface-2)]">
        <span className="text-xs font-semibold text-[var(--lp-ink)]">Sessions</span>
        <span className="text-[10px] font-semibold px-2 py-0.5 rounded-full text-[var(--lp-accent-ink)] bg-[var(--lp-accent-soft)]">
          1 new
        </span>
      </div>
      <div className="p-4">
        <div className="rounded-xl border border-[var(--lp-accent-line)] bg-[var(--lp-accent-soft)] p-3.5">
          <div className="flex items-center gap-2">
            <span className="w-2 h-2 rounded-full bg-[var(--lp-accent)] lp-live-dot shrink-0" />
            <span className="text-[13px] font-semibold text-[var(--lp-ink)] truncate">CONTOSO-4711</span>
            <span className="ml-auto text-[10px] font-semibold px-2 py-0.5 rounded-full bg-[var(--lp-surface)] border border-[var(--lp-line)] text-[var(--lp-ink-soft)] shrink-0">
              Enrolling
            </span>
          </div>
          <div className="mt-2 flex items-center gap-2 text-[11px] text-[var(--lp-ink-faint)]">
            <span className="font-mono">ThinkPad X1 Carbon G12</span>
            <span>·</span>
            <span>User-driven</span>
            <span>·</span>
            <span>Berlin, DE</span>
          </div>
          <div className="mt-3 space-y-1 font-mono text-[10px] text-[var(--lp-ink-faint)]">
            <p>09:02:14  phase_transition      Device Preparation started</p>
            <p>09:02:31  enrollment_type_detected  User-driven deployment</p>
          </div>
        </div>
        <div className="mt-3 flex items-center justify-between text-[11px] text-[var(--lp-ink-faint)] px-1">
          <span>No remote session. No guessing.</span>
          <span className="text-[var(--lp-accent-ink)] font-medium">Live ↗</span>
        </div>
      </div>
    </div>
  );
}

/**
 * The scroll story: one enrollment, four acts on a single vertical
 * timeline. Reading order is strictly top-down — text left, visual
 * right, time rail connecting the acts.
 */
export function Story() {
  const acts: { time: string; title: string; text: React.ReactNode; visual: React.ReactNode }[] = [
    {
      time: "09:02",
      title: "A device powers on somewhere.",
      text: (
        <>
          <p>
            A new hire unboxes their laptop three time zones away. The bootstrapper you assigned
            once in Intune kicks in, and the enrollment appears on your dashboard — live.
          </p>
          <p>
            Every phase, every app install, every reboot streams in as it happens. No remote
            session, no guessing, no &ldquo;can you send a photo of the screen?&rdquo;
          </p>
        </>
      ),
      visual: <SessionAppeared />,
    },
    {
      time: "09:41",
      title: "Something's wrong.",
      text: (
        <>
          <p>
            Device Setup has been running for 39 minutes. Without monitoring, this is where you
            stare at a frozen ESP spinner and start guessing.
          </p>
          <p>
            Instead, an analyze rule fires and names the stuck app —{" "}
            <span className="font-semibold text-[var(--lp-ink)]">before anyone opens a ticket</span>.
            Rules are community-driven and fully customizable.
          </p>
        </>
      ),
      visual: <StoryAnalysis />,
    },
    {
      time: "09:50",
      title: "See where the minutes went.",
      text: (
        <>
          <p>
            The enrollment finished — but why did it take 39 minutes in Device Setup? Time
            attribution breaks the session down cost by cost: which blocking app, which reboot,
            which wait actually consumed the time.
          </p>
          <p>
            No more &ldquo;it felt slow&rdquo;. You see exactly what to cut to make every
            following rollout faster.
          </p>
        </>
      ),
      visual: (
        <div className="rounded-2xl border border-[var(--lp-line)] shadow-xl shadow-black/[0.06] overflow-hidden bg-white">
          {/* Static export: next/image is not configured, plain img is intentional */}
          {/* eslint-disable-next-line @next/next/no-img-element */}
          <img
            src="/landing/time-attribution.png"
            alt="Time attribution breakdown of a real enrollment session"
            width={1216}
            height={403}
            className="w-full h-auto block"
          />
        </div>
      ),
    },
    {
      time: "Later",
      title: "Now multiply by 500.",
      text: (
        <>
          <p>
            One device is a story. Five hundred are a pattern. Fleet health shows success rates,
            duration trends, and which app or hardware model is quietly costing you the most.
          </p>
          <p>
            The VPN client from this morning? It&apos;s in 62% of your slow enrollments. Now you
            know what to fix first — with data, not anecdotes.
          </p>
        </>
      ),
      visual: <StoryFleet />,
    },
  ];

  return (
    <section id="story" className="py-20 sm:py-28 px-6 scroll-mt-20">
      <div className="max-w-6xl mx-auto">
        <Reveal className="max-w-2xl mx-auto text-center">
          <p className="text-xs font-semibold uppercase tracking-[0.22em] text-[var(--lp-accent-ink)]">The story</p>
          <h2 className="mt-3 text-3xl sm:text-5xl font-bold tracking-tight text-[var(--lp-ink)] text-balance">
            Follow one enrollment.
          </h2>
          <p className="mt-4 text-lg text-[var(--lp-ink-soft)]">
            This is a normal Tuesday with Autopilot Monitor — told the way you&apos;d actually live it.
          </p>
        </Reveal>

        {/* One vertical timeline, strict top-down reading order */}
        <div className="relative mt-16 sm:mt-20">
          {/* Rail */}
          <div className="hidden md:block absolute left-[7px] top-2 bottom-2 w-px bg-[var(--lp-line)]" aria-hidden="true" />

          <div className="space-y-16 sm:space-y-24">
            {acts.map(act => (
              <div key={act.time + act.title} className="relative md:pl-14">
                {/* Rail dot */}
                <span className="hidden md:flex absolute left-0 top-1.5 w-[15px] h-[15px] rounded-full border-[3px] border-[var(--lp-accent)] bg-[var(--lp-bg)]" aria-hidden="true" />

                <div className="grid md:grid-cols-[minmax(0,5fr)_minmax(0,6fr)] gap-8 md:gap-12 items-start">
                  <Reveal>
                    <ActText time={act.time} title={act.title}>
                      {act.text}
                    </ActText>
                  </Reveal>
                  <Reveal delayMs={120} className="min-w-0">
                    {act.visual}
                  </Reveal>
                </div>
              </div>
            ))}
          </div>
        </div>
      </div>
    </section>
  );
}
