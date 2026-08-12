"use client";

import { useNotifications } from '@/contexts/NotificationContext';
import { useGlobalNotifications } from '@/contexts/GlobalNotificationContext';
import { useTenantNotifications } from '@/contexts/TenantNotificationContext';
import {
  bellBadgeLabel,
  countBellNotifications,
  ephemeralNotificationIcon,
  globalNotificationIcon,
  hasClearableNotifications,
  tenantNotificationIcon,
  type NavbarCapabilities,
} from '@/lib/navbarPolicy';
import { MenuIcon } from './icons';
import { NotificationRow } from './NotificationRow';
import type { NavbarMenus } from './useNavbarMenus';

interface NotificationBellProps {
  menus: NavbarMenus;
  capabilities: NavbarCapabilities;
  /** GA lane is visible/counted only with platform scope AND global mode on. */
  includeGlobal: boolean;
  /** Close the dropdown, then navigate (so it doesn't linger over the target page). */
  onOpenHref: (href: string) => void;
}

/** Bell button + dropdown with the three notification lanes (tenant, GA, ephemeral). */
export function NotificationBell({
  menus,
  capabilities,
  includeGlobal,
  onOpenHref,
}: NotificationBellProps) {
  const { notifications, unreadCount, markAsRead, markAllAsRead, removeNotification, clearAll } =
    useNotifications();
  const {
    notifications: globalNotifications,
    dismissNotification: dismissGlobal,
    dismissAll: dismissAllGlobal,
  } = useGlobalNotifications();
  const { tenantNotifications, dismissTenantNotification, dismissAllTenant } =
    useTenantNotifications();

  const visibleGlobal = includeGlobal ? globalNotifications : [];
  const badge = bellBadgeLabel(
    countBellNotifications({
      ephemeralUnread: unreadCount,
      tenantCount: tenantNotifications.length,
      globalCount: globalNotifications.length,
      includeGlobal,
    }),
  );
  const hasAny =
    notifications.length > 0 || visibleGlobal.length > 0 || tenantNotifications.length > 0;
  const hasClearable = hasClearableNotifications({
    ephemeralCount: notifications.length,
    tenantCount: tenantNotifications.length,
    visibleGlobalCount: visibleGlobal.length,
    canDismissTenant: capabilities.canDismissTenant,
    canDismissGlobal: capabilities.canDismissGlobal,
  });
  const now = new Date();

  const handleClearAll = () => {
    clearAll();
    if (includeGlobal && capabilities.canDismissGlobal) dismissAllGlobal();
    if (capabilities.canDismissTenant && tenantNotifications.length > 0) dismissAllTenant();
  };

  return (
    <div className="relative" ref={menus.containerRef('notifications')}>
      <button
        onClick={() => menus.toggle('notifications')}
        className="relative p-2 rounded-lg hover:bg-gray-100 transition-colors"
        title="Notifications"
      >
        <MenuIcon name="bell" className="w-5 h-5 text-gray-600" />
        {badge && (
          <span className="absolute top-0.5 right-0.5 inline-flex items-center justify-center w-4 h-4 text-[10px] font-bold leading-none text-white bg-red-600 rounded-full">
            {badge}
          </span>
        )}
      </button>

      {menus.isOpen('notifications') && (
        <div className="fixed sm:absolute top-16 sm:top-auto left-2 right-2 sm:left-auto sm:right-0 mt-0 sm:mt-2 sm:w-96 bg-white rounded-lg shadow-lg border border-gray-200 z-50 max-h-[calc(100vh-5rem)] sm:max-h-96 overflow-hidden flex flex-col">
          <div className="px-4 py-3 border-b border-gray-200 flex justify-between items-center">
            <h3 className="text-sm font-semibold text-gray-900">Notifications</h3>
            {(unreadCount > 0 || hasClearable) && (
              <div className="flex space-x-2">
                {unreadCount > 0 && (
                  <button
                    onClick={markAllAsRead}
                    className="text-xs text-green-700 hover:text-green-800"
                  >
                    Mark all read
                  </button>
                )}
                {hasClearable && (
                  <button
                    onClick={handleClearAll}
                    className="text-xs text-gray-500 hover:text-gray-700"
                  >
                    Clear all
                  </button>
                )}
              </div>
            )}
          </div>
          <div className="overflow-y-auto flex-1">
            {!hasAny ? (
              <div className="p-6 text-center text-gray-400">
                <MenuIcon name="emptyInbox" className="w-10 h-10 mx-auto mb-2 text-gray-300" />
                <p className="text-sm">No notifications</p>
              </div>
            ) : (
              <div className="divide-y divide-gray-100">
                {/* Tenant-scoped persistent notifications (e.g. hardware rejections) — top */}
                {tenantNotifications.map((tn) => (
                  <NotificationRow
                    key={`tn-${tn.id}`}
                    lane="tenant"
                    icon={tenantNotificationIcon(tn.type)}
                    title={tn.title}
                    message={tn.message}
                    createdAt={new Date(tn.createdAt)}
                    now={now}
                    href={tn.href}
                    canDismiss={capabilities.canDismissTenant}
                    onDismiss={() => dismissTenantNotification(tn.id)}
                    onOpen={() => {
                      if (tn.href) onOpenHref(tn.href);
                    }}
                    onViewLinkClick={menus.close}
                  />
                ))}
                {/* Persistent Global Admin notifications */}
                {visibleGlobal.map((gn) => (
                  <NotificationRow
                    key={`ga-${gn.id}`}
                    lane="global"
                    icon={globalNotificationIcon(gn.type)}
                    title={gn.title}
                    message={gn.message}
                    createdAt={new Date(gn.createdAt)}
                    now={now}
                    href={gn.href}
                    showGaBadge
                    canDismiss={capabilities.canDismissGlobal}
                    onDismiss={() => dismissGlobal(gn.id)}
                    onOpen={() => {
                      if (gn.href) onOpenHref(gn.href);
                    }}
                    onViewLinkClick={menus.close}
                  />
                ))}
                {/* Ephemeral notifications */}
                {notifications.map((notification) => (
                  <NotificationRow
                    key={notification.id}
                    lane="ephemeral"
                    icon={ephemeralNotificationIcon(notification.type)}
                    title={notification.title}
                    message={notification.message}
                    createdAt={new Date(notification.timestamp)}
                    now={now}
                    href={notification.href}
                    unread={!notification.read}
                    canDismiss
                    onDismiss={() => removeNotification(notification.id)}
                    onOpen={() => {
                      if (!notification.read) markAsRead(notification.id);
                      if (notification.href) onOpenHref(notification.href);
                    }}
                    onViewLinkClick={() => {
                      markAsRead(notification.id);
                      menus.close();
                    }}
                  />
                ))}
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  );
}
