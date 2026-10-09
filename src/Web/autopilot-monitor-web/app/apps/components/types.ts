import type { useAggregatedAdminScope } from "@/hooks";

/** Global-admin tenant scope shared by every Software-hub tab (read values + setters). */
export type SoftwareTabScope = ReturnType<typeof useAggregatedAdminScope>;

/** Default window (days) of the Software hub and the app detail page; a link back omits it. */
export const APPS_DEFAULT_WINDOW_DAYS = 30;
