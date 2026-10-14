"use client";

import { useCallback, useEffect, useState } from "react";
import { api } from "@/lib/api";
import { apiErrorText, fetchJson, jsonBody } from "@/lib/apiClient";
import { useAdminMode } from "@/hooks/useAdminMode";
import { useLatest } from "@/hooks/useLatest";
import {
  buildPutBody,
  hasContent,
  visibleLanes,
  validateNote,
  writableLaneSet,
  type AnnotationLane,
  type AnnotationUser,
  type SessionAnnotationDto,
} from "../components/sessionAnnotationLogic";
import type { UpsertSessionAnnotationRequest } from "@/lib/generated/wire-types.generated";

interface AnnotationsResponse {
  success: boolean;
  annotations?: SessionAnnotationDto[] | null;
  /** Lanes the caller may write, per the backend matrix. Absent on older backends → read-only. */
  writableLanes?: string[] | null;
}

export interface LaneEditState {
  verdict: string | null;
  note: string;
  saving: boolean;
  /** "saved" | error message | null. Typing clears it. */
  saveResult: string | null;
}

const EMPTY_EDIT: LaneEditState = { verdict: null, note: "", saving: false, saveResult: null };

/**
 * Session annotations: per-lane human verdict + note about this enrollment's analysis (Operator /
 * Tenant Admin / platform team). Which lanes are writable comes from the SERVER (`writableLanes` on
 * the GET — the same matrix the PUT re-gates with), so UI and enforcement cannot drift. The read is
 * fail-soft: an error leaves the empty state.
 */
export function useSessionAnnotations({
  sessionId,
  effectiveTenantId,
  user,
  getAccessToken,
}: {
  sessionId: string;
  /** Tenant used for the API calls (resolved session tenant / GA override); undefined = own tenant. */
  effectiveTenantId?: string;
  user: AnnotationUser | null | undefined;
  getAccessToken: (forceRefresh?: boolean) => Promise<string | null>;
}) {
  const [annotations, setAnnotations] = useState<Partial<Record<string, SessionAnnotationDto>>>({});
  const [writableLanes, setWritableLanes] = useState<Set<string>>(new Set());
  const [loaded, setLoaded] = useState(false);
  const [edit, setEdit] = useState<Partial<Record<string, LaneEditState>>>({});
  const getToken = useLatest(getAccessToken);

  // Effective Global-Admin view (demo mode forces it off): the internal lane follows the view, so
  // presenting the session page shows the same two lanes a tenant admin sees.
  const { globalAdminMode } = useAdminMode();
  const lanes = visibleLanes(user, globalAdminMode);

  useEffect(() => {
    if (!sessionId) return;
    let cancelled = false;
    (async () => {
      try {
        const json = await fetchJson<AnnotationsResponse>(api.sessions.annotations(sessionId, effectiveTenantId), (force?: boolean) => getToken.current(force));
        if (cancelled) return;
        const byLane: Partial<Record<string, SessionAnnotationDto>> = {};
        for (const a of json.annotations ?? []) byLane[a.lane] = a;
        setAnnotations(byLane);
        setWritableLanes(writableLaneSet(json.writableLanes));
        const seeded: Partial<Record<string, LaneEditState>> = {};
        for (const [lane, a] of Object.entries(byLane)) {
          seeded[lane] = { verdict: a?.verdict ?? null, note: a?.note ?? "", saving: false, saveResult: null };
        }
        setEdit(seeded);
      } catch {
        // fail-soft: the empty state
      } finally {
        if (!cancelled) setLoaded(true);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [sessionId, effectiveTenantId, getToken]);

  const laneEdit = (lane: AnnotationLane): LaneEditState => edit[lane] ?? EMPTY_EDIT;

  const setLaneEdit = useCallback((lane: AnnotationLane, patch: Partial<LaneEditState>) => {
    setEdit((prev) => ({ ...prev, [lane]: { ...(prev[lane] ?? EMPTY_EDIT), ...patch } }));
  }, []);

  const handleSave = async (lane: AnnotationLane) => {
    const state = laneEdit(lane);
    const noteError = validateNote(state.note);
    if (noteError) {
      setLaneEdit(lane, { saveResult: noteError });
      return;
    }
    const { body, isClear } = buildPutBody(state.verdict, state.note);
    setLaneEdit(lane, { saving: true, saveResult: null });
    try {
      const json = await fetchJson<{ success: boolean; annotation?: SessionAnnotationDto }>(
        api.sessions.annotation(sessionId, lane, effectiveTenantId),
        (force?: boolean) => getToken.current(force),
        { method: "PUT", body: jsonBody<UpsertSessionAnnotationRequest>(body) }
      );
      setAnnotations((prev) => {
        const next = { ...prev };
        if (isClear) delete next[lane];
        else if (json.annotation) next[lane] = json.annotation;
        return next;
      });
      setLaneEdit(lane, { saving: false, saveResult: "saved" });
    } catch (err) {
      setLaneEdit(lane, { saving: false, saveResult: apiErrorText(err, "Failed to save annotation") });
    }
  };

  const annotatedLanes = lanes.filter((lane) => hasContent(annotations[lane]));

  return { lanes, annotations, writableLanes, loaded, laneEdit, setLaneEdit, handleSave, annotatedLanes };
}

export type SessionAnnotationsState = ReturnType<typeof useSessionAnnotations>;
