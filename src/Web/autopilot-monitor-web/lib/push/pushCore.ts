/**
 * The receiver pages' view of the pure push logic. The implementation lives once, in
 * public/push/sw-core.js, because the service worker loads that file as a module from the
 * static export; TypeScript reads its JSDoc types through allowJs.
 */
export * from "../../public/push/sw-core.js";
export type {
  HistoryEntry,
  Platform,
  PlatformInput,
  Severity,
  TraceRecord,
} from "../../public/push/sw-core.js";
