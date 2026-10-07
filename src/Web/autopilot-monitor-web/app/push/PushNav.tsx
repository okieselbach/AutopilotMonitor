"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { BrandMark } from "@/components/BrandMark";

const ITEMS = [
  { href: "/push", label: "History" },
  { href: "/push/status", label: "Status" },
] as const;

export function PushNav() {
  const pathname = (usePathname() ?? "/push").replace(/\/$/, "") || "/push";
  return (
    <header className="bg-white border-b border-gray-200 sticky top-0 z-10">
      <div className="max-w-md mx-auto px-4 h-12 flex items-center justify-between">
        <Link href="/push" className="flex items-center gap-2 min-w-0">
          <BrandMark className="w-5 h-5 shrink-0" />
          <span className="text-sm font-semibold text-gray-900 truncate">Autopilot Monitor Alerts</span>
        </Link>
        <nav className="flex items-center gap-1 text-sm" aria-label="Receiver">
          {ITEMS.map((item) => {
            const active = pathname === item.href;
            return (
              <Link
                key={item.href}
                href={item.href}
                aria-current={active ? "page" : undefined}
                className={`px-2 py-1 rounded-md transition-colors ${
                  active ? "bg-gray-100 text-gray-900 font-medium" : "text-gray-600 hover:bg-gray-50 hover:text-gray-900"
                }`}
              >
                {item.label}
              </Link>
            );
          })}
        </nav>
      </div>
    </header>
  );
}
