/** The parts of a click that decide whether a link opens in this tab. */
export interface LinkClick {
  button: number;
  metaKey: boolean;
  ctrlKey: boolean;
  shiftKey: boolean;
  altKey: boolean;
  defaultPrevented: boolean;
}

/** The parts of a link that decide where and how it opens. */
export interface ClickedLink {
  href: string;
  target: string;
  download: boolean;
}

/**
 * The in-app path a plain left click on this link opens in this tab, or null when the click opens a
 * new tab or window, downloads, or leaves for another site (the browser's own prompt covers that).
 */
export function sameTabLinkTarget(click: LinkClick, link: ClickedLink, origin: string): string | null {
  if (click.defaultPrevented || click.button !== 0) return null;
  if (click.metaKey || click.ctrlKey || click.shiftKey || click.altKey) return null;
  if ((link.target && link.target !== "_self") || link.download) return null;
  let url: URL;
  try {
    url = new URL(link.href, origin);
  } catch {
    return null;
  }
  if (url.origin !== origin) return null;
  return `${url.pathname}${url.search}${url.hash}`;
}

/** Whether an in-app path leaves the settings area, whose layout holds the unsaved edits. */
export function leavesSettings(path: string): boolean {
  const { pathname } = new URL(path, "https://portal.invalid");
  return !(pathname === "/settings" || pathname.startsWith("/settings/"));
}
