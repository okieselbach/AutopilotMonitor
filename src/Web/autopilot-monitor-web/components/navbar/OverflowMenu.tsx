"use client";

import { useState } from 'react';
import type { NavbarCapabilities } from '@/lib/navbarPolicy';
import { AdminModeToggles } from './AdminModeToggles';
import { HelpMenuItems } from './HelpMenuItems';
import { MenuIcon } from './icons';
import type { NavbarMenus } from './useNavbarMenus';

interface OverflowMenuProps {
  menus: NavbarMenus;
  capabilities: NavbarCapabilities;
  isGlobalAdmin: boolean;
  theme: string;
  onToggleTheme: () => void;
}

/**
 * Mobile (<sm) overflow ("...") menu: hosts the theme toggle plus the same
 * settings toggles and help links the desktop dropdowns show, via the shared
 * AdminModeToggles / HelpMenuItems components.
 */
export function OverflowMenu({
  menus,
  capabilities,
  isGlobalAdmin,
  theme,
  onToggleTheme,
}: OverflowMenuProps) {
  const [submenu, setSubmenu] = useState<'help' | 'settings' | null>(null);
  const open = menus.isOpen('overflow');

  const itemClass =
    'w-full flex items-center justify-between px-3 py-2 text-sm text-gray-700 dark:text-gray-200 hover:bg-gray-50 dark:hover:bg-gray-700 transition-colors';
  const backClass =
    'w-full flex items-center gap-2 px-3 py-2 text-sm font-medium text-gray-700 dark:text-gray-200 hover:bg-gray-50 dark:hover:bg-gray-700 transition-colors';
  const dividerClass = 'border-t border-gray-100 dark:border-gray-700 my-1';

  return (
    <div className="sm:hidden relative" ref={menus.containerRef('overflow')}>
      {/* Reset to the top-level menu on every open/close via the trigger — the
          only way back in after an outside-click close is this button. */}
      <button
        onClick={() => {
          menus.toggle('overflow');
          setSubmenu(null);
        }}
        className="p-2 rounded-lg hover:bg-gray-100 transition-colors"
        title="More"
      >
        <MenuIcon name="dots" className="w-5 h-5 text-gray-600" />
      </button>

      {open && (
        <div className="absolute right-0 mt-2 w-56 bg-white dark:bg-gray-800 rounded-lg shadow-lg border border-gray-200 dark:border-gray-700 z-50 py-1">
          {/* ── Main overflow menu ── */}
          {!submenu && (
            <>
              <button onClick={() => { onToggleTheme(); menus.close(); }} className={itemClass}>
                <div className="flex items-center gap-2.5">
                  {theme === 'dark' ? (
                    <MenuIcon name="sun" className="w-4 h-4 text-yellow-500" />
                  ) : (
                    <MenuIcon name="moon" className="w-4 h-4 text-gray-400" />
                  )}
                  <span>{theme === 'dark' ? 'Light Mode' : 'Dark Mode'}</span>
                </div>
              </button>

              <div className={dividerClass}></div>

              {capabilities.showSettingsMenu && (
                <button onClick={() => setSubmenu('settings')} className={itemClass}>
                  <div className="flex items-center gap-2.5">
                    <MenuIcon name={['gearOuter', 'gearInner']} className="w-4 h-4 text-gray-400" />
                    <span>Settings</span>
                  </div>
                  <MenuIcon name="chevronRight" className="w-3.5 h-3.5 text-gray-400" />
                </button>
              )}

              <button onClick={() => setSubmenu('help')} className={itemClass}>
                <div className="flex items-center gap-2.5">
                  <MenuIcon name="help" className="w-4 h-4 text-gray-400" />
                  <span>Help</span>
                </div>
                <MenuIcon name="chevronRight" className="w-3.5 h-3.5 text-gray-400" />
              </button>
            </>
          )}

          {/* ── Settings submenu ── */}
          {submenu === 'settings' && (
            <>
              <button onClick={() => setSubmenu(null)} className={backClass}>
                <MenuIcon name="chevronLeft" className="w-3.5 h-3.5" />
                Settings
              </button>
              <div className={dividerClass}></div>
              <div className="px-3 py-2">
                <AdminModeToggles
                  capabilities={capabilities}
                  isGlobalAdmin={isGlobalAdmin}
                  variant="overflow"
                />
              </div>
            </>
          )}

          {/* ── Help submenu ── */}
          {submenu === 'help' && (
            <>
              <button onClick={() => setSubmenu(null)} className={backClass}>
                <MenuIcon name="chevronLeft" className="w-3.5 h-3.5" />
                Help
              </button>
              <div className={dividerClass}></div>
              <HelpMenuItems variant="overflow" onNavigate={menus.close} />
            </>
          )}
        </div>
      )}
    </div>
  );
}
