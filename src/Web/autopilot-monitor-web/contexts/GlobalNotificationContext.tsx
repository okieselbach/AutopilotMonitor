"use client";

import React, { createContext, useContext, useState, useCallback, useEffect, useRef } from 'react';
import { useAuth } from './AuthContext';
import { useSignalR } from './SignalRContext';
import { api } from '@/lib/api';
import { authenticatedFetch } from '@/lib/authenticatedFetch';
import type { GlobalNotificationDto, NotificationListResponse } from '@/utils/wire-types.generated';

// Wire type is generated from the backend DTO; the alias keeps the established local name.
export type GlobalNotification = GlobalNotificationDto;

interface GlobalNotificationContextType {
  notifications: GlobalNotification[];
  unreadCount: number;
  dismissNotification: (id: string) => Promise<void>;
  dismissAll: () => Promise<void>;
  isLoading: boolean;
}

const GlobalNotificationContext = createContext<GlobalNotificationContextType | undefined>(undefined);

// Stable identity for the scope-masked empty list.
const EMPTY_NOTIFICATIONS: GlobalNotification[] = [];

export function GlobalNotificationProvider({ children }: { children: React.ReactNode }) {
  const { user, hasGlobalScope, getAccessToken } = useAuth();
  const { connection, isConnected, joinGroup, leaveGroup } = useSignalR();
  const [notifications, setNotifications] = useState<GlobalNotification[]>([]);
  const [isLoading, setIsLoading] = useState(false);
  const fetchingRef = useRef(false);

  // View scope (hasGlobalScope = GA OR read-only Global Reader) gates the read surfaces: FETCH/hydrate
  // (backend global/notifications GET is GlobalReadOrAdmin) AND the SignalR global-admins live-group join
  // (backend now admits HasGlobalScope — read broadcast, reader only receives). canManageGlobal (real GA)
  // gates only dismiss/clear, which the backend keeps GlobalAdminOnly.
  const canManageGlobal = user?.isGlobalAdmin === true;

  const fetchNotifications = useCallback(async () => {
    if (!hasGlobalScope || fetchingRef.current) return;
    fetchingRef.current = true;

    try {
      const response = await authenticatedFetch(
        api.notifications.list(),
        getAccessToken,
      );
      if (response.ok) {
        const data = (await response.json()) as NotificationListResponse;
        setNotifications(data.notifications ?? []);
      }
    } catch {
      // Silently ignore — notifications are best-effort
    } finally {
      fetchingRef.current = false;
    }
  }, [hasGlobalScope, getAccessToken]);

  // Initial state hydration. Re-fetched on SignalR reconnect to recover any deltas
  // pushed during the disconnect window. Without scope nothing is fetched — the
  // provider value below masks the list to empty instead of clearing state here.
  useEffect(() => {
    if (!hasGlobalScope) return;
    const run = async () => {
      setIsLoading(true);
      await fetchNotifications().finally(() => setIsLoading(false));
    };
    void run();
  }, [hasGlobalScope, fetchNotifications]);

  useEffect(() => {
    if (!connection || !hasGlobalScope) return;
    const handler = () => { fetchNotifications(); };
    connection.onreconnected(handler);
  }, [connection, hasGlobalScope, fetchNotifications]);

  // Re-fetch when globalAdminMode is toggled in localStorage
  useEffect(() => {
    const handleStorageChange = () => {
      if (hasGlobalScope) {
        fetchNotifications();
      }
    };
    window.addEventListener('localStorageChange', handleStorageChange);
    return () => window.removeEventListener('localStorageChange', handleStorageChange);
  }, [hasGlobalScope, fetchNotifications]);

  // Group membership: any platform scope (GA or read-only Global Reader) joins the global-admins live
  // group — it is a READ broadcast group (backend now admits HasGlobalScope), so a reader receives live
  // notification pushes too. Dismiss/clear remain GA-only (guarded above).
  useEffect(() => {
    if (!isConnected || !hasGlobalScope) return;
    const group = 'global-admins';
    joinGroup(group);
    return () => { leaveGroup(group); };
  }, [isConnected, hasGlobalScope, joinGroup, leaveGroup]);

  // Live-push handlers.
  useEffect(() => {
    if (!connection) return;

    const onCreate = (notification: GlobalNotification) => {
      if (!notification?.id) return;
      setNotifications(prev => prev.some(n => n.id === notification.id) ? prev : [notification, ...prev]);
    };
    const onDismiss = (payload: { id: string }) => {
      if (!payload?.id) return;
      setNotifications(prev => prev.filter(n => n.id !== payload.id));
    };
    const onDismissAll = () => {
      setNotifications([]);
    };

    connection.on('globalNotification', onCreate);
    connection.on('globalNotificationDismissed', onDismiss);
    connection.on('globalNotificationsDismissedAll', onDismissAll);

    return () => {
      connection.off('globalNotification', onCreate);
      connection.off('globalNotificationDismissed', onDismiss);
      connection.off('globalNotificationsDismissedAll', onDismissAll);
    };
  }, [connection]);

  const dismissNotification = useCallback(async (id: string) => {
    // Dismiss is GA-only (backend GlobalAdminOnly). A read-only Global Reader must not even optimistically
    // clear their view. UI dismiss buttons are also hidden for readers.
    if (!canManageGlobal) return;
    setNotifications(prev => prev.filter(n => n.id !== id));

    try {
      await authenticatedFetch(
        api.notifications.dismiss(id),
        getAccessToken,
        { method: 'POST' },
      );
    } catch {
      // Best-effort; backend push will reconcile other tabs if dismiss eventually lands
    }
  }, [canManageGlobal, getAccessToken]);

  const dismissAll = useCallback(async () => {
    if (!canManageGlobal) return; // GA-only (see dismissNotification)
    setNotifications([]);

    try {
      await authenticatedFetch(
        api.notifications.dismissAll(),
        getAccessToken,
        { method: 'POST' },
      );
    } catch {
      // Best-effort
    }
  }, [canManageGlobal, getAccessToken]);

  // Without platform scope consumers see an empty list — derived here rather than
  // clearing state, so a scope re-grant simply re-exposes the refetched data.
  const visibleNotifications = hasGlobalScope ? notifications : EMPTY_NOTIFICATIONS;
  const unreadCount = visibleNotifications.length;

  return (
    <GlobalNotificationContext.Provider
      value={{ notifications: visibleNotifications, unreadCount, dismissNotification, dismissAll, isLoading }}
    >
      {children}
    </GlobalNotificationContext.Provider>
  );
}

export function useGlobalNotifications() {
  const context = useContext(GlobalNotificationContext);
  if (context === undefined) {
    throw new Error('useGlobalNotifications must be used within a GlobalNotificationProvider');
  }
  return context;
}
