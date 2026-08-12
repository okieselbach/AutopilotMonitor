"use client";

import { HelpMenuItems } from './HelpMenuItems';
import { MenuIcon } from './icons';
import type { NavbarMenus } from './useNavbarMenus';

/** Desktop help (?) dropdown. */
export function HelpMenu({ menus }: { menus: NavbarMenus }) {
  return (
    <div className="hidden sm:block relative" ref={menus.containerRef('help')}>
      <button
        onClick={() => menus.toggle('help')}
        className="p-2 rounded-lg hover:bg-gray-100 transition-colors"
        title="Help & Info"
      >
        <MenuIcon name="help" className="w-5 h-5 text-gray-600" />
      </button>

      {menus.isOpen('help') && (
        <div className="absolute right-0 mt-2 w-48 bg-white rounded-lg shadow-lg border border-gray-200 z-50 py-1">
          <HelpMenuItems variant="desktop" onNavigate={menus.close} />
        </div>
      )}
    </div>
  );
}
