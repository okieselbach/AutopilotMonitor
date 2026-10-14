"use client";

import type { ReactNode } from "react";
import type { ContextIcon, ContextItem } from "../lib/contextItems";

const ICONS: Record<ContextIcon, string> = {
  power: "M21 10.5h.375c.621 0 1.125.504 1.125 1.125v2.25c0 .621-.504 1.125-1.125 1.125H21M3.75 18h15A2.25 2.25 0 0021 15.75v-6a2.25 2.25 0 00-2.25-2.25h-15A2.25 2.25 0 001.5 9.75v6A2.25 2.25 0 003.75 18z",
  update: "M16.023 9.348h4.992v-.001M2.985 19.644v-4.992m0 0h4.992m-4.993 0l3.181 3.183a8.25 8.25 0 0013.803-3.7M4.031 9.865a8.25 8.25 0 0113.803-3.7l3.181 3.182m0-4.991v4.99",
  clock: "M12 6v6h4.5m4.5 0a9 9 0 11-18 0 9 9 0 0118 0z",
  memory: "M8.25 3v1.5M4.5 8.25H3m18 0h-1.5M4.5 12H3m18 0h-1.5m-15 3.75H3m18 0h-1.5M8.25 19.5V21M12 3v1.5m0 15V21m3.75-18v1.5m0 15V21m-9-1.5h10.5a2.25 2.25 0 002.25-2.25V6.75a2.25 2.25 0 00-2.25-2.25H6.75A2.25 2.25 0 004.5 6.75v10.5a2.25 2.25 0 002.25 2.25z",
  restart: "M16.023 9.348h4.992v-.001M2.985 19.644v-4.992m0 0h4.992m-4.993 0l3.181 3.183a8.25 8.25 0 0013.803-3.7M4.031 9.865a8.25 8.25 0 0113.803-3.7l3.181 3.182",
  eye: "M3.98 8.223A10.477 10.477 0 001.934 12C3.226 16.338 7.244 19.5 12 19.5c.993 0 1.953-.138 2.863-.395M6.228 6.228A10.45 10.45 0 0112 4.5c4.756 0 8.773 3.162 10.065 7.498a10.523 10.523 0 01-4.293 5.774M6.228 6.228L3 3m3.228 3.228l3.65 3.65m7.894 7.894L21 21m-3.228-3.228l-3.65-3.65m0 0a3 3 0 10-4.243-4.243m4.242 4.242L9.88 9.88",
  info: "M11.25 11.25l.041-.02a.75.75 0 011.063.852l-.708 2.836a.75.75 0 001.063.853l.041-.021M21 12a9 9 0 11-18 0 9 9 0 0118 0zm-9-3.75h.008v.008H12V8.25z",
};

/** Circumstances around the outcome: context findings and the limits of the observation. */
export default function ContextSection({ items, outcome, children }: { items: ContextItem[]; outcome: string; children?: ReactNode }) {
  if (items.length === 0 && !children) return null;
  return (
    <section className="rounded-lg bg-white p-5 shadow sm:p-6" aria-labelledby="context-title">
      <div className="flex flex-wrap items-baseline justify-between gap-2">
        <h2 id="context-title" className="text-lg font-semibold text-gray-900">
          {outcome === "failed" ? "Around the failure" : "Around this enrollment"}
        </h2>
        <p className="text-xs text-gray-500">Observed on the device. Context, not proof of cause.</p>
      </div>
      {items.length > 0 && (
        <ul className="mt-4 grid grid-cols-1 gap-3 sm:grid-cols-2">
          {items.map((item) => (
            <li key={item.key} className="flex gap-3 rounded-lg border border-gray-200 p-3">
              <span className="mt-0.5 flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-gray-100 text-gray-600">
                <svg className="h-4 w-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.75} aria-hidden="true">
                  <path strokeLinecap="round" strokeLinejoin="round" d={ICONS[item.icon]} />
                </svg>
              </span>
              <div className="min-w-0">
                <p className="text-sm font-medium text-gray-900">{item.title}</p>
                {item.detail && <p className="mt-0.5 break-words text-xs text-gray-500">{item.detail}</p>}
              </div>
            </li>
          ))}
        </ul>
      )}
      {children}
    </section>
  );
}
