"use client";

import { useAuth } from '@/contexts/AuthContext';
import { useNotifications } from '@/contexts/NotificationContext';
import { useGlobalNotifications } from '@/contexts/GlobalNotificationContext';
import { useTenantNotifications } from '@/contexts/TenantNotificationContext';
import { useTheme } from '@/contexts/ThemeContext';
import { useState, useRef, useEffect } from 'react';
import Link from 'next/link';
import { usePathname } from 'next/navigation';
import { trackEvent } from '@/lib/appInsights';
import { useAdminMode } from '@/hooks/useAdminMode';
import GlobalSearch from './GlobalSearch';

export default function Navbar() {
  const { isAuthenticated, user, hasGlobalScope, logout } = useAuth();
  const { notifications, unreadCount, markAsRead, markAllAsRead, removeNotification, clearAll } = useNotifications();
  const { notifications: globalNotifications, dismissNotification: dismissGlobal, dismissAll: dismissAllGlobal } = useGlobalNotifications();
  const { tenantNotifications, dismissTenantNotification, dismissAllTenant } = useTenantNotifications();
  const { theme, toggleTheme } = useTheme();
  const pathname = usePathname();

  const [showNotifications, setShowNotifications] = useState(false);
  const [showUserMenu, setShowUserMenu] = useState(false);
  const [showSettings, setShowSettings] = useState(false);
  const [showHelp, setShowHelp] = useState(false);
  const [showOverflow, setShowOverflow] = useState(false);
  const [overflowSubmenu, setOverflowSubmenu] = useState<'help' | 'settings' | null>(null);
  const { adminMode, setAdminMode, globalAdminMode, setGlobalAdminMode } = useAdminMode();
  const [previewMode, setPreviewMode] = useState(() => {
    if (typeof window !== 'undefined') {
      return localStorage.getItem('previewMode') === 'true';
    }
    return false;
  });

  const notificationRef = useRef<HTMLDivElement>(null);
  const userMenuRef = useRef<HTMLDivElement>(null);
  const settingsRef = useRef<HTMLDivElement>(null);
  const helpRef = useRef<HTMLDivElement>(null);
  const overflowRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (typeof window !== 'undefined') {
      localStorage.setItem('previewMode', previewMode.toString());
      window.dispatchEvent(new Event('localStorageChange'));
    }
  }, [previewMode]);

  // Seed preview mode from ?preview=1 URL parameter (one-shot)
  useEffect(() => {
    if (typeof window !== 'undefined') {
      const params = new URLSearchParams(window.location.search);
      const previewParam = params.get('preview');
      if (previewParam === '1' || previewParam === 'true') {
        setPreviewMode(true);
        // Clean up URL
        params.delete('preview');
        const cleanUrl = params.toString()
          ? `${window.location.pathname}?${params.toString()}`
          : window.location.pathname;
        window.history.replaceState({}, '', cleanUrl);
      } else if (previewParam === '0' || previewParam === 'false') {
        setPreviewMode(false);
        params.delete('preview');
        const cleanUrl = params.toString()
          ? `${window.location.pathname}?${params.toString()}`
          : window.location.pathname;
        window.history.replaceState({}, '', cleanUrl);
      }
    }
  }, []);

  // Close dropdowns when clicking outside
  useEffect(() => {
    function handleClickOutside(event: MouseEvent) {
      if (notificationRef.current && !notificationRef.current.contains(event.target as Node)) {
        setShowNotifications(false);
      }
      if (userMenuRef.current && !userMenuRef.current.contains(event.target as Node)) {
        setShowUserMenu(false);
      }
      if (settingsRef.current && !settingsRef.current.contains(event.target as Node)) {
        setShowSettings(false);
      }
      if (helpRef.current && !helpRef.current.contains(event.target as Node)) {
        setShowHelp(false);
      }
      if (overflowRef.current && !overflowRef.current.contains(event.target as Node)) {
        setShowOverflow(false);
        setOverflowSubmenu(null);
      }
    }

    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, []);

  // Don't render navbar on landing page (root route)
  if (pathname === '/') {
    return null;
  }

  // Don't render navbar if not authenticated
  if (!isAuthenticated) {
    return null;
  }

  const getNotificationIcon = (type: string) => {
    switch (type) {
      case 'error':
        return '🔴';
      case 'warning':
        return '⚠️';
      case 'success':
        return '✅';
      default:
        return 'ℹ️';
    }
  };

  const formatTime = (date: Date) => {
    const now = new Date();
    const diffMs = now.getTime() - new Date(date).getTime();
    const diffMins = Math.floor(diffMs / 60000);

    if (diffMins < 1) return 'Just now';
    if (diffMins < 60) return `${diffMins}m ago`;
    const diffHours = Math.floor(diffMins / 60);
    if (diffHours < 24) return `${diffHours}h ago`;
    return `${Math.floor(diffHours / 24)}d ago`;
  };

  const getUserInitials = () => {
    if (user?.displayName) {
      const names = user.displayName.split(' ');
      if (names.length >= 2) {
        return `${names[0].charAt(0)}${names[names.length - 1].charAt(0)}`.toUpperCase();
      }
      return user.displayName.charAt(0).toUpperCase();
    }
    return user?.upn?.charAt(0).toUpperCase() || 'U';
  };

  const isTenantAdmin = user?.isTenantAdmin ?? false;
  const isOperator = user?.role === 'Operator';
  const isAdminOrOperator = isTenantAdmin || isOperator;
  // Tenant notification dismiss is currently tenant-shared (clearing for one user clears for
  // all). Only Admins / Global Admins are permitted to dismiss; Operators and Viewers see the
  // bell as read-only. See TenantNotificationContext + EndpointAccessPolicyCatalog.
  const canDismissTenant = isTenantAdmin || (user?.isGlobalAdmin ?? false);

  // Regular users (non-Admin, non-Operator, no platform scope): show minimal navbar with only
  // Progress Portal. A read-only Global Reader has platform scope → gets the full navbar.
  if (!isAdminOrOperator && !hasGlobalScope) {
    return (
      <nav className="bg-white border-b border-gray-200 shadow-sm sticky top-0 z-30">
        <div className="px-3">
          <div className="flex justify-between h-14">
            <div className="flex items-center">
              <Link href="/progress" className="flex items-center space-x-2.5">
                <div className="w-8 h-8 bg-gradient-to-br from-blue-600 to-indigo-600 rounded-lg flex items-center justify-center">
                  <svg className="w-5 h-5 text-white" viewBox="0 0 24 24" fill="none">
                    <rect x="5.0" y="12.2" width="2.8" height="7.8" rx="0.9" fill="currentColor" />
                    <rect x="10.6" y="10.9" width="2.8" height="9.1" rx="0.9" fill="currentColor" />
                    <rect x="16.2" y="8.6" width="2.8" height="11.4" rx="0.9" fill="currentColor" />
                    <path d="M4.4 8.9L8.6 6.8L12.0 7.4L15.4 5.5L18.8 4.9" stroke="currentColor" strokeWidth="1" strokeLinecap="round" strokeLinejoin="round" />
                    <path d="M17.8 4.2L19.1 4.9L17.9 5.9" stroke="currentColor" strokeWidth="1" strokeLinecap="round" strokeLinejoin="round" />
                  </svg>
                </div>
                <span className="text-lg font-bold bg-gradient-to-r from-blue-600 to-indigo-600 bg-clip-text text-transparent">
                  <span className="hidden md:inline">AutopilotMonitor</span>
                  <span className="md:hidden">AP Monitor</span>
                </span>
              </Link>
            </div>
            <div className="flex items-center space-x-1">
              {/* User Menu */}
              <div className="relative" ref={userMenuRef}>
                <button onClick={() => setShowUserMenu(!showUserMenu)} className="flex items-center space-x-1.5 p-1.5 rounded-lg hover:bg-gray-100 transition-colors">
                  <div className="w-7 h-7 rounded-full bg-blue-600 flex items-center justify-center text-white font-semibold text-xs">
                    {getUserInitials()}
                  </div>
                  <svg className="w-3.5 h-3.5 text-gray-500" fill="none" strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" viewBox="0 0 24 24" stroke="currentColor">
                    <path d="M19 9l-7 7-7-7"></path>
                  </svg>
                </button>
                {showUserMenu && (
                  <div className="absolute right-0 mt-2 w-72 bg-white rounded-lg shadow-lg border border-gray-200 z-50">
                    <div className="px-3 py-2.5 border-b border-gray-200 flex items-start space-x-2.5">
                      <div className="w-8 h-8 rounded-full bg-blue-600 flex-shrink-0 flex items-center justify-center text-white font-semibold text-xs">
                        {getUserInitials()}
                      </div>
                      <div className="min-w-0">
                        <p className="text-sm font-medium text-gray-900">{user?.displayName || 'User'}</p>
                        <p className="text-xs text-gray-500 truncate">{user?.upn}</p>
                      </div>
                    </div>
                    <div className="py-1">
                      <button onClick={() => { logout(); setShowUserMenu(false); }} className="w-full text-left px-3 py-2 text-sm text-gray-700 hover:bg-gray-100 flex items-center space-x-2">
                        <svg className="w-4 h-4 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                          <path strokeLinecap="round" strokeLinejoin="round" d="M17 16l4-4m0 0l-4-4m4 4H7m6 4v1a3 3 0 01-3 3H6a3 3 0 01-3-3V7a3 3 0 013-3h4a3 3 0 013 3v1" />
                        </svg>
                        <span>Sign out</span>
                      </button>
                    </div>
                  </div>
                )}
              </div>
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
            <Link href="/" className="flex items-center space-x-2.5">
              <div className="w-8 h-8 bg-gradient-to-br from-blue-600 to-indigo-600 rounded-lg flex items-center justify-center">
                <svg className="w-5 h-5 text-white" viewBox="0 0 24 24" fill="none">
                  <rect x="5.0" y="12.2" width="2.8" height="7.8" rx="0.9" fill="currentColor" />
                  <rect x="10.6" y="10.9" width="2.8" height="9.1" rx="0.9" fill="currentColor" />
                  <rect x="16.2" y="8.6" width="2.8" height="11.4" rx="0.9" fill="currentColor" />
                  <path d="M4.4 8.9L8.6 6.8L12.0 7.4L15.4 5.5L18.8 4.9" stroke="currentColor" strokeWidth="1" strokeLinecap="round" strokeLinejoin="round" />
                  <path d="M17.8 4.2L19.1 4.9L17.9 5.9" stroke="currentColor" strokeWidth="1" strokeLinecap="round" strokeLinejoin="round" />
                </svg>
              </div>
              <span className="text-lg font-bold bg-gradient-to-r from-blue-600 to-indigo-600 bg-clip-text text-transparent">
                <span className="hidden lg:inline">AutopilotMonitor</span>
                <span className="hidden md:inline lg:hidden">AP Monitor</span>
                <span className="md:hidden">AP Mon</span>
              </span>
            </Link>
          </div>

          {/* Global Search — centered on desktop, lupe pushed right on mobile */}
          <GlobalSearch />

          {/* Right side — Dark Mode, Notifications, Settings, Overflow, User */}
          <div className="flex items-center space-x-1">
            {/* Dark Mode Toggle — hidden on <sm, moved to overflow */}
            <button
              onClick={toggleTheme}
              className="hidden sm:block p-2 rounded-lg hover:bg-gray-100 transition-colors"
              title={theme === 'dark' ? 'Switch to light mode' : 'Switch to dark mode'}
            >
              {theme === 'dark' ? (
                <svg className="w-5 h-5 text-yellow-500" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M12 3v1m0 16v1m9-9h-1M4 12H3m15.364 6.364l-.707-.707M6.343 6.343l-.707-.707m12.728 0l-.707.707M6.343 17.657l-.707.707M16 12a4 4 0 11-8 0 4 4 0 018 0z" />
                </svg>
              ) : (
                <svg className="w-5 h-5 text-gray-600" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M20.354 15.354A9 9 0 018.646 3.646 9.003 9.003 0 0012 21a9.003 9.003 0 008.354-5.646z" />
                </svg>
              )}
            </button>

            {/* Notification Bell — always visible (including mobile) */}
            <div className="relative" ref={notificationRef}>
              <button
                onClick={() => setShowNotifications(!showNotifications)}
                className="relative p-2 rounded-lg hover:bg-gray-100 transition-colors"
                title="Notifications"
              >
                <svg className="w-5 h-5 text-gray-600" fill="none" strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" viewBox="0 0 24 24" stroke="currentColor">
                  <path d="M15 17h5l-1.405-1.405A2.032 2.032 0 0118 14.158V11a6.002 6.002 0 00-4-5.659V5a2 2 0 10-4 0v.341C7.67 6.165 6 8.388 6 11v3.159c0 .538-.214 1.055-.595 1.436L4 17h5m6 0v1a3 3 0 11-6 0v-1m6 0H9"></path>
                </svg>
                {(() => {
                  const globalCount = (hasGlobalScope && globalAdminMode) ? globalNotifications.length : 0;
                  const totalUnread = unreadCount + globalCount + tenantNotifications.length;
                  return totalUnread > 0 ? (
                    <span className="absolute top-0.5 right-0.5 inline-flex items-center justify-center w-4 h-4 text-[10px] font-bold leading-none text-white bg-red-600 rounded-full">
                      {totalUnread > 9 ? '9+' : totalUnread}
                    </span>
                  ) : null;
                })()}
              </button>

              {/* Notification Dropdown */}
              {showNotifications && (() => {
                const showGlobal = hasGlobalScope && globalAdminMode;
                const visibleGlobal = showGlobal ? globalNotifications : [];
                const hasAny = notifications.length > 0 || visibleGlobal.length > 0 || tenantNotifications.length > 0;
                // "Clear all" should only show when the caller can actually clear something: ephemeral
                // (client-side, anyone), tenant (if canDismissTenant), or global (real GA only). A read-only
                // Global Reader viewing only global notifications gets no dead button.
                const hasClearable =
                  notifications.length > 0 ||
                  (canDismissTenant && tenantNotifications.length > 0) ||
                  (!!user?.isGlobalAdmin && visibleGlobal.length > 0);

                return (
                <div className="fixed sm:absolute top-16 sm:top-auto left-2 right-2 sm:left-auto sm:right-0 mt-0 sm:mt-2 sm:w-96 bg-white rounded-lg shadow-lg border border-gray-200 z-50 max-h-[calc(100vh-5rem)] sm:max-h-96 overflow-hidden flex flex-col">
                  <div className="px-4 py-3 border-b border-gray-200 flex justify-between items-center">
                    <h3 className="text-sm font-semibold text-gray-900">Notifications</h3>
                    {(unreadCount > 0 || hasClearable) && (
                      <div className="flex space-x-2">
                        {unreadCount > 0 && (
                          <button onClick={markAllAsRead} className="text-xs text-blue-600 hover:text-blue-800">
                            Mark all read
                          </button>
                        )}
                        {hasClearable && (
                          <button onClick={() => { clearAll(); if (showGlobal && user?.isGlobalAdmin) dismissAllGlobal(); if (canDismissTenant && tenantNotifications.length > 0) dismissAllTenant(); }} className="text-xs text-gray-500 hover:text-gray-700">
                            Clear all
                          </button>
                        )}
                      </div>
                    )}
                  </div>
                  <div className="overflow-y-auto flex-1">
                    {!hasAny ? (
                      <div className="p-6 text-center text-gray-400">
                        <svg className="w-10 h-10 mx-auto mb-2 text-gray-300" fill="none" strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" viewBox="0 0 24 24" stroke="currentColor">
                          <path d="M20 13V6a2 2 0 00-2-2H6a2 2 0 00-2 2v7m16 0v5a2 2 0 01-2 2H6a2 2 0 01-2-2v-5m16 0h-2.586a1 1 0 00-.707.293l-2.414 2.414a1 1 0 01-.707.293h-3.172a1 1 0 01-.707-.293l-2.414-2.414A1 1 0 006.586 13H4"></path>
                        </svg>
                        <p className="text-sm">No notifications</p>
                      </div>
                    ) : (
                      <div className="divide-y divide-gray-100">
                        {/* Tenant-scoped persistent notifications (e.g. hardware rejections) — top */}
                        {tenantNotifications.map((tn) => (
                          <div key={`tn-${tn.id}`} className="px-4 py-3 hover:bg-blue-50/50 transition-colors border-l-4 border-blue-500 bg-blue-50/30">
                            <div className="flex items-start justify-between">
                              <div className="flex items-start space-x-2.5 flex-1">
                                <span className="text-lg">{tn.type === 'hardware_rejection' ? '🖥️' : '🔔'}</span>
                                <div className="flex-1 min-w-0">
                                  <p className="text-sm font-medium text-gray-900">{tn.title}</p>
                                  <p className="text-xs text-gray-600 mt-0.5">{tn.message}</p>
                                  <div className="flex items-center gap-3 mt-1">
                                    <p className="text-[10px] text-gray-400">{formatTime(new Date(tn.createdAt))}</p>
                                    {tn.href && (
                                      <Link
                                        href={tn.href}
                                        onClick={(e) => { e.stopPropagation(); }}
                                        className="text-[10px] text-blue-600 hover:text-blue-800 font-medium underline"
                                      >
                                        View
                                      </Link>
                                    )}
                                  </div>
                                </div>
                              </div>
                              {canDismissTenant && (
                                <button onClick={(e) => { e.stopPropagation(); dismissTenantNotification(tn.id); }} className="ml-2 text-gray-300 hover:text-gray-500" title="Dismiss">
                                  <svg className="w-3.5 h-3.5" fill="none" strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" viewBox="0 0 24 24" stroke="currentColor">
                                    <path d="M6 18L18 6M6 6l12 12"></path>
                                  </svg>
                                </button>
                              )}
                            </div>
                          </div>
                        ))}
                        {/* Persistent Global Admin Notifications */}
                        {visibleGlobal.map((gn) => (
                          <div key={`ga-${gn.id}`} className="px-4 py-3 hover:bg-purple-50/50 transition-colors border-l-4 border-purple-500 bg-purple-50/30">
                            <div className="flex items-start justify-between">
                              <div className="flex items-start space-x-2.5 flex-1">
                                <span className="text-lg">{gn.type === 'session_report' ? '\uD83D\uDCCB' : '\uD83C\uDF1F'}</span>
                                <div className="flex-1 min-w-0">
                                  <div className="flex items-center gap-1.5">
                                    <p className="text-sm font-medium text-gray-900">{gn.title}</p>
                                    <span className="text-[9px] font-semibold text-purple-700 bg-purple-100 px-1 py-0.5 rounded">GA</span>
                                  </div>
                                  <p className="text-xs text-gray-600 mt-0.5">{gn.message}</p>
                                  <p className="text-[10px] text-gray-400 mt-1">{formatTime(new Date(gn.createdAt))}</p>
                                </div>
                              </div>
                              {user?.isGlobalAdmin && (
                                <button onClick={(e) => { e.stopPropagation(); dismissGlobal(gn.id); }} className="ml-2 text-gray-300 hover:text-gray-500" title="Dismiss">
                                  <svg className="w-3.5 h-3.5" fill="none" strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" viewBox="0 0 24 24" stroke="currentColor">
                                    <path d="M6 18L18 6M6 6l12 12"></path>
                                  </svg>
                                </button>
                              )}
                            </div>
                          </div>
                        ))}
                        {/* Ephemeral Notifications */}
                        {notifications.map((notification) => (
                          <div key={notification.id} className={`px-4 py-3 hover:bg-gray-50 transition-colors cursor-pointer ${!notification.read ? 'bg-blue-50' : ''}`} onClick={() => { if (!notification.read) { markAsRead(notification.id); } }}>
                            <div className="flex items-start justify-between">
                              <div className="flex items-start space-x-2.5 flex-1">
                                <span className="text-lg">{getNotificationIcon(notification.type)}</span>
                                <div className="flex-1 min-w-0">
                                  <p className="text-sm font-medium text-gray-900">{notification.title}</p>
                                  <p className="text-xs text-gray-600 mt-0.5">{notification.message}</p>
                                  <div className="flex items-center gap-3 mt-1">
                                    <p className="text-[10px] text-gray-400">{formatTime(notification.timestamp)}</p>
                                    {notification.href && (
                                      <Link
                                        href={notification.href}
                                        onClick={(e) => { e.stopPropagation(); markAsRead(notification.id); }}
                                        className="text-[10px] text-blue-600 hover:text-blue-800 font-medium underline"
                                      >
                                        View
                                      </Link>
                                    )}
                                  </div>
                                </div>
                              </div>
                              <button onClick={(e) => { e.stopPropagation(); removeNotification(notification.id); }} className="ml-2 text-gray-300 hover:text-gray-500">
                                <svg className="w-3.5 h-3.5" fill="none" strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" viewBox="0 0 24 24" stroke="currentColor">
                                  <path d="M6 18L18 6M6 6l12 12"></path>
                                </svg>
                              </button>
                            </div>
                          </div>
                        ))}
                      </div>
                    )}
                  </div>
                </div>
                );
              })()}
            </div>

            {/* Settings Menu (Gear) — hidden on <sm, moved to overflow */}
            <div className="hidden sm:block relative" ref={settingsRef}>
              <button onClick={() => setShowSettings(!showSettings)} className="p-2 rounded-lg hover:bg-gray-100 transition-colors" title="Settings">
                <svg className="w-5 h-5 text-gray-600" fill="none" viewBox="0 0 24 24" stroke="currentColor">
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z" />
                  <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
                </svg>
              </button>

              {showSettings && (
                <div className="absolute right-0 mt-2 w-64 bg-white rounded-lg shadow-lg border border-gray-200 z-50 max-h-[32rem] overflow-y-auto">
                  <div className="p-3">
                    {/* MODE TOGGLES */}
                    {isTenantAdmin && (
                      <>
                    <p className="text-[11px] font-semibold uppercase tracking-wider text-gray-400 mb-2">Administration</p>

                    {/* Admin Mode Toggle */}
                    <div className="flex items-center justify-between py-2 px-2.5 rounded-md bg-gray-50 mb-1">
                      <div className="flex items-center gap-1.5">
                        <svg className="w-4 h-4 text-gray-500" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                          <path strokeLinecap="round" strokeLinejoin="round" d="M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z" />
                        </svg>
                        <span className="text-sm text-gray-700">Admin Mode</span>
                        {adminMode && <span className="text-[10px] text-amber-600 font-semibold">ON</span>}
                      </div>
                      <button onClick={() => { trackEvent("admin_mode_toggled", { enabled: !adminMode }); setAdminMode(!adminMode); }} className={`relative inline-flex h-5 w-9 items-center rounded-full transition-colors ${adminMode ? 'bg-amber-500' : 'bg-gray-300'}`}>
                        <span className={`inline-block h-3.5 w-3.5 transform rounded-full bg-white transition-transform ${adminMode ? 'translate-x-[18px]' : 'translate-x-[3px]'}`} />
                      </button>
                    </div>
                      </>
                    )}

                    {/* Global scope toggle — Global Admin or read-only Global Reader */}
                    {hasGlobalScope && (
                      <div className="mb-1">
                        <div className="flex items-center justify-between py-2 px-2.5 rounded-md bg-purple-50">
                          <div className="flex items-center gap-1.5">
                            <svg className="w-4 h-4 text-purple-500" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                              <path strokeLinecap="round" strokeLinejoin="round" d="M3.055 11H5a2 2 0 012 2v1a2 2 0 002 2 2 2 0 012 2v2.945M8 3.935V5.5A2.5 2.5 0 0010.5 8h.5a2 2 0 012 2 2 2 0 104 0 2 2 0 012-2h1.064M15 20.488V18a2 2 0 012-2h3.064M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                            </svg>
                            <span className="text-sm text-gray-700">{user?.isGlobalAdmin ? 'Global Admin' : 'Global View'}</span>
                            {globalAdminMode && <span className="text-[10px] text-purple-700 font-semibold">ON</span>}
                          </div>
                          <button onClick={() => { trackEvent("admin_mode_toggled", { enabled: !globalAdminMode, isGlobal: true }); setGlobalAdminMode(!globalAdminMode); }} className={`relative inline-flex h-5 w-9 items-center rounded-full transition-colors ${globalAdminMode ? 'bg-purple-600' : 'bg-gray-300'}`}>
                            <span className={`inline-block h-3.5 w-3.5 transform rounded-full bg-white transition-transform ${globalAdminMode ? 'translate-x-[18px]' : 'translate-x-[3px]'}`} />
                          </button>
                        </div>
                      </div>
                    )}

                  </div>
                </div>
              )}
            </div>

            {/* Help Menu (?) — hidden on <sm, moved to overflow */}
            <div className="hidden sm:block relative" ref={helpRef}>
              <button onClick={() => setShowHelp(!showHelp)} className="p-2 rounded-lg hover:bg-gray-100 transition-colors" title="Help & Info">
                <svg className="w-5 h-5 text-gray-600" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M8.228 9c.549-1.165 2.03-2 3.772-2 2.21 0 4 1.343 4 3 0 1.4-1.278 2.575-3.006 2.907-.542.104-.994.54-.994 1.093m0 3h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                </svg>
              </button>

              {showHelp && (
                <div className="absolute right-0 mt-2 w-48 bg-white rounded-lg shadow-lg border border-gray-200 z-50 py-1">
                  <a
                    href="https://docs.autopilotmonitor.com"
                    target="_blank"
                    rel="noopener noreferrer"
                    className="w-full flex items-center gap-2.5 px-3 py-2 text-sm text-gray-700 hover:bg-gray-50 transition-colors"
                    onClick={() => setShowHelp(false)}
                  >
                    <svg className="w-4 h-4 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                      <path strokeLinecap="round" strokeLinejoin="round" d="M12 6.253v13m0-13C10.832 5.477 9.246 5 7.5 5S4.168 5.477 3 6.253v13C4.168 18.477 5.754 18 7.5 18s3.332.477 4.5 1.253m0-13C13.168 5.477 14.754 5 16.5 5c1.747 0 3.332.477 4.5 1.253v13C19.832 18.477 18.247 18 16.5 18c-1.746 0-3.332.477-4.5 1.253" />
                    </svg>
                    <span>Documentation</span>
                  </a>

                  <Link
                    href="/roadmap"
                    className="w-full flex items-center gap-2.5 px-3 py-2 text-sm text-gray-700 hover:bg-gray-50 transition-colors"
                    onClick={() => setShowHelp(false)}
                  >
                    <svg className="w-4 h-4 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                      <path strokeLinecap="round" strokeLinejoin="round" d="M9 20l-5.447-2.724A1 1 0 013 16.382V5.618a1 1 0 01.553-.894L9 2m0 18l6-3m-6 3V2m6 15l5.447-2.724A1 1 0 0021 13.382V2.618a1 1 0 00-.553-.894L15 2m0 15V2" />
                    </svg>
                    <span>Roadmap</span>
                  </Link>

                  <a
                    href="https://docs.autopilotmonitor.com/changelog/platform-changelog"
                    target="_blank"
                    rel="noopener noreferrer"
                    className="w-full flex items-center gap-2.5 px-3 py-2 text-sm text-gray-700 hover:bg-gray-50 transition-colors"
                    onClick={() => setShowHelp(false)}
                  >
                    <svg className="w-4 h-4 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                      <path strokeLinecap="round" strokeLinejoin="round" d="M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2" />
                    </svg>
                    <span>Changelog</span>
                  </a>

                  <a
                    href="https://docs.autopilotmonitor.com/troubleshooting/service-announcements"
                    target="_blank"
                    rel="noopener noreferrer"
                    className="w-full flex items-center gap-2.5 px-3 py-2 text-sm text-gray-700 hover:bg-gray-50 transition-colors"
                    onClick={() => setShowHelp(false)}
                  >
                    <svg className="w-4 h-4 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                      <path strokeLinecap="round" strokeLinejoin="round" d="M12 9v2m0 4h.01M10.29 3.86L1.82 18a2 2 0 001.71 3h16.94a2 2 0 001.71-3L13.71 3.86a2 2 0 00-3.42 0z" />
                    </svg>
                    <span>Known Issues</span>
                  </a>

                  <div className="border-t border-gray-100 my-1"></div>

                  <Link
                    href="/privacy"
                    className="w-full flex items-center gap-2.5 px-3 py-2 text-sm text-gray-700 hover:bg-gray-50 transition-colors"
                    onClick={() => setShowHelp(false)}
                  >
                    <svg className="w-4 h-4 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                      <path strokeLinecap="round" strokeLinejoin="round" d="M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z" />
                    </svg>
                    <span>Privacy Policy</span>
                  </Link>

                  <Link
                    href="/terms"
                    className="w-full flex items-center gap-2.5 px-3 py-2 text-sm text-gray-700 hover:bg-gray-50 transition-colors"
                    onClick={() => setShowHelp(false)}
                  >
                    <svg className="w-4 h-4 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                      <path strokeLinecap="round" strokeLinejoin="round" d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
                    </svg>
                    <span>Terms of Use</span>
                  </Link>
                </div>
              )}
            </div>

            {/* Overflow Menu (...) — visible only on <sm */}
            <div className="sm:hidden relative" ref={overflowRef}>
              <button
                onClick={() => { setShowOverflow(!showOverflow); setOverflowSubmenu(null); }}
                className="p-2 rounded-lg hover:bg-gray-100 transition-colors"
                title="More"
              >
                <svg className="w-5 h-5 text-gray-600" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M5 12h.01M12 12h.01M19 12h.01M6 12a1 1 0 11-2 0 1 1 0 012 0zm7 0a1 1 0 11-2 0 1 1 0 012 0zm7 0a1 1 0 11-2 0 1 1 0 012 0z" />
                </svg>
              </button>
              {showOverflow && (
                <div className="absolute right-0 mt-2 w-56 bg-white dark:bg-gray-800 rounded-lg shadow-lg border border-gray-200 dark:border-gray-700 z-50 py-1">

                  {/* ── Main overflow menu ── */}
                  {!overflowSubmenu && (
                    <>
                      {/* Dark Mode Toggle */}
                      <button
                        onClick={() => { toggleTheme(); setShowOverflow(false); }}
                        className="w-full flex items-center justify-between px-3 py-2 text-sm text-gray-700 dark:text-gray-200 hover:bg-gray-50 dark:hover:bg-gray-700 transition-colors"
                      >
                        <div className="flex items-center gap-2.5">
                          {theme === 'dark' ? (
                            <svg className="w-4 h-4 text-yellow-500" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                              <path strokeLinecap="round" strokeLinejoin="round" d="M12 3v1m0 16v1m9-9h-1M4 12H3m15.364 6.364l-.707-.707M6.343 6.343l-.707-.707m12.728 0l-.707.707M6.343 17.657l-.707.707M16 12a4 4 0 11-8 0 4 4 0 018 0z" />
                            </svg>
                          ) : (
                            <svg className="w-4 h-4 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                              <path strokeLinecap="round" strokeLinejoin="round" d="M20.354 15.354A9 9 0 018.646 3.646 9.003 9.003 0 0012 21a9.003 9.003 0 008.354-5.646z" />
                            </svg>
                          )}
                          <span>{theme === 'dark' ? 'Light Mode' : 'Dark Mode'}</span>
                        </div>
                      </button>

                      <div className="border-t border-gray-100 dark:border-gray-700 my-1"></div>

                      {/* Settings — opens submenu */}
                      {isTenantAdmin && (
                        <button
                          onClick={() => setOverflowSubmenu('settings')}
                          className="w-full flex items-center justify-between px-3 py-2 text-sm text-gray-700 dark:text-gray-200 hover:bg-gray-50 dark:hover:bg-gray-700 transition-colors"
                        >
                          <div className="flex items-center gap-2.5">
                            <svg className="w-4 h-4 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                              <path strokeLinecap="round" strokeLinejoin="round" d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z" />
                              <path strokeLinecap="round" strokeLinejoin="round" d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" />
                            </svg>
                            <span>Settings</span>
                          </div>
                          <svg className="w-3.5 h-3.5 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                            <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
                          </svg>
                        </button>
                      )}

                      {/* Help — opens submenu */}
                      <button
                        onClick={() => setOverflowSubmenu('help')}
                        className="w-full flex items-center justify-between px-3 py-2 text-sm text-gray-700 dark:text-gray-200 hover:bg-gray-50 dark:hover:bg-gray-700 transition-colors"
                      >
                        <div className="flex items-center gap-2.5">
                          <svg className="w-4 h-4 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                            <path strokeLinecap="round" strokeLinejoin="round" d="M8.228 9c.549-1.165 2.03-2 3.772-2 2.21 0 4 1.343 4 3 0 1.4-1.278 2.575-3.006 2.907-.542.104-.994.54-.994 1.093m0 3h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                          </svg>
                          <span>Help</span>
                        </div>
                        <svg className="w-3.5 h-3.5 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                          <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
                        </svg>
                      </button>
                    </>
                  )}

                  {/* ── Settings submenu ── */}
                  {overflowSubmenu === 'settings' && (
                    <>
                      <button
                        onClick={() => setOverflowSubmenu(null)}
                        className="w-full flex items-center gap-2 px-3 py-2 text-sm font-medium text-gray-700 dark:text-gray-200 hover:bg-gray-50 dark:hover:bg-gray-700 transition-colors"
                      >
                        <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                          <path strokeLinecap="round" strokeLinejoin="round" d="M15 19l-7-7 7-7" />
                        </svg>
                        Settings
                      </button>
                      <div className="border-t border-gray-100 dark:border-gray-700 my-1"></div>
                      <div className="px-3 py-2">
                        <p className="text-[11px] font-semibold uppercase tracking-wider text-gray-400 mb-2">Administration</p>

                        {/* Admin Mode Toggle */}
                        {isTenantAdmin && (
                          <div className="flex items-center justify-between py-2 px-2.5 rounded-md bg-gray-50 dark:bg-gray-700 mb-1">
                            <div className="flex items-center gap-1.5">
                              <svg className="w-4 h-4 text-gray-500" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                                <path strokeLinecap="round" strokeLinejoin="round" d="M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z" />
                              </svg>
                              <span className="text-sm text-gray-700 dark:text-gray-200">Admin Mode</span>
                              {adminMode && <span className="text-[10px] text-amber-600 font-semibold">ON</span>}
                            </div>
                            <button onClick={() => { trackEvent("admin_mode_toggled", { enabled: !adminMode }); setAdminMode(!adminMode); }} className={`relative inline-flex h-5 w-9 items-center rounded-full transition-colors ${adminMode ? 'bg-amber-500' : 'bg-gray-300'}`}>
                              <span className={`inline-block h-3.5 w-3.5 transform rounded-full bg-white transition-transform ${adminMode ? 'translate-x-[18px]' : 'translate-x-[3px]'}`} />
                            </button>
                          </div>
                        )}

                        {/* Global scope toggle — Global Admin or read-only Global Reader */}
                        {hasGlobalScope && (
                          <div className="flex items-center justify-between py-2 px-2.5 rounded-md bg-purple-50 dark:bg-purple-900/30 mb-1">
                            <div className="flex items-center gap-1.5">
                              <svg className="w-4 h-4 text-purple-500" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                                <path strokeLinecap="round" strokeLinejoin="round" d="M3.055 11H5a2 2 0 012 2v1a2 2 0 002 2 2 2 0 012 2v2.945M8 3.935V5.5A2.5 2.5 0 0010.5 8h.5a2 2 0 012 2 2 2 0 104 0 2 2 0 012-2h1.064M15 20.488V18a2 2 0 012-2h3.064M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                              </svg>
                              <span className="text-sm text-gray-700 dark:text-gray-200">{user?.isGlobalAdmin ? 'Global Admin' : 'Global View'}</span>
                              {globalAdminMode && <span className="text-[10px] text-purple-700 font-semibold">ON</span>}
                            </div>
                            <button onClick={() => { trackEvent("admin_mode_toggled", { enabled: !globalAdminMode, isGlobal: true }); setGlobalAdminMode(!globalAdminMode); }} className={`relative inline-flex h-5 w-9 items-center rounded-full transition-colors ${globalAdminMode ? 'bg-purple-600' : 'bg-gray-300'}`}>
                              <span className={`inline-block h-3.5 w-3.5 transform rounded-full bg-white transition-transform ${globalAdminMode ? 'translate-x-[18px]' : 'translate-x-[3px]'}`} />
                            </button>
                          </div>
                        )}
                      </div>
                    </>
                  )}

                  {/* ── Help submenu ── */}
                  {overflowSubmenu === 'help' && (
                    <>
                      <button
                        onClick={() => setOverflowSubmenu(null)}
                        className="w-full flex items-center gap-2 px-3 py-2 text-sm font-medium text-gray-700 dark:text-gray-200 hover:bg-gray-50 dark:hover:bg-gray-700 transition-colors"
                      >
                        <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                          <path strokeLinecap="round" strokeLinejoin="round" d="M15 19l-7-7 7-7" />
                        </svg>
                        Help
                      </button>
                      <div className="border-t border-gray-100 dark:border-gray-700 my-1"></div>
                      <a href="https://docs.autopilotmonitor.com" target="_blank" rel="noopener noreferrer" className="w-full flex items-center gap-2.5 px-3 py-2 text-sm text-gray-700 dark:text-gray-200 hover:bg-gray-50 dark:hover:bg-gray-700 transition-colors" onClick={() => { setShowOverflow(false); setOverflowSubmenu(null); }}>
                        <svg className="w-4 h-4 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}><path strokeLinecap="round" strokeLinejoin="round" d="M12 6.253v13m0-13C10.832 5.477 9.246 5 7.5 5S4.168 5.477 3 6.253v13C4.168 18.477 5.754 18 7.5 18s3.332.477 4.5 1.253m0-13C13.168 5.477 14.754 5 16.5 5c1.747 0 3.332.477 4.5 1.253v13C19.832 18.477 18.247 18 16.5 18c-1.746 0-3.332.477-4.5 1.253" /></svg>
                        <span>Documentation</span>
                      </a>
                      <Link href="/roadmap" className="w-full flex items-center gap-2.5 px-3 py-2 text-sm text-gray-700 dark:text-gray-200 hover:bg-gray-50 dark:hover:bg-gray-700 transition-colors" onClick={() => { setShowOverflow(false); setOverflowSubmenu(null); }}>
                        <svg className="w-4 h-4 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}><path strokeLinecap="round" strokeLinejoin="round" d="M9 20l-5.447-2.724A1 1 0 013 16.382V5.618a1 1 0 01.553-.894L9 2m0 18l6-3m-6 3V2m6 15l5.447-2.724A1 1 0 0021 13.382V2.618a1 1 0 00-.553-.894L15 2m0 15V2" /></svg>
                        <span>Roadmap</span>
                      </Link>
                      <a href="https://docs.autopilotmonitor.com/changelog/platform-changelog" target="_blank" rel="noopener noreferrer" className="w-full flex items-center gap-2.5 px-3 py-2 text-sm text-gray-700 dark:text-gray-200 hover:bg-gray-50 dark:hover:bg-gray-700 transition-colors" onClick={() => { setShowOverflow(false); setOverflowSubmenu(null); }}>
                        <svg className="w-4 h-4 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}><path strokeLinecap="round" strokeLinejoin="round" d="M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2" /></svg>
                        <span>Changelog</span>
                      </a>
                      <a href="https://docs.autopilotmonitor.com/troubleshooting/service-announcements" target="_blank" rel="noopener noreferrer" className="w-full flex items-center gap-2.5 px-3 py-2 text-sm text-gray-700 dark:text-gray-200 hover:bg-gray-50 dark:hover:bg-gray-700 transition-colors" onClick={() => { setShowOverflow(false); setOverflowSubmenu(null); }}>
                        <svg className="w-4 h-4 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}><path strokeLinecap="round" strokeLinejoin="round" d="M12 9v2m0 4h.01M10.29 3.86L1.82 18a2 2 0 001.71 3h16.94a2 2 0 001.71-3L13.71 3.86a2 2 0 00-3.42 0z" /></svg>
                        <span>Known Issues</span>
                      </a>
                      <div className="border-t border-gray-100 dark:border-gray-700 my-1"></div>
                      <Link href="/privacy" className="w-full flex items-center gap-2.5 px-3 py-2 text-sm text-gray-700 dark:text-gray-200 hover:bg-gray-50 dark:hover:bg-gray-700 transition-colors" onClick={() => { setShowOverflow(false); setOverflowSubmenu(null); }}>
                        <svg className="w-4 h-4 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}><path strokeLinecap="round" strokeLinejoin="round" d="M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z" /></svg>
                        <span>Privacy Policy</span>
                      </Link>
                      <Link href="/terms" className="w-full flex items-center gap-2.5 px-3 py-2 text-sm text-gray-700 dark:text-gray-200 hover:bg-gray-50 dark:hover:bg-gray-700 transition-colors" onClick={() => { setShowOverflow(false); setOverflowSubmenu(null); }}>
                        <svg className="w-4 h-4 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}><path strokeLinecap="round" strokeLinejoin="round" d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" /></svg>
                        <span>Terms of Use</span>
                      </Link>
                    </>
                  )}

                </div>
              )}
            </div>

            {/* User Menu */}
            <div className="relative" ref={userMenuRef}>
              <button onClick={() => setShowUserMenu(!showUserMenu)} className="flex items-center space-x-1.5 p-1.5 rounded-lg hover:bg-gray-100 transition-colors">
                <div className="w-7 h-7 rounded-full bg-blue-600 flex items-center justify-center text-white font-semibold text-xs">
                  {getUserInitials()}
                </div>
                <svg className="w-3.5 h-3.5 text-gray-500" fill="none" strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" viewBox="0 0 24 24" stroke="currentColor">
                  <path d="M19 9l-7 7-7-7"></path>
                </svg>
              </button>

              {/* User Dropdown */}
              {showUserMenu && (
                <div className="absolute right-0 mt-2 w-72 bg-white rounded-lg shadow-lg border border-gray-200 z-50">
                  <div className="px-3 py-2.5 border-b border-gray-200 flex items-start space-x-2.5">
                    <div className="w-8 h-8 rounded-full bg-blue-600 flex-shrink-0 flex items-center justify-center text-white font-semibold text-xs">
                      {getUserInitials()}
                    </div>
                    <div className="min-w-0">
                      <p className="text-sm font-medium text-gray-900">{user?.displayName || 'User'}</p>
                      <p className="text-xs text-gray-500 truncate">{user?.upn}</p>
                      {user?.isGlobalAdmin && (
                        <span className="inline-block mt-1.5 px-1.5 py-0.5 text-[10px] font-semibold text-purple-800 bg-purple-100 rounded-full">
                          Global Admin
                        </span>
                      )}
                      {user?.isGlobalReader && !user?.isGlobalAdmin && (
                        <span className="inline-block mt-1.5 px-1.5 py-0.5 text-[10px] font-semibold text-purple-800 bg-purple-50 rounded-full">
                          Global Reader
                        </span>
                      )}
                      {isOperator && (
                        <span className="inline-block mt-1.5 px-1.5 py-0.5 text-[10px] font-semibold text-blue-800 bg-blue-100 rounded-full">
                          Operator
                        </span>
                      )}
                      {isTenantAdmin && !user?.isGlobalAdmin && (
                        <span className="inline-block mt-1.5 px-1.5 py-0.5 text-[10px] font-semibold text-green-800 bg-green-100 rounded-full">
                          Admin
                        </span>
                      )}
                    </div>
                  </div>
                  <div className="py-1">
                    <button onClick={() => { logout(); setShowUserMenu(false); }} className="w-full text-left px-3 py-2 text-sm text-gray-700 hover:bg-gray-100 flex items-center space-x-2">
                      <svg className="w-4 h-4 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                        <path strokeLinecap="round" strokeLinejoin="round" d="M17 16l4-4m0 0l-4-4m4 4H7m6 4v1a3 3 0 01-3 3H6a3 3 0 01-3-3V7a3 3 0 013-3h4a3 3 0 013 3v1" />
                      </svg>
                      <span>Sign out</span>
                    </button>
                  </div>
                </div>
              )}
            </div>
          </div>
        </div>
      </div>
    </nav>
  );
}
