"use client";

import Link from 'next/link';
import { usePathname, useRouter } from 'next/navigation';
import { useAuth } from '@/contexts/AuthContext';
import { useTheme } from '@/contexts/ThemeContext';
import { useAdminMode } from '@/hooks/useAdminMode';
import { trustedRoute } from '@/lib/routes';
import {
  deriveNavbarCapabilities,
  includeGlobalNotifications,
  resolveNavbarVariant,
} from '@/lib/navbarPolicy';
import { BrandMark } from './BrandMark';
import GlobalSearch from './GlobalSearch';
import { HelpMenu } from './navbar/HelpMenu';
import { MenuIcon } from './navbar/icons';
import { NotificationBell } from './navbar/NotificationBell';
import { OverflowMenu } from './navbar/OverflowMenu';
import { SettingsMenu } from './navbar/SettingsMenu';
import { UserMenu } from './navbar/UserMenu';
import { useNavbarMenus } from './navbar/useNavbarMenus';

/**
 * Top navbar. All gating decisions (which variant renders, which menus and
 * dismiss actions a user gets) come from lib/navbarPolicy.ts — keep new rules
 * there, not inline, so they stay unit-tested and identical across the desktop
 * and mobile surfaces.
 */
export default function Navbar() {
  const { isAuthenticated, user, hasGlobalScope, logout } = useAuth();
  const { theme, toggleTheme } = useTheme();
  const { globalAdminMode } = useAdminMode();
  const pathname = usePathname();
  const router = useRouter();
  const menus = useNavbarMenus();

  const variant = resolveNavbarVariant({ pathname, isAuthenticated, user, hasGlobalScope });
  if (variant === 'hidden') {
    return null;
  }

  const capabilities = deriveNavbarCapabilities(user, hasGlobalScope);
  const includeGlobal = includeGlobalNotifications(hasGlobalScope, globalAdminMode);

  // Notification rows with an href navigate on click — close the dropdown first
  // so it doesn't linger over the target page.
  const openNotificationHref = (href: string) => {
    menus.close();
    router.push(trustedRoute(href));
  };

  // Regular users (non-Admin, non-Operator, no platform scope): minimal navbar
  // with only the Progress Portal brand link and the user menu.
  if (variant === 'minimal') {
    return (
      <nav className="bg-white border-b border-gray-200 shadow-sm sticky top-0 z-30">
        <div className="px-3">
          <div className="flex justify-between h-14">
            <div className="flex items-center">
              <Link href="/progress" prefetch={false} className="flex items-center space-x-2.5">
                <BrandMark className="w-6 h-6" />
                <span className="text-[15px] font-bold tracking-tight text-gray-900 dark:text-gray-100">
                  <span className="hidden md:inline">Autopilot Monitor</span>
                  <span className="md:hidden">AP Monitor</span>
                </span>
              </Link>
            </div>
            <div className="flex items-center space-x-1">
              <UserMenu user={user} menus={menus} onLogout={logout} />
            </div>
          </div>
        </div>
      </nav>
    );
  }

  return (
    <nav className="bg-white border-b border-gray-200 shadow-sm sticky top-0 z-30">
      <div className="px-3">
        <div className="flex justify-between h-14">
          {/* Logo and Title */}
          <div className="flex items-center">
            <Link href="/" prefetch={false} className="flex items-center space-x-2.5">
              <BrandMark className="w-6 h-6" />
              <span className="text-[15px] font-bold tracking-tight text-gray-900 dark:text-gray-100">
                <span className="hidden lg:inline">Autopilot Monitor</span>
                <span className="hidden md:inline lg:hidden">AP Monitor</span>
                <span className="md:hidden">AP Mon</span>
              </span>
            </Link>
          </div>

          {/* Global Search — centered on desktop, lupe pushed right on mobile */}
          <GlobalSearch />

          {/* Right side — Dark Mode, Notifications, Settings, Help, Overflow, User */}
          <div className="flex items-center space-x-1">
            {/* Dark Mode Toggle — hidden on <sm, moved to overflow */}
            <button
              onClick={toggleTheme}
              className="hidden sm:block p-2 rounded-lg hover:bg-gray-100 transition-colors"
              title={theme === 'dark' ? 'Switch to light mode' : 'Switch to dark mode'}
            >
              {theme === 'dark' ? (
                <MenuIcon name="sun" className="w-5 h-5 text-yellow-500" />
              ) : (
                <MenuIcon name="moon" className="w-5 h-5 text-gray-600" />
              )}
            </button>

            {/* Notification Bell — always visible (including mobile) */}
            <NotificationBell
              menus={menus}
              capabilities={capabilities}
              includeGlobal={includeGlobal}
              onOpenHref={openNotificationHref}
            />

            {/* Settings + Help — hidden on <sm, moved to overflow */}
            {capabilities.showSettingsMenu && (
              <SettingsMenu
                menus={menus}
                capabilities={capabilities}
                isGlobalAdmin={user?.isGlobalAdmin ?? false}
              />
            )}
            <HelpMenu menus={menus} />

            {/* Overflow Menu (...) — visible only on <sm */}
            <OverflowMenu
              menus={menus}
              capabilities={capabilities}
              isGlobalAdmin={user?.isGlobalAdmin ?? false}
              theme={theme}
              onToggleTheme={toggleTheme}
            />

            <UserMenu user={user} menus={menus} onLogout={logout} />
          </div>
        </div>
      </div>
    </nav>
  );
}
