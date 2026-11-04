"use client";

import { useCallback, useMemo } from "react";
import { useSearchParams } from "next/navigation";
import {
  EXCLUDE_GROUP_TAGS_PARAM,
  EXCLUDE_UNTAGGED_PARAM,
  NO_GROUP_TAG_EXCLUSION,
  parseGroupTagExclusion,
  toggleExcludedGroupTag,
  writeGroupTagExclusion,
  type GroupTagExclusion,
} from "../lib/groupTagExclusion";

export interface UseGroupTagExclusionReturn {
  exclusion: GroupTagExclusion;
  toggleTag: (tag: string) => void;
  toggleUntagged: () => void;
  clear: () => void;
}

/**
 * The group-tag view filter of Fleet Health, carried in the URL like the `?days=` window
 * (useWindowDays): the URL is the only state, so a shared link opens the same view. Writing
 * replaces the history entry without navigating; other parameters stay. Needs the page's
 * Suspense boundary (useSearchParams under the static export).
 */
export function useGroupTagExclusion(): UseGroupTagExclusionReturn {
  const searchParams = useSearchParams();
  const tagsParam = searchParams?.get(EXCLUDE_GROUP_TAGS_PARAM);
  const untaggedParam = searchParams?.get(EXCLUDE_UNTAGGED_PARAM);
  const exclusion = useMemo(() => parseGroupTagExclusion(tagsParam, untaggedParam), [tagsParam, untaggedParam]);

  const write = useCallback((next: GroupTagExclusion) => {
    const url = new URL(window.location.href);
    writeGroupTagExclusion(url.searchParams, next);
    window.history.replaceState(null, "", url.toString());
  }, []);

  const toggleTag = useCallback((tag: string) => write(toggleExcludedGroupTag(exclusion, tag)), [write, exclusion]);
  const toggleUntagged = useCallback(() => write({ ...exclusion, untagged: !exclusion.untagged }), [write, exclusion]);
  const clear = useCallback(() => write(NO_GROUP_TAG_EXCLUSION), [write]);

  return { exclusion, toggleTag, toggleUntagged, clear };
}
