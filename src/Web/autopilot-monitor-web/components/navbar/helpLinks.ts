import { DOCS_URL } from '@/utils/config';
import type { IconName } from './icons';

export interface HelpLink {
  key: string;
  label: string;
  href: string;
  /** External links open in a new tab; internal ones use next/link. */
  external: boolean;
  icon: IconName;
  /** Render a divider above this entry. */
  dividerBefore?: boolean;
}

/**
 * The one help-menu link list, rendered by both the desktop help dropdown and
 * the mobile overflow submenu. Add entries here — never in a surface component.
 */
export const HELP_LINKS: HelpLink[] = [
  { key: 'docs', label: 'Documentation', href: DOCS_URL, external: true, icon: 'book' },
  {
    key: 'changelog',
    label: 'Changelog',
    href: `${DOCS_URL}/changelog/platform-changelog`,
    external: true,
    icon: 'clipboard',
  },
  {
    key: 'announcements',
    label: 'Service Announcements',
    href: `${DOCS_URL}/troubleshooting/service-announcements`,
    external: true,
    icon: 'warningTriangle',
  },
  {
    key: 'privacy',
    label: 'Privacy Policy',
    href: '/privacy',
    external: false,
    icon: 'shieldCheck',
    dividerBefore: true,
  },
  { key: 'terms', label: 'Terms of Use', href: '/terms', external: false, icon: 'documentText' },
  {
    key: 'imprint',
    label: 'Imprint',
    href: 'https://www.glueckkanja.com/en/imprint',
    external: true,
    icon: 'building',
  },
];
