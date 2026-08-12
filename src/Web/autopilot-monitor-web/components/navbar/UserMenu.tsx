"use client";

import { deriveRoleBadges, getUserInitials, type NavbarUserView } from '@/lib/navbarPolicy';
import { MenuIcon } from './icons';
import type { NavbarMenus } from './useNavbarMenus';

const BADGE_CLASSES: Record<string, string> = {
  'global-admin': 'text-purple-800 bg-purple-100',
  'global-reader': 'text-purple-800 bg-purple-50',
  operator: 'text-blue-800 bg-blue-100',
  admin: 'text-green-800 bg-green-100',
};

interface UserMenuProps {
  user: NavbarUserView | null;
  menus: NavbarMenus;
  onLogout: () => void;
}

/** Avatar button + user dropdown, shared by the minimal and full navbar variants. */
export function UserMenu({ user, menus, onLogout }: UserMenuProps) {
  const initials = getUserInitials(user?.displayName, user?.upn);
  const badges = deriveRoleBadges(user);

  return (
    <div className="relative" ref={menus.containerRef('user')}>
      <button
        onClick={() => menus.toggle('user')}
        className="flex items-center space-x-1.5 p-1.5 rounded-lg hover:bg-gray-100 transition-colors"
      >
        <div className="w-7 h-7 rounded-full bg-green-600 flex items-center justify-center text-white font-semibold text-xs">
          {initials}
        </div>
        <MenuIcon name="chevronDown" className="w-3.5 h-3.5 text-gray-500" />
      </button>

      {menus.isOpen('user') && (
        <div className="absolute right-0 mt-2 w-72 bg-white rounded-lg shadow-lg border border-gray-200 z-50">
          <div className="px-3 py-2.5 border-b border-gray-200 flex items-start space-x-2.5">
            <div className="w-8 h-8 rounded-full bg-green-600 flex-shrink-0 flex items-center justify-center text-white font-semibold text-xs">
              {initials}
            </div>
            <div className="min-w-0">
              <p className="text-sm font-medium text-gray-900">{user?.displayName || 'User'}</p>
              <p className="text-xs text-gray-500 truncate">{user?.upn}</p>
              {badges.map((badge) => (
                <span
                  key={badge.key}
                  className={`inline-block mt-1.5 mr-1 px-1.5 py-0.5 text-[10px] font-semibold rounded-full ${BADGE_CLASSES[badge.key]}`}
                >
                  {badge.label}
                </span>
              ))}
            </div>
          </div>
          <div className="py-1">
            <button
              onClick={() => {
                onLogout();
                menus.close();
              }}
              className="w-full text-left px-3 py-2 text-sm text-gray-700 hover:bg-gray-100 flex items-center space-x-2"
            >
              <MenuIcon name="signOut" className="w-4 h-4 text-gray-400" />
              <span>Sign out</span>
            </button>
          </div>
        </div>
      )}
    </div>
  );
}
