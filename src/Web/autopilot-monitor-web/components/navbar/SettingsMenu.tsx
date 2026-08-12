"use client";

import type { NavbarCapabilities } from '@/lib/navbarPolicy';
import { AdminModeToggles } from './AdminModeToggles';
import { MenuIcon } from './icons';
import type { NavbarMenus } from './useNavbarMenus';

interface SettingsMenuProps {
  menus: NavbarMenus;
  capabilities: NavbarCapabilities;
  isGlobalAdmin: boolean;
}

/**
 * Desktop settings (gear) dropdown. Callers gate rendering on
 * capabilities.showSettingsMenu so users without any toggle never get an
 * empty panel.
 */
export function SettingsMenu({ menus, capabilities, isGlobalAdmin }: SettingsMenuProps) {
  return (
    <div className="hidden sm:block relative" ref={menus.containerRef('settings')}>
      <button
        onClick={() => menus.toggle('settings')}
        className="p-2 rounded-lg hover:bg-gray-100 transition-colors"
        title="Settings"
      >
        <MenuIcon name={['gearOuter', 'gearInner']} className="w-5 h-5 text-gray-600" />
      </button>

      {menus.isOpen('settings') && (
        <div className="absolute right-0 mt-2 w-64 bg-white rounded-lg shadow-lg border border-gray-200 z-50 max-h-[32rem] overflow-y-auto">
          <div className="p-3">
            <AdminModeToggles
              capabilities={capabilities}
              isGlobalAdmin={isGlobalAdmin}
              variant="desktop"
            />
          </div>
        </div>
      )}
    </div>
  );
}
