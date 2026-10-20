import type { ReactNode } from "react";
import { LockIcon } from "../ai/icons";

/**
 * A phone at its lock screen for the landing demos. Clock and day are static strings (no Date in
 * render); `children` sit where notifications appear. The caller crops the frame and animates
 * what goes inside.
 */
export function PhoneFrame({ children }: { children?: ReactNode }) {
  return (
    <div className="w-[232px] rounded-[2.6rem] bg-[var(--lp-ink)] p-[7px] shadow-2xl shadow-black/25">
      <div className="h-[470px] rounded-[2.17rem] bg-[var(--lp-term-bg)] px-2.5 pt-2.5">
        <div className="mx-auto h-[22px] w-[76px] rounded-full bg-[var(--lp-ink)]" />
        <div className="mt-4 flex flex-col items-center">
          <LockIcon className="h-3.5 w-3.5 text-[var(--lp-term-faint)]" />
          <p className="mt-1.5 text-[12px] font-medium leading-4 text-[var(--lp-term-faint)]">Tuesday</p>
          <p className="text-[54px] font-semibold leading-none tracking-tight tabular-nums text-[var(--lp-term-ink)]">10:07</p>
        </div>
        <div className="mt-5">{children}</div>
      </div>
    </div>
  );
}
