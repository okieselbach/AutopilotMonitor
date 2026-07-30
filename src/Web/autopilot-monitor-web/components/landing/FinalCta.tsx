import { LoginButton } from "./LoginButton";
import { Reveal } from "./Reveal";
import { BrandMark } from "../BrandMark";

export function FinalCta() {
  return (
    <section className="py-20 sm:py-28 px-6">
      <Reveal className="max-w-4xl mx-auto">
        <div className="relative rounded-3xl bg-[var(--lp-term-bg)] border border-[var(--lp-term-line)] px-6 py-14 sm:px-14 text-center overflow-hidden">
          <div className="absolute inset-x-0 -top-24 h-48 bg-[radial-gradient(ellipse_at_center,rgba(51,177,97,0.25),transparent_70%)] pointer-events-none" />
          <BrandMark className="w-10 h-10 mx-auto mb-6" />
          <h2 className="text-3xl sm:text-4xl font-bold tracking-tight text-white text-balance">
            Your next enrollment doesn&apos;t have to be a black box.
          </h2>
          <p className="mt-4 text-lg text-[#9fb0c5] max-w-xl mx-auto">
            Free to use, open source, deployed in minutes. Watch your first live enrollment today.
          </p>
          <div className="mt-8 flex flex-wrap items-center justify-center gap-3">
            <LoginButton className="px-8 py-3.5 rounded-xl bg-[var(--lp-accent)] hover:brightness-110 text-white font-semibold text-lg shadow-lg transition-all hover:-translate-y-0.5">
              Start Monitoring Now
            </LoginButton>
          </div>
        </div>
      </Reveal>
    </section>
  );
}
