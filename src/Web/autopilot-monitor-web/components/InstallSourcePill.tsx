import type { InstallSource } from "@/lib/installProgress";

// Source pills. Intune/IME is the default and deliberately carries no pill — labelling
// every ordinary app row would be noise. Only rows the customer would otherwise mistake
// for a duplicate Intune app get one, with a tooltip explaining what they are looking at.
const SOURCE_PILLS: Partial<Record<InstallSource, { label: string; title: string }>> = {
  "office-c2r": {
    label: "Click-to-Run",
    title:
      "Observed directly from the Office Click-to-Run installer, not from Intune. " +
      "If you also deploy Microsoft 365 Apps as your own Win32 app, that row shows Intune's view of " +
      "the deployment — this row shows when Office actually finished laying itself down on disk, " +
      "which is usually much later.",
  },
  realmjoin: {
    label: "RealmJoin",
    title: "Package installed by the RealmJoin agent, not by Intune's management extension.",
  },
};

/** Label of an install channel for filters and headings; the IME channel reads as Intune. */
export function installSourceLabel(source: string): string {
  return source === "realmjoin" ? "RealmJoin" : source === "office-c2r" ? "Click-to-Run" : "Intune";
}

export function InstallSourcePill({ source, className = "" }: { source: string | undefined; className?: string }) {
  const pill = source && Object.prototype.hasOwnProperty.call(SOURCE_PILLS, source)
    ? SOURCE_PILLS[source as InstallSource]
    : undefined;
  if (!pill) return null;
  return (
    // Neutral outline so the source never competes with the coloured state badge
    // next to it. Dark mode is covered by the global gray-family overrides.
    <span
      className={`text-xs px-2 py-0.5 rounded-full bg-gray-100 border border-gray-300 text-gray-600 font-medium whitespace-nowrap${className ? ` ${className}` : ""}`}
      title={pill.title}
    >
      {pill.label}
    </span>
  );
}
