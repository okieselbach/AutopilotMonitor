"use client";

import React, { createContext, useContext, useEffect, useMemo, useRef, useState, useCallback } from 'react';
import { HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr';
import { api } from '@/lib/api';
import { trackEvent } from '@/lib/appInsights';
import type { SignalRMessageName } from '@/lib/signalrMessages';
import { createGroupRegistry } from '@/lib/signalrGroupRegistry';
import { useLatest } from '@/hooks/useLatest';
import { useAuth } from './AuthContext';
import { ApiError, fetchOk, jsonBody, type GetAccessToken } from "@/lib/apiClient";
import type { SignalRJoinGroupRequest, SignalRLeaveGroupRequest } from "@/utils/wire-types.generated";

// Hub payloads are untyped JSON; mirror @microsoft/signalr's own callback signature so
// consumer handlers keep their narrower parameter types without laundering here.
type SignalRHandler = Parameters<HubConnection["on"]>[1];

// A group is left this long after its last consumer released it (see lib/signalrGroupRegistry):
// long enough to absorb a dashboard → session → dashboard hop, short enough that an abandoned
// membership does not keep receiving broadcasts for long.
const GROUP_LEAVE_GRACE_MS = 2_000;

// Session-group joins by roleless Progress-Portal users must present the device's serial number
// (server-side knowledge proof). The serial is remembered per group so the automatic re-join
// after a reconnect carries it too — without that, live updates would silently die on the first
// network blip. onDenied surfaces a join the server refused (403); the context otherwise only
// logs it, and a silent join failure historically looked like a frozen page.
export interface JoinGroupOptions {
  serialNumber?: string;
  onDenied?: (status: number) => void;
}

interface SignalRContextType {
  connection: HubConnection | null;
  connectionState: HubConnectionState;
  connectionId: string | null;
  // Event names are pinned to the backend's message-name catalog (shared manifest) so a
  // subscription to a name the backend never sends is a compile error, not a silent no-op.
  on: (eventName: SignalRMessageName, callback: SignalRHandler) => void;
  off: (eventName: SignalRMessageName, callback: SignalRHandler) => void;
  invoke: (methodName: string, ...args: unknown[]) => Promise<unknown>;
  // Group membership is reference-counted per group name: several consumers of one group produce
  // one hub join, and the group is left only after the last of them left (plus a grace period).
  // Every joinGroup must be paired with exactly one leaveGroup — an effect's join with its cleanup.
  joinGroup: (groupName: string, options?: JoinGroupOptions) => Promise<void>;
  leaveGroup: (groupName: string) => Promise<void>;
  isConnected: boolean;
  /** Groups the hub connection is a member of (joined, or a join in flight) — not the reference counts. */
  joinedGroups: string[];
}

const SignalRContext = createContext<SignalRContextType | undefined>(undefined);

// The negotiated transport is not exposed by the public API. Sniff the internal transport
// object's fields instead of constructor.name, which production minification mangles;
// property names survive because bundlers do not mangle them. Verified against
// @microsoft/signalr 10.x internals.
function getTransportName(conn: HubConnection): string {
  const transport = (conn as unknown as {
    connection?: { transport?: Record<string, unknown> };
  }).connection?.transport;
  if (!transport) return 'Unknown';
  if ('_webSocketConstructor' in transport || '_webSocket' in transport) return 'WebSockets';
  if ('_eventSource' in transport) return 'ServerSentEvents';
  if ('_pollAbort' in transport) return 'LongPolling';
  return 'Unknown';
}

export function SignalRProvider({ children }: { children: React.ReactNode }) {
  const { getAccessToken, isAuthenticated } = useAuth();
  const [connection, setConnection] = useState<HubConnection | null>(null);
  const [connectionState, setConnectionState] = useState<HubConnectionState>(HubConnectionState.Disconnected);
  const connectionRef = useRef<HubConnection | null>(null);
  const joinedGroupsRef = useRef<Set<string>>(new Set());
  // Serial-number proof per session group (see JoinGroupOptions). Kept across disconnects so the
  // auto-rejoin after a reconnect can re-present it; entries are removed on a confirmed leave.
  const joinSerialsRef = useRef<Map<string, string>>(new Map());
  const [joinedGroups, setJoinedGroups] = useState<string[]>([]);
  const retryCountRef = useRef(0);
  const retryTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  // Collapses rapid successive accessRevoked pushes (one per revoked grant) into a single
  // restart — a second start() on a non-Disconnected connection throws and burns the retry budget.
  const revokeRestartInFlightRef = useRef(false);
  // Set in onreconnecting, read in onreconnected so we can report downtime as a measurement.
  const disconnectStartedAtRef = useRef<number | null>(null);
  const maxRetries = 3;

  // One stable token getter for every hub-side call (connection factory, join, leave, rejoin, the
  // deferred leave fired from a timer): always the latest getAccessToken without re-creating the
  // callbacks below — consumer effects list them as dependencies.
  const getAccessTokenRef = useLatest(getAccessToken);
  const getToken = useCallback<GetAccessToken>((forceRefresh) => getAccessTokenRef.current(forceRefresh), [getAccessTokenRef]);

  const syncJoinedGroups = useCallback(() => {
    setJoinedGroups(Array.from(joinedGroupsRef.current));
  }, []);

  // Reference counts per group name plus the deferred leaves; created once per provider. The
  // leave handler is subscribed below, from an effect, because it reads live refs.
  const [groupRegistry] = useState(() => createGroupRegistry({ graceMs: GROUP_LEAVE_GRACE_MS }));

  // Hub leave for a group whose last reference was released and whose grace period has passed
  // (the registry's leave listener). Fires from a timer, so it reads the live connection, never a closure.
  const leaveHubGroup = useCallback(async (groupName: string) => {
    // Never joined, or the join was refused.
    if (!joinedGroupsRef.current.has(groupName)) {
      return;
    }

    // Drop the membership first so a join arriving meanwhile re-joins instead of assuming membership.
    joinedGroupsRef.current.delete(groupName);
    syncJoinedGroups();

    const conn = connectionRef.current;
    const connectionId = conn?.state === HubConnectionState.Connected ? conn.connectionId : null;
    if (!connectionId) {
      // The membership died with the connection; there is nothing to tell the server.
      joinSerialsRef.current.delete(groupName);
      return;
    }

    try {
      await fetchOk(api.realtime.leaveGroup(), getToken, {
        method: 'POST',
        body: jsonBody<SignalRLeaveGroupRequest>({ connectionId, groupName }),
      });
      // Confirmed leave: drop the stored serial proof for this group (a refusal lands in the catch, which re-adds the group).
      joinSerialsRef.current.delete(groupName);
    } catch (error) {
      console.error(`[SignalR] Error leaving group ${groupName}:`, error);
      // Assume the server still has the membership, so the next release retries the leave.
      joinedGroupsRef.current.add(groupName);
      syncJoinedGroups();
    }
  }, [getToken, syncJoinedGroups]);

  useEffect(() => groupRegistry.onLeave((groupName) => { void leaveHubGroup(groupName); }), [groupRegistry, leaveHubGroup]);

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
    const newConnection = new HubConnectionBuilder()
      .withUrl(hubUrl, {
        accessTokenFactory: async () => {
          const token = await getToken();
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
      .configureLogging(LogLevel.Information)
      .build();

    connectionRef.current = newConnection;

    // Setup connection state change handlers
    newConnection.onclose((error) => {
      setConnectionState(HubConnectionState.Disconnected);
      trackEvent("signalr_disconnected", { hasError: !!error });
      // Memberships and references die with the connection: every consumer hook releases on the
      // Disconnected transition (a no-op against the reset registry) and re-joins on the next
      // Connected transition.
      joinedGroupsRef.current.clear();
      groupRegistry.reset();
      syncJoinedGroups();
      disconnectStartedAtRef.current = null;
    });

    newConnection.onreconnecting(() => {
      setConnectionState(HubConnectionState.Reconnecting);
      disconnectStartedAtRef.current = performance.now();
    });

    newConnection.onreconnected(async (connectionId) => {
      setConnectionState(HubConnectionState.Connected);
      retryCountRef.current = 0; // Reset retry count on successful reconnect
      const downtimeMs = disconnectStartedAtRef.current !== null
        ? Math.round(performance.now() - disconnectStartedAtRef.current)
        : 0;
      disconnectStartedAtRef.current = null;

      // Auto-rejoin after a reconnect: the server-side connection ID changed, so every group
      // membership is lost. Only groups somebody still holds (a reference, or a leave still in
      // its grace period) are re-established; the rest died with the old connection and is
      // dropped locally. The entries stay in the set while the rejoins are in flight (optimistic,
      // like joinGroup) so a consumer re-joining on the Connected transition does not issue a
      // second join; a failed rejoin is dropped and logged per group, and the consumer's next
      // join retries it. Pending leaves restart their grace period so none can race its rejoin.
      const previousGroups = Array.from(joinedGroupsRef.current);
      const rejoinGroups = previousGroups.filter((groupName) => groupRegistry.isHeld(groupName));
      for (const groupName of previousGroups) {
        if (!groupRegistry.isHeld(groupName)) {
          joinedGroupsRef.current.delete(groupName);
          joinSerialsRef.current.delete(groupName);
        }
      }
      groupRegistry.restartPendingLeaves();
      syncJoinedGroups();

      const results = await Promise.allSettled(rejoinGroups.map((groupName) =>
        fetchOk(api.realtime.joinGroup(), getToken, {
          method: 'POST',
          // Re-present the stored serial proof for session groups: a roleless
          // Progress-Portal user's rejoin is refused without it.
          body: jsonBody<SignalRJoinGroupRequest>({
            connectionId,
            groupName,
            serialNumber: joinSerialsRef.current.get(groupName),
          }),
        })));
      let rejoined = 0;
      results.forEach((result, index) => {
        if (result.status === 'fulfilled') {
          rejoined++;
          return;
        }
        const groupName = rejoinGroups[index];
        console.warn(`[SignalR] Error rejoining group ${groupName} after reconnect:`, result.reason);
        joinedGroupsRef.current.delete(groupName);
        joinSerialsRef.current.delete(groupName);
      });
      syncJoinedGroups();

      if (rejoinGroups.length > 0) {
        console.log(`[SignalR] Rejoined ${rejoined}/${rejoinGroups.length} groups after reconnect`);
      }
      trackEvent("signalr_reconnected", {
        rejoinedGroups: rejoined,
        downtimeMs,
        transport: getTransportName(newConnection),
      });
    });

    // Start connection
    const startConnection = async () => {
      try {
        await newConnection.start();
        setConnectionState(HubConnectionState.Connected);
        setConnection(newConnection);
        retryCountRef.current = 0;
        // Surfaces clients stuck on a fallback transport (e.g. corporate proxies blocking
        // WebSockets) — those sessions get slower live updates and produce long-poll 404
        // noise in dependency telemetry.
        trackEvent("signalr_connected", { transport: getTransportName(newConnection) });
      } catch (error) {
        console.error('[SignalR] Failed to start connection:', error);
        setConnectionState(HubConnectionState.Disconnected);

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

    // Server push after an operator revoked this user's (delegated) access: restart the
    // connection. onclose clears the joined-group tracking, and every consumer hook re-joins on
    // the next Connected transition — through the join endpoint, which re-runs authorization
    // against the fresh scope. Revoked groups 403; still-authorized streams recover automatically.
    newConnection.on('accessRevoked' satisfies SignalRMessageName, async () => {
      // Dropping an event that arrives mid-restart is safe: the rejoin after start() re-runs
      // authorization against the CURRENT scope, which already reflects that later revoke.
      if (revokeRestartInFlightRef.current) return;
      revokeRestartInFlightRef.current = true;
      console.warn('[SignalR] accessRevoked received — restarting connection to re-authorize group memberships');
      trackEvent('signalr_access_revoked');
      try {
        await newConnection.stop();
      } catch { /* proceed to restart regardless */ }
      try {
        await startConnection();
      } finally {
        revokeRestartInFlightRef.current = false;
      }
    });

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
      // No deferred leave may fire against a connection that is being stopped.
      groupRegistry.reset();
      if (connectionRef.current) {
        connectionRef.current.stop();
        connectionRef.current = null;
      }
    };
  }, [isAuthenticated, getToken, syncJoinedGroups, groupRegistry]);

  // Keyed on the connection object (set once start() succeeded, kept across the accessRevoked
  // stop/start of the same object): consumer effects that list on/off as dependencies register
  // once the connection exists and are not churned by connection-state or group changes.
  const on = useCallback((eventName: SignalRMessageName, callback: SignalRHandler) => {
    connection?.on(eventName, callback);
  }, [connection]);

  const off = useCallback((eventName: SignalRMessageName, callback: SignalRHandler) => {
    connection?.off(eventName, callback);
  }, [connection]);

  const invoke = useCallback(async (methodName: string, ...args: unknown[]): Promise<unknown> => {
    if (connection && connection.state === HubConnectionState.Connected) {
      return await connection.invoke(methodName, ...args);
    }
    throw new Error('SignalR connection not established');
  }, [connection]);

  const joinGroup = useCallback(async (groupName: string, options?: JoinGroupOptions) => {
    // The reference is counted first: the consumer releases it through leaveGroup whether or not
    // a hub join happens now (a consumer calling this while disconnected re-joins on the next
    // Connected transition, releasing this reference on the Disconnected one).
    groupRegistry.acquire(groupName);

    const conn = connectionRef.current;
    if (!conn || conn.state !== HubConnectionState.Connected) {
      return;
    }

    // One hub membership serves every holder: already a member, a join in flight, or a pending
    // leave whose grace period the acquire above just cancelled.
    if (joinedGroupsRef.current.has(groupName)) {
      return;
    }

    // Add to Set immediately to prevent race conditions with multiple simultaneous calls
    joinedGroupsRef.current.add(groupName);
    if (options?.serialNumber) {
      joinSerialsRef.current.set(groupName, options.serialNumber);
    }
    syncJoinedGroups();

    try {
      const connectionId = conn.connectionId;
      if (!connectionId) {
        joinedGroupsRef.current.delete(groupName); // Remove if we can't get connection ID
        syncJoinedGroups();
        return;
      }

      try {
        await fetchOk(api.realtime.joinGroup(), getToken, {
          method: 'POST',
          body: jsonBody<SignalRJoinGroupRequest>({
            connectionId,
            groupName,
            serialNumber: options?.serialNumber,
          }),
        });
      } catch (err) {
        if (!(err instanceof ApiError)) throw err;
        // Remove from Set if the join was refused (so we can retry). A refused join must be
        // VISIBLE: a silently swallowed 403 here historically left the page looking frozen
        // (no live updates, no error): see the c4dabeee regression.
        console.warn(`[SignalR] Failed to join group ${groupName} (status ${err.status})`);
        joinedGroupsRef.current.delete(groupName);
        joinSerialsRef.current.delete(groupName);
        syncJoinedGroups();
        options?.onDenied?.(err.status);
      }
    } catch (error) {
      console.error(`[SignalR] Error joining group ${groupName}:`, error);
      // Remove from Set if API call failed (so we can retry)
      joinedGroupsRef.current.delete(groupName);
      joinSerialsRef.current.delete(groupName);
      syncJoinedGroups();
    }
  }, [groupRegistry, getToken, syncJoinedGroups]);

  const leaveGroup = useCallback((groupName: string): Promise<void> => {
    // Only this consumer's reference is dropped here; the hub leave follows from the registry
    // once the last holder is gone and the grace period has passed (leaveHubGroup).
    groupRegistry.release(groupName);
    return Promise.resolve();
  }, [groupRegistry]);

  const connectionId = connection?.connectionId ?? null;
  const isConnected = connectionState === HubConnectionState.Connected;
  const value = useMemo<SignalRContextType>(() => ({
    connection,
    connectionState,
    connectionId,
    on,
    off,
    invoke,
    joinGroup,
    leaveGroup,
    isConnected,
    joinedGroups,
  }), [connection, connectionState, connectionId, on, off, invoke, joinGroup, leaveGroup, isConnected, joinedGroups]);

  return (
    <SignalRContext.Provider value={value}>
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
