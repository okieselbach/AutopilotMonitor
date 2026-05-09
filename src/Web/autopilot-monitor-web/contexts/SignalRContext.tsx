"use client";

import React, { createContext, useContext, useEffect, useRef, useState, useCallback } from 'react';
import * as signalR from '@microsoft/signalr';
import { api } from '@/lib/api';
import { authenticatedFetch } from '@/lib/authenticatedFetch';
import { trackEvent } from '@/lib/appInsights';
import { useAuth } from './AuthContext';

interface SignalRContextType {
  connection: signalR.HubConnection | null;
  connectionState: signalR.HubConnectionState;
  connectionId: string | null;
  on: (eventName: string, callback: (...args: any[]) => void) => void;
  off: (eventName: string, callback: (...args: any[]) => void) => void;
  invoke: (methodName: string, ...args: any[]) => Promise<any>;
  joinGroup: (groupName: string) => Promise<void>;
  leaveGroup: (groupName: string) => Promise<void>;
  isConnected: boolean;
  joinedGroups: string[];
}

const SignalRContext = createContext<SignalRContextType | undefined>(undefined);

export function SignalRProvider({ children }: { children: React.ReactNode }) {
  const { getAccessToken, isAuthenticated } = useAuth();
  const [connection, setConnection] = useState<signalR.HubConnection | null>(null);
  const [connectionState, setConnectionState] = useState<signalR.HubConnectionState>(signalR.HubConnectionState.Disconnected);
  const connectionRef = useRef<signalR.HubConnection | null>(null);
  const joinedGroupsRef = useRef<Set<string>>(new Set());
  const [joinedGroups, setJoinedGroups] = useState<string[]>([]);
  const retryCountRef = useRef(0);
  const retryTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  // Set in onreconnecting, read in onreconnected so we can report downtime as a measurement.
  const disconnectStartedAtRef = useRef<number | null>(null);
  const maxRetries = 3;

  const syncJoinedGroups = useCallback(() => {
    setJoinedGroups(Array.from(joinedGroupsRef.current));
  }, []);

  useEffect(() => {
    // Only create connection if authenticated
    if (!isAuthenticated) {
      return;
    }

    // Only create connection once
    if (connectionRef.current) {
      return;
    }

    const hubUrl = api.realtime.hub();
    const newConnection = new signalR.HubConnectionBuilder()
      .withUrl(hubUrl, {
        accessTokenFactory: async () => {
          const token = await getAccessToken();
          return token || '';
        }
      })
      .withAutomaticReconnect({
        nextRetryDelayInMilliseconds: (retryContext) => {
          // Exponential backoff: 0s, 2s, 10s, 30s, then 30s thereafter
          if (retryContext.elapsedMilliseconds < 60000) {
            return Math.min(1000 * Math.pow(2, retryContext.previousRetryCount), 30000);
          }
          return 30000;
        }
      })
      .configureLogging(signalR.LogLevel.Information)
      .build();

    connectionRef.current = newConnection;

    // Setup connection state change handlers
    newConnection.onclose((error) => {
      setConnectionState(signalR.HubConnectionState.Disconnected);
      trackEvent("signalr_disconnected", { hasError: !!error });
      joinedGroupsRef.current.clear(); // Clear joined groups on disconnect
      syncJoinedGroups();
      disconnectStartedAtRef.current = null;
    });

    newConnection.onreconnecting((error) => {
      setConnectionState(signalR.HubConnectionState.Reconnecting);
      disconnectStartedAtRef.current = performance.now();
    });

    newConnection.onreconnected(async (connectionId) => {
      setConnectionState(signalR.HubConnectionState.Connected);
      retryCountRef.current = 0; // Reset retry count on successful reconnect
      const downtimeMs = disconnectStartedAtRef.current !== null
        ? Math.round(performance.now() - disconnectStartedAtRef.current)
        : 0;
      disconnectStartedAtRef.current = null;

      // Auto-rejoin all previously joined groups after reconnect.
      // The server-side connection ID changed, so all group memberships are lost.
      const previousGroups = Array.from(joinedGroupsRef.current);
      joinedGroupsRef.current.clear();
      syncJoinedGroups();

      for (const groupName of previousGroups) {
        try {
          const response = await authenticatedFetch(
            api.realtime.joinGroup(),
            getAccessToken,
            {
              method: 'POST',
              headers: { 'Content-Type': 'application/json' },
              body: JSON.stringify({ connectionId, groupName })
            }
          );

          if (response.ok) {
            joinedGroupsRef.current.add(groupName);
            syncJoinedGroups();
          } else {
            console.warn(`[SignalR] Failed to rejoin group ${groupName} after reconnect (status ${response.status})`);
          }
        } catch (error) {
          console.warn(`[SignalR] Error rejoining group ${groupName} after reconnect:`, error);
        }
      }

      if (previousGroups.length > 0) {
        console.log(`[SignalR] Rejoined ${joinedGroupsRef.current.size}/${previousGroups.length} groups after reconnect`);
      }
      trackEvent("signalr_reconnected", { rejoinedGroups: joinedGroupsRef.current.size, downtimeMs });
    });

    // Start connection
    const startConnection = async () => {
      try {
        await newConnection.start();
        setConnectionState(signalR.HubConnectionState.Connected);
        setConnection(newConnection);
        retryCountRef.current = 0;
      } catch (error) {
        console.error('[SignalR] Failed to start connection:', error);
        setConnectionState(signalR.HubConnectionState.Disconnected);

        // Limited retry with exponential backoff
        if (retryCountRef.current < maxRetries) {
          retryCountRef.current++;
          const delay = Math.min(1000 * Math.pow(2, retryCountRef.current), 30000);
          retryTimeoutRef.current = setTimeout(startConnection, delay);
        } else {
          console.error('[SignalR] Max retries reached. Connection failed.');
          trackEvent("signalr_connection_failed");
        }
      }
    };

    // Start as soon as the user is authenticated. The previous 2s setTimeout
    // was meant to avoid competing with the initial dashboard fetches, but
    // the web origin is HTTP/2 and the API is on a different origin, so
    // there is no real connection contention — the delay only pushed live
    // updates ~2s into the dashboard load for no benefit.
    startConnection();

    // Cleanup only when provider unmounts (app closes)
    return () => {
      // Clear any pending retry timeout to prevent reconnection after unmount
      if (retryTimeoutRef.current) {
        clearTimeout(retryTimeoutRef.current);
        retryTimeoutRef.current = null;
      }
      if (connectionRef.current) {
        connectionRef.current.stop();
        connectionRef.current = null;
      }
    };
  }, [isAuthenticated, getAccessToken]);

  const on = (eventName: string, callback: (...args: any[]) => void) => {
    if (connectionRef.current) {
      connectionRef.current.on(eventName, callback);
    }
  };

  const off = (eventName: string, callback: (...args: any[]) => void) => {
    if (connectionRef.current) {
      connectionRef.current.off(eventName, callback);
    }
  };

  const invoke = async (methodName: string, ...args: any[]) => {
    if (connection && connectionState === signalR.HubConnectionState.Connected) {
      return await connection.invoke(methodName, ...args);
    }
    throw new Error('SignalR connection not established');
  };

  const joinGroup = useCallback(async (groupName: string) => {
    if (!connection || connectionState !== signalR.HubConnectionState.Connected) {
      return;
    }

    // Check if already in group to prevent duplicate joins
    if (joinedGroupsRef.current.has(groupName)) {
      return;
    }

    // Add to Set immediately to prevent race conditions with multiple simultaneous calls
    joinedGroupsRef.current.add(groupName);
    syncJoinedGroups();

    try {
      const connectionId = connection.connectionId;
      if (!connectionId) {
        joinedGroupsRef.current.delete(groupName); // Remove if we can't get connection ID
        syncJoinedGroups();
        return;
      }

      const response = await authenticatedFetch(
        api.realtime.joinGroup(),
        getAccessToken,
        {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ connectionId, groupName })
        }
      );

      if (!response.ok) {
        // Remove from Set if API call failed (so we can retry)
        joinedGroupsRef.current.delete(groupName);
        syncJoinedGroups();
      }
    } catch (error) {
      console.error(`[SignalR] Error joining group ${groupName}:`, error);
      // Remove from Set if API call failed (so we can retry)
      joinedGroupsRef.current.delete(groupName);
      syncJoinedGroups();
    }
  }, [connection, connectionState, getAccessToken, syncJoinedGroups]);

  const leaveGroup = useCallback(async (groupName: string) => {
    if (!connection || connectionState !== signalR.HubConnectionState.Connected) {
      return;
    }

    // Check if we're actually in the group before leaving
    if (!joinedGroupsRef.current.has(groupName)) {
      return;
    }

    // Remove from Set immediately to prevent race conditions with multiple simultaneous calls
    joinedGroupsRef.current.delete(groupName);
    syncJoinedGroups();

    try {
      const connectionId = connection.connectionId;
      if (!connectionId) {
        return;
      }

      const response = await authenticatedFetch(
        api.realtime.leaveGroup(),
        getAccessToken,
        {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ connectionId, groupName })
        }
      );

      if (!response.ok) {
        // Re-add to Set if API call failed (so we can retry)
        joinedGroupsRef.current.add(groupName);
        syncJoinedGroups();
      }
    } catch (error) {
      console.error(`[SignalR] Error leaving group ${groupName}:`, error);
      // Re-add to Set if API call failed (so we can retry)
      joinedGroupsRef.current.add(groupName);
      syncJoinedGroups();
    }
  }, [connection, connectionState, getAccessToken, syncJoinedGroups]);

  return (
    <SignalRContext.Provider
      value={{
        connection,
        connectionState,
        connectionId: connection?.connectionId ?? null,
        on,
        off,
        invoke,
        joinGroup,
        leaveGroup,
        isConnected: connectionState === signalR.HubConnectionState.Connected,
        joinedGroups,
      }}
    >
      {children}
    </SignalRContext.Provider>
  );
}

export function useSignalR() {
  const context = useContext(SignalRContext);
  if (context === undefined) {
    throw new Error('useSignalR must be used within a SignalRProvider');
  }
  return context;
}
