"use client";

import { Fragment } from 'react';
import Link from 'next/link';
import { trustedRoute } from '@/lib/routes';
import { HELP_LINKS } from './helpLinks';
import { MenuIcon } from './icons';

interface HelpMenuItemsProps {
  onNavigate: () => void;
  /** 'overflow' adds the dark-mode classes the mobile menu uses. */
  variant: 'desktop' | 'overflow';
}

/** The shared help-link list; both help surfaces render exactly this. */
export function HelpMenuItems({ onNavigate, variant }: HelpMenuItemsProps) {
  const itemClass =
    variant === 'overflow'
      ? 'w-full flex items-center gap-2.5 px-3 py-2 text-sm text-gray-700 dark:text-gray-200 hover:bg-gray-50 dark:hover:bg-gray-700 transition-colors'
      : 'w-full flex items-center gap-2.5 px-3 py-2 text-sm text-gray-700 hover:bg-gray-50 transition-colors';
  const dividerClass =
    variant === 'overflow'
      ? 'border-t border-gray-100 dark:border-gray-700 my-1'
      : 'border-t border-gray-100 my-1';

  return (
    <>
      {HELP_LINKS.map((link) => {
        const content = (
          <>
            <MenuIcon name={link.icon} className="w-4 h-4 text-gray-400" />
            <span>{link.label}</span>
          </>
        );
        return (
          <Fragment key={link.key}>
            {link.dividerBefore && <div className={dividerClass}></div>}
            {link.external ? (
              <a
                href={link.href}
                target="_blank"
                rel="noopener noreferrer"
                className={itemClass}
                onClick={onNavigate}
              >
                {content}
              </a>
            ) : (
              <Link
                href={trustedRoute(link.href)}
                prefetch={false}
                className={itemClass}
                onClick={onNavigate}
              >
                {content}
              </Link>
            )}
          </Fragment>
        );
      })}
    </>
  );
}
