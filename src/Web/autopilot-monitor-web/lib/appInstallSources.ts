import { SHARED_MANIFEST } from "@/utils/shared-manifests.generated";

/** Install channels of the app rows (backend `AppInstallSources`). */
export const APP_INSTALL_SOURCES = SHARED_MANIFEST.appInstallSources;
export type AppInstallSource = (typeof APP_INSTALL_SOURCES)[number];

/** The Intune channel: the default of every per-app endpoint, left out of URLs. */
export const DEFAULT_APP_INSTALL_SOURCE: AppInstallSource = "ime";

/** Reads a `?source=` value; anything unknown falls back to the Intune channel. */
export function parseAppInstallSource(raw: string | null | undefined): AppInstallSource {
  return (APP_INSTALL_SOURCES as readonly string[]).includes(raw ?? "")
    ? (raw as AppInstallSource)
    : DEFAULT_APP_INSTALL_SOURCE;
}

/** The query value for a channel: undefined for the default, so Intune URLs stay unchanged. */
export function appInstallSourceParam(source: string | undefined): string | undefined {
  return source && source !== DEFAULT_APP_INSTALL_SOURCE ? source : undefined;
}
