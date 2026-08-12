"use client";

import { trackEvent } from '@/lib/appInsights';
import type { NavbarCapabilities } from '@/lib/navbarPolicy';
import { useAdminMode } from '@/hooks/useAdminMode';
import { MenuIcon } from './icons';

function ToggleSwitch({
  enabled,
  onColor,
  onToggle,
}: {
  enabled: boolean;
  onColor: string;
  onToggle: () => void;
}) {
  return (
    <button
      onClick={onToggle}
      className={`relative inline-flex h-5 w-9 items-center rounded-full transition-colors ${enabled ? onColor : 'bg-gray-300'}`}
    >
      <span
        className={`inline-block h-3.5 w-3.5 transform rounded-full bg-white transition-transform ${enabled ? 'translate-x-[18px]' : 'translate-x-[3px]'}`}
      />
    </button>
  );
}

interface AdminModeTogglesProps {
  capabilities: NavbarCapabilities;
  isGlobalAdmin: boolean;
  /** 'overflow' adds the dark-mode classes the mobile menu uses. */
  variant: 'desktop' | 'overflow';
}

/**
 * The Admin Mode / Global Mode toggle rows, shared between the desktop settings
 * dropdown and the mobile overflow submenu so the two can't drift apart.
 * Renders nothing when the caller has no toggles (the surfaces are hidden then
 * anyway, via capabilities.showSettingsMenu).
 */
export function AdminModeToggles({ capabilities, isGlobalAdmin, variant }: AdminModeTogglesProps) {
  const { adminMode, setAdminMode, globalAdminMode, setGlobalAdminMode } = useAdminMode();
  const dark = variant === 'overflow';
  const labelClass = dark ? 'text-sm text-gray-700 dark:text-gray-200' : 'text-sm text-gray-700';

  if (!capabilities.showAdminToggle && !capabilities.showGlobalToggle) {
    return null;
  }

  return (
    <>
      <p className="text-[11px] font-semibold uppercase tracking-wider text-gray-400 mb-2">
        Administration
      </p>

      {capabilities.showAdminToggle && (
        <div
          className={`flex items-center justify-between py-2 px-2.5 rounded-md mb-1 ${dark ? 'bg-gray-50 dark:bg-gray-700' : 'bg-gray-50'}`}
        >
          <div className="flex items-center gap-1.5">
            <MenuIcon name="shieldCheck" className="w-4 h-4 text-gray-500" />
            <span className={labelClass}>Admin Mode</span>
            {adminMode && <span className="text-[10px] text-amber-600 font-semibold">ON</span>}
          </div>
          <ToggleSwitch
            enabled={adminMode}
            onColor="bg-amber-500"
            onToggle={() => {
              trackEvent('admin_mode_toggled', { enabled: !adminMode });
              setAdminMode(!adminMode);
            }}
          />
        </div>
      )}

      {/* Global scope toggle — Global Admin or read-only Global Reader */}
      {capabilities.showGlobalToggle && (
        <div
          className={`flex items-center justify-between py-2 px-2.5 rounded-md mb-1 ${dark ? 'bg-purple-50 dark:bg-purple-900/30' : 'bg-purple-50'}`}
        >
          <div className="flex items-center gap-1.5">
            <MenuIcon name="globe" className="w-4 h-4 text-purple-500" />
            <span className={labelClass}>{isGlobalAdmin ? 'Global Admin' : 'Global View'}</span>
            {globalAdminMode && (
              <span className="text-[10px] text-purple-700 font-semibold">ON</span>
            )}
          </div>
          <ToggleSwitch
            enabled={globalAdminMode}
            onColor="bg-purple-600"
            onToggle={() => {
              trackEvent('admin_mode_toggled', { enabled: !globalAdminMode, isGlobal: true });
              setGlobalAdminMode(!globalAdminMode);
            }}
          />
        </div>
      )}
    </>
  );
}
