"use client";

import React, { createContext, useContext, useState, useCallback, useMemo } from 'react';

export type NotificationType = 'error' | 'warning' | 'info' | 'success';

export interface Notification {
  id: string;
  type: NotificationType;
  title: string;
  message: string;
  timestamp: Date;
  read: boolean;
  key?: string; // Optional unique key for deduplication
  href?: string; // Optional navigation link shown as "View" action
  reference?: string; // Optional short correlation id (backend failures) rendered as "Ref …"
}

interface NotificationContextType {
  notifications: Notification[];
  unreadCount: number;
  addNotification: (type: NotificationType, title: string, message: string, key?: string, href?: string, reference?: string) => void;
  markAsRead: (id: string) => void;
  markAllAsRead: () => void;
  removeNotification: (id: string) => void;
  clearAll: () => void;
}

const NotificationContext = createContext<NotificationContextType | undefined>(undefined);

export function NotificationProvider({ children }: { children: React.ReactNode }) {
  const [notifications, setNotifications] = useState<Notification[]>([]);

  const addNotification = useCallback((type: NotificationType, title: string, message: string, key?: string, href?: string, reference?: string) => {
    // Check if a notification with this key already exists
    if (key) {
      setNotifications(prev => {
        const existingIndex = prev.findIndex(n => n.key === key);

        if (existingIndex !== -1) {
          // Update existing notification - move to top, update timestamp and message, mark as unread
          const existing = prev[existingIndex];
          const updated = {
            ...existing,
            message,
            href,
            reference,
            timestamp: new Date(),
            read: false, // Mark as unread to get user's attention again
          };

          // Remove from current position and add to top
          const newNotifications = [...prev];
          newNotifications.splice(existingIndex, 1);
          return [updated, ...newNotifications];
        } else {
          // Create new notification with key
          const notification: Notification = {
            id: `${Date.now()}-${Math.random()}`,
            type,
            title,
            message,
            timestamp: new Date(),
            read: false,
            key,
            href,
            reference,
          };

          // Auto-remove success/info notifications after 10 seconds
          if (type === 'success' || type === 'info') {
            setTimeout(() => {
              setNotifications(prev => prev.filter(n => n.id !== notification.id));
            }, 10000);
          }

          return [notification, ...prev];
        }
      });
    } else {
      // No key provided - create new notification (original behavior)
      const notification: Notification = {
        id: `${Date.now()}-${Math.random()}`,
        type,
        title,
        message,
        timestamp: new Date(),
        read: false,
        href,
        reference,
      };

      setNotifications(prev => [notification, ...prev]);

      // Auto-remove success/info notifications after 10 seconds
      if (type === 'success' || type === 'info') {
        setTimeout(() => {
          setNotifications(prev => prev.filter(n => n.id !== notification.id));
        }, 10000);
      }
    }
  }, []);

  const markAsRead = useCallback((id: string) => {
    setNotifications(prev =>
      prev.map(n => n.id === id ? { ...n, read: true } : n)
    );
  }, []);

  const markAllAsRead = useCallback(() => {
    setNotifications(prev =>
      prev.map(n => ({ ...n, read: true }))
    );
  }, []);

  const removeNotification = useCallback((id: string) => {
    setNotifications(prev => prev.filter(n => n.id !== id));
  }, []);

  const clearAll = useCallback(() => {
    setNotifications([]);
  }, []);

  const unreadCount = notifications.filter(n => !n.read).length;

  // Memoized (P6.3): every addNotification anywhere in the app re-renders this provider;
  // without the memo that minted a fresh value object and re-rendered every consumer even
  // when nothing they read changed.
  const value: NotificationContextType = useMemo(() => ({
    notifications,
    unreadCount,
    addNotification,
    markAsRead,
    markAllAsRead,
    removeNotification,
    clearAll,
  }), [notifications, unreadCount, addNotification, markAsRead, markAllAsRead, removeNotification, clearAll]);

  return (
    <NotificationContext.Provider value={value}>
      {children}
    </NotificationContext.Provider>
  );
}

export function useNotifications() {
  const context = useContext(NotificationContext);
  if (context === undefined) {
    throw new Error('useNotifications must be used within a NotificationProvider');
  }
  return context;
}
