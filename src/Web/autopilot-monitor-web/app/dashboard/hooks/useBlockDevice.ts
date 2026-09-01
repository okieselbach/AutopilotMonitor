"use client";
import { useState } from "react";
import { api } from "@/lib/api";
import { authenticatedFetch, TokenExpiredError } from "@/lib/authenticatedFetch";
import { NotificationType } from "@/contexts/NotificationContext";
import { BULK_CONCURRENCY, runWithConcurrency, summarizeBlockOutcomes, type BlockOutcome } from "./bulkActions";

export interface BlockTarget {
  serialNumber: string;
  tenantId: string;
  deviceName?: string;
}

// Module-level so the masked value returned while scope is off is referentially stable.
const EMPTY_SET = new Set<string>();

const blockKey = (t: { tenantId: string; serialNumber: string }) => `${t.tenantId}:${t.serialNumber}`;

export function useBlockDevice(
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>,
  addNotification: (type: NotificationType, title: string, message: string, key?: string, href?: string) => void,
  adminMode: boolean,
  globalAdminMode: boolean
) {
  // Devices awaiting the user's confirmation — one for the row icon, many for Select mode.
  const [blockTargets, setBlockTargets] = useState<BlockTarget[]>([]);
  const showBlockConfirm = blockTargets.length > 0;
  const [blockingDevice, setBlockingDevice] = useState(false);
  const [blockedDevicesSet, setBlockedDevicesSet] = useState<Set<string>>(new Set());

  // Blocked devices are only meaningful while admin mode AND global admin mode are
  // both on — mask the set at read instead of clearing state in an effect. The raw
  // state stays owned by useDashboardSessions.fetchBlockedDevices, which replaces
  // it wholesale on every fetch (and empties it while scope is off).
  const hasBlockScope = adminMode && globalAdminMode;

  /**
   * Open the confirm dialog for one or more devices. Blocking is per device, so several
   * selected sessions of the same device collapse into one target.
   */
  const blockDevices = (targets: BlockTarget[]) => {
    const unique = new Map<string, BlockTarget>();
    for (const t of targets) if (!unique.has(blockKey(t))) unique.set(blockKey(t), t);
    if (unique.size === 0) return;
    setBlockTargets([...unique.values()]);
  };

  const blockOne = async (target: BlockTarget): Promise<BlockOutcome> => {
    const response = await authenticatedFetch(api.devices.block(), getAccessToken, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        tenantId: target.tenantId,
        serialNumber: target.serialNumber,
        durationHours: 24,
        reason: `Blocked from dashboard by Global Admin`
      })
    });
    if (response.ok) return { ok: true };
    const data = await response.json().catch(() => ({}));
    return { ok: false, message: data.message || `HTTP ${response.status}` };
  };

  const confirmBlock = async () => {
    if (blockTargets.length === 0) return;
    const targets = blockTargets;

    try {
      setBlockingDevice(true);

      if (targets.length === 1) {
        const target = targets[0];
        const outcome = await blockOne(target);
        if (outcome.ok) {
          setBlockTargets([]);
          addNotification('success', 'Device Blocked', `Device ${target.deviceName || target.serialNumber} blocked for 24 hours.`);
          setBlockedDevicesSet(prev => new Set(prev).add(blockKey(target)));
        } else {
          addNotification('error', 'Block failed', outcome.message, 'device-block-error');
        }
        return;
      }

      // Bulk: keep going past individual failures and report once. Network errors become
      // failed outcomes; an expired token is rethrown so the outer catch reports it once.
      const outcomes = await runWithConcurrency(targets, BULK_CONCURRENCY, async (target): Promise<BlockOutcome> => {
        try {
          return await blockOne(target);
        } catch (error) {
          if (error instanceof TokenExpiredError) throw error;
          console.error('Failed to block device:', error);
          return { ok: false, message: 'Unable to reach the backend.' };
        }
      });

      setBlockTargets([]);
      setBlockedDevicesSet(prev => {
        const next = new Set(prev);
        targets.forEach((t, i) => { if (outcomes[i].ok) next.add(blockKey(t)); });
        return next;
      });
      const summary = summarizeBlockOutcomes(outcomes);
      addNotification(summary.type, summary.title, summary.message, 'device-bulk-block-summary');
    } catch (error) {
      if (error instanceof TokenExpiredError) {
        addNotification('error', 'Session Expired', error.message, 'session-expired-error');
      } else {
        console.error('Failed to block device:', error);
        addNotification('error', 'Block failed', 'Could not block the device. Please try again.', 'device-block-error');
      }
    } finally {
      setBlockingDevice(false);
    }
  };

  const cancelBlock = () => {
    setBlockTargets([]);
  };

  return {
    showBlockConfirm,
    blockTargets,
    blockingDevice,
    blockedDevicesSet: hasBlockScope ? blockedDevicesSet : EMPTY_SET,
    setBlockedDevicesSet,
    blockDevices,
    confirmBlock,
    cancelBlock,
  };
}
