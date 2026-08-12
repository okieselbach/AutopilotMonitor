"use client";

import React, { createContext, useContext, useState, useCallback, useEffect, useRef } from 'react';
import { useAuth } from './AuthContext';
import { useSignalR } from './SignalRContext';
import { canFetchTenantNotifications } from './tenantNotificationsGate';
import { api } from '@/lib/api';
import { authenticatedFetch } from '@/lib/authenticatedFetch';

export interface TenantNotification {
  id: string;
  type: string;
  title: string;
  message: string;
  href?: string;
  createdAt: string;
}

interface TenantNotificationContextType {
  tenantNotifications: TenantNotification[];
  tenantUnreadCount: number;
  dismissTenantNotification: (id: string) => Promise<void>;
  dismissAllTenant: () => Promise<void>;
  isLoading: boolean;
}

const TenantNotificationContext = createContext<TenantNotificationContextType | undefined>(undefined);

// Stable identity for the rights-masked empty list.
const EMPTY_TENANT_NOTIFICATIONS: TenantNotification[] = [];

export function TenantNotificationProvider({ children }: { children: React.ReactNode }) {
  const { user, getAccessToken } = useAuth();
  const { connection, isConnected, joinGroup, leaveGroup } = useSignalR();
  const [tenantNotifications, setTenantNotifications] = useState<TenantNotification[]>([]);
  const [isLoading, setIsLoading] = useState(false);
  const fetchingRef = useRef(false);

  // Only tenant members (Admin/Operator/Viewer) and Global Admins are entitled to the bell.
  // The backend filters per-notification visibility by audience tier, so it is safe to fetch
  // for every member role — but unauthenticated users and users without a tenant role would
  // just produce 401/403 noise, so we skip them entirely.
  const canFetchNotifications = canFetchTenantNotifications(user);
  const tenantId = user?.tenantId ?? null;
  const isAdminTier = user?.isTenantAdmin === true || user?.isGlobalAdmin === true;

  const fetchNotifications = useCallback(async () => {
    if (!canFetchNotifications || fetchingRef.current) return;
    fetchingRef.current = true;

    try {
      const response = await authenticatedFetch(
        api.notifications.tenantList(),
        getAccessToken,
      );
      if (response.ok) {
        const data = await response.json();
        setTenantNotifications(data.notifications ?? []);
      }
    } catch {
      // Silently ignore — notifications are best-effort
    } finally {
      fetchingRef.current = false;
    }
  }, [canFetchNotifications, getAccessToken]);

  // Initial state hydration. Runs once on mount and again after every SignalR reconnect to
  // recover any deltas that were emitted while the SignalR connection was down. Without
  // fetch rights nothing is fetched — the provider value below masks the list to empty
  // instead of clearing state here.
  useEffect(() => {
    if (!canFetchNotifications) return;
    const run = async () => {
      setIsLoading(true);
      await fetchNotifications().finally(() => setIsLoading(false));
    };
    void run();
  }, [canFetchNotifications, fetchNotifications]);

  // Re-fetch on SignalR reconnect — covers the deltas pushed during the disconnect window.
  useEffect(() => {
    if (!connection || !canFetchNotifications) return;
    const handler = () => { fetchNotifications(); };
    connection.onreconnected(handler);
    // The signalr-js client does not expose an unsubscribe for onreconnected; rely on the
    // SignalR connection lifecycle (one connection per app session) for cleanup.
  }, [connection, canFetchNotifications, fetchNotifications]);

  // Group membership: Member-tier always; Admin-tier only for Tenant Admins / Global Admins.
  useEffect(() => {
    if (!isConnected || !canFetchNotifications || !tenantId) return;

    const memberGroup = `tenant-${tenantId}-notify-member`;
    const adminGroup = `tenant-${tenantId}-notify-admin`;

    joinGroup(memberGroup);
    if (isAdminTier) {
      joinGroup(adminGroup);
    }

    return () => {
      leaveGroup(memberGroup);
      if (isAdminTier) {
        leaveGroup(adminGroup);
      }
    };
  }, [isConnected, canFetchNotifications, tenantId, isAdminTier, joinGroup, leaveGroup]);

  // Live-push handlers.
  useEffect(() => {
    if (!connection) return;

    const onCreate = (notification: TenantNotification) => {
      if (!notification?.id) return;
      setTenantNotifications(prev => prev.some(n => n.id === notification.id) ? prev : [notification, ...prev]);
    };
    const onDismiss = (payload: { id: string }) => {
      if (!payload?.id) return;
      setTenantNotifications(prev => prev.filter(n => n.id !== payload.id));
    };
    const onDismissAll = () => {
      setTenantNotifications([]);
    };

    connection.on('tenantNotification', onCreate);
    connection.on('tenantNotificationDismissed', onDismiss);
    connection.on('tenantNotificationsDismissedAll', onDismissAll);

    return () => {
      connection.off('tenantNotification', onCreate);
      connection.off('tenantNotificationDismissed', onDismiss);
      connection.off('tenantNotificationsDismissedAll', onDismissAll);
    };
  }, [connection]);

  const dismissTenantNotification = useCallback(async (id: string) => {
    setTenantNotifications(prev => prev.filter(n => n.id !== id));

    try {
      await authenticatedFetch(
        api.notifications.tenantDismiss(id),
        getAccessToken,
        { method: 'POST' },
      );
    } catch {
      // Best-effort; the backend push will reconcile other tabs if the dismiss eventually lands
    }
  }, [getAccessToken]);

  const dismissAllTenant = useCallback(async () => {
    setTenantNotifications([]);

    try {
      await authenticatedFetch(
        api.notifications.tenantDismissAll(),
        getAccessToken,
        { method: 'POST' },
      );
    } catch {
      // Best-effort
    }
  }, [getAccessToken]);

  // Without fetch rights consumers see an empty list — derived here rather than
  // clearing state, so a rights re-grant simply re-exposes the refetched data.
  const visibleTenantNotifications = canFetchNotifications
    ? tenantNotifications
    : EMPTY_TENANT_NOTIFICATIONS;
  const tenantUnreadCount = visibleTenantNotifications.length;

  return (
    <TenantNotificationContext.Provider value={{
      tenantNotifications: visibleTenantNotifications,
      tenantUnreadCount,
      dismissTenantNotification,
      dismissAllTenant,
      isLoading,
    }}>
      {children}
    </TenantNotificationContext.Provider>
  );
}

export function useTenantNotifications() {
  const context = useContext(TenantNotificationContext);
  if (context === undefined) {
    throw new Error('useTenantNotifications must be used within a TenantNotificationProvider');
  }
  return context;
}
