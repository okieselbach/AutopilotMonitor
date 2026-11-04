/**
 * The Fleet Health group-tag view filter, carried in the URL as `?excludeGroupTags=TEST,LAB` and
 * `?excludeUntagged=true`. Mirrors the backend's FleetGroupTagFilter: tags compare trimmed and
 * without case, at most {@link MAX_EXCLUDED_GROUP_TAGS}. Pure, so vitest pins it without rendering.
 */
export const EXCLUDE_GROUP_TAGS_PARAM = "excludeGroupTags";
export const EXCLUDE_UNTAGGED_PARAM = "excludeUntagged";
export const MAX_EXCLUDED_GROUP_TAGS = 20;

export interface GroupTagExclusion {
  /** Excluded group tags: trimmed, de-duplicated without case. */
  readonly tags: readonly string[];
  /** Whether the sessions without a group tag leave the view. */
  readonly untagged: boolean;
}

export const NO_GROUP_TAG_EXCLUSION: GroupTagExclusion = { tags: [], untagged: false };

export function parseGroupTagExclusion(
  tagsParam: string | null | undefined,
  untaggedParam: string | null | undefined,
): GroupTagExclusion {
  const seen = new Set<string>();
  const tags: string[] = [];
  for (const raw of (tagsParam ?? "").split(",")) {
    const tag = raw.trim();
    if (!tag || seen.has(tag.toLowerCase())) continue;
    seen.add(tag.toLowerCase());
    tags.push(tag);
    if (tags.length === MAX_EXCLUDED_GROUP_TAGS) break;
  }
  return { tags, untagged: untaggedParam?.trim().toLowerCase() === "true" };
}

export function isGroupTagExclusionActive(exclusion: GroupTagExclusion): boolean {
  return exclusion.tags.length > 0 || exclusion.untagged;
}

/** Hides or shows one tag (compared without case); hiding stops at the cap. */
export function toggleExcludedGroupTag(exclusion: GroupTagExclusion, tag: string): GroupTagExclusion {
  const key = tag.trim().toLowerCase();
  if (!key) return exclusion;
  const remaining = exclusion.tags.filter((t) => t.toLowerCase() !== key);
  if (remaining.length !== exclusion.tags.length) return { ...exclusion, tags: remaining };
  if (exclusion.tags.length >= MAX_EXCLUDED_GROUP_TAGS) return exclusion;
  return { ...exclusion, tags: [...exclusion.tags, tag.trim()] };
}

/** Writes the filter into URL search params; an empty filter removes both parameters. */
export function writeGroupTagExclusion(params: URLSearchParams, exclusion: GroupTagExclusion): void {
  if (exclusion.tags.length > 0) params.set(EXCLUDE_GROUP_TAGS_PARAM, exclusion.tags.join(","));
  else params.delete(EXCLUDE_GROUP_TAGS_PARAM);
  if (exclusion.untagged) params.set(EXCLUDE_UNTAGGED_PARAM, "true");
  else params.delete(EXCLUDE_UNTAGGED_PARAM);
}

/** "group tag TEST", "group tags TEST, LAB", "sessions without a group tag" or both parts joined. */
export function describeGroupTagExclusion(exclusion: GroupTagExclusion): string {
  const parts: string[] = [];
  if (exclusion.tags.length === 1) parts.push(`group tag ${exclusion.tags[0]}`);
  else if (exclusion.tags.length > 1) parts.push(`group tags ${exclusion.tags.join(", ")}`);
  if (exclusion.untagged) parts.push(parts.length > 0 ? "sessions without one" : "sessions without a group tag");
  return parts.join(" and ");
}
