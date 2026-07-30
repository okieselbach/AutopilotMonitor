import { Reveal } from "./Reveal";
import { StoryAnalysis } from "./StoryAnalysis";
import { McpTerminalDemo } from "./McpTerminalDemo";
import { StoryFleet } from "./StoryFleet";

function ActHeader({
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

/**
 * The scroll story: one enrollment, four acts. The reader experiences
 * exactly what they would experience in the product.
 */
export function Story() {
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

        {/* Act 1 + 2 — device powers on, something goes wrong */}
        <div className="mt-16 sm:mt-20 grid md:grid-cols-2 gap-8 md:gap-14 items-center">
          <Reveal>
            <ActHeader time="09:02" title="A device powers on somewhere.">
              <p>
                A new hire unboxes their laptop three time zones away. The bootstrapper you assigned
                once in Intune kicks in, and the enrollment appears on your dashboard — live.
              </p>
              <p>
                Every phase, every app install, every reboot streams in as it happens. No remote
                session, no guessing, no &ldquo;can you send a photo of the screen?&rdquo;
              </p>
            </ActHeader>
          </Reveal>
          <Reveal delayMs={120}>
            <ActHeader time="09:41" title="Something's wrong.">
              <p>
                Device Setup has been running for 39 minutes. Without monitoring, this is where you
                stare at a frozen ESP spinner and start guessing.
              </p>
              <p>
                Instead, an analyze rule fires and names the stuck app —{" "}
                <span className="font-semibold text-[var(--lp-ink)]">before anyone opens a ticket</span>. Rules are
                community-driven and fully customizable.
              </p>
            </ActHeader>
          </Reveal>
        </div>

        <Reveal className="mt-10 max-w-2xl mx-auto md:ml-auto md:mr-0">
          <StoryAnalysis />
        </Reveal>

        {/* Act 3 — just ask */}
        <div className="mt-20 sm:mt-24 grid md:grid-cols-[1fr_1.2fr] gap-8 md:gap-14 items-center">
          <Reveal>
            <ActHeader time="09:43" title="Just ask.">
              <p>
                Your AI assistant is connected to Autopilot Monitor through the built-in MCP server.
                One question — and it reads the whole session for you.
              </p>
              <p>
                Phase durations, time attribution, unexplained gaps, detected issues, the likely
                root cause and what to do about it. A complete debrief in seconds, from tools like{" "}
                <code className="font-mono text-[13px] px-1.5 py-0.5 rounded bg-[var(--lp-surface-2)] text-[var(--lp-accent-ink)]">get_session_summary</code>{" "}
                and{" "}
                <code className="font-mono text-[13px] px-1.5 py-0.5 rounded bg-[var(--lp-surface-2)] text-[var(--lp-accent-ink)]">get_time_attribution</code>.
              </p>
            </ActHeader>
          </Reveal>
          <Reveal delayMs={120} className="min-w-0">
            <McpTerminalDemo />
          </Reveal>
        </div>

        {/* Act 4 — multiply by 500 */}
        <div className="mt-20 sm:mt-24 grid md:grid-cols-[1.2fr_1fr] gap-8 md:gap-14 items-center">
          <Reveal className="order-2 md:order-1 min-w-0">
            <StoryFleet />
          </Reveal>
          <Reveal delayMs={120} className="order-1 md:order-2">
            <ActHeader time="Later" title="Now multiply by 500.">
              <p>
                One device is a story. Five hundred are a pattern. Fleet health shows success rates,
                duration trends, and which app or hardware model is quietly costing you the most.
              </p>
              <p>
                The VPN client from this morning? It&apos;s in 62% of your slow enrollments. Now you
                know what to fix first — with data, not anecdotes.
              </p>
            </ActHeader>
          </Reveal>
        </div>
      </div>
    </section>
  );
}
