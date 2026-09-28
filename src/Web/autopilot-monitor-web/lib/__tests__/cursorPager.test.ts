import { describe, expect, it } from "vitest";
import {
  afterSuccess,
  cursorFor,
  initialCursorPagerState,
  pageNumberOf,
  type CursorPage,
  type CursorPagerState,
  type PageMove,
} from "../cursorPager";

/**
 * Pattern B1 transitions behind hooks/useCursorPager. The audit page used to bump its page counter
 * before the Next request and never took it back: a failed request left "Page 3" over the rows of
 * page 1. Here a move only exists as (cursor to fetch) → (state once that page has loaded), so a
 * failed move cannot change anything.
 */

const link = (cursor: string) => `/api/global/audit/logs?pageSize=15&continuation=${encodeURIComponent(cursor)}&dateFrom=x`;
const page = (items: string[], next: string | null): CursorPage<string> => ({ items, nextLink: next ? link(next) : null });

/** Drives a move the way the hook does: resolve the cursor, fetch, commit on success. */
function move(state: CursorPagerState<string>, m: PageMove, fetched: CursorPage<string> | null) {
  const cursor = cursorFor(state, m);
  if (cursor === undefined || fetched === null) return state;
  return afterSuccess(state, m, cursor, fetched);
}

describe("cursorPager", () => {
  it("starts empty on page 1", () => {
    const s = initialCursorPagerState<string>();
    expect(pageNumberOf(s)).toBe(1);
    expect(s.items).toEqual([]);
    expect(cursorFor(s, "next")).toBeUndefined();
    expect(cursorFor(s, "prev")).toBeUndefined();
  });

  it("walks forward and back with the cursors the backend handed out", () => {
    let s = move(initialCursorPagerState(), "reset", page(["a"], "c2"));
    expect(s).toMatchObject({ items: ["a"], continuation: null, stack: [] });

    expect(cursorFor(s, "next")).toBe("c2");
    s = move(s, "next", page(["b"], "c3"));
    expect(pageNumberOf(s)).toBe(2);
    expect(s.continuation).toBe("c2");

    s = move(s, "next", page(["c"], null));
    expect(pageNumberOf(s)).toBe(3);
    expect(cursorFor(s, "next")).toBeUndefined(); // last page

    expect(cursorFor(s, "prev")).toBe("c2");
    s = move(s, "prev", page(["b"], "c3"));
    expect(pageNumberOf(s)).toBe(2);
    expect(s.items).toEqual(["b"]);

    expect(cursorFor(s, "prev")).toBeNull(); // back to the first page
    s = move(s, "prev", page(["a"], "c2"));
    expect(pageNumberOf(s)).toBe(1);
    expect(cursorFor(s, "prev")).toBeUndefined();
  });

  it("keeps page, rows and cursors when the Next request fails", () => {
    const onPage2 = move(move(initialCursorPagerState(), "reset", page(["a"], "c2")), "next", page(["b"], "c3"));

    const afterFailure = move(onPage2, "next", null);

    expect(afterFailure).toBe(onPage2);
    expect(pageNumberOf(afterFailure)).toBe(2);
    expect(cursorFor(afterFailure, "next")).toBe("c3"); // the same Next can be retried
  });

  it("refresh reloads the page on screen without moving", () => {
    const onPage2 = move(move(initialCursorPagerState(), "reset", page(["a"], "c2")), "next", page(["b"], "c3"));

    expect(cursorFor(onPage2, "refresh")).toBe("c2");
    const refreshed = move(onPage2, "refresh", page(["b", "new"], null));

    expect(pageNumberOf(refreshed)).toBe(2);
    expect(refreshed.items).toEqual(["b", "new"]);
    expect(refreshed.nextLink).toBeNull();
  });

  it("reset returns to the first page from anywhere", () => {
    const onPage3 = move(
      move(move(initialCursorPagerState(), "reset", page(["a"], "c2")), "next", page(["b"], "c3")),
      "next",
      page(["c"], null),
    );

    expect(cursorFor(onPage3, "reset")).toBeNull();
    const reset = move(onPage3, "reset", page(["a2"], "c9"));

    expect(pageNumberOf(reset)).toBe(1);
    expect(reset).toMatchObject({ items: ["a2"], continuation: null, stack: [] });
  });

  it("reads the continuation out of an absolute nextLink too", () => {
    const s = afterSuccess(initialCursorPagerState<string>(), "reset", null, {
      items: [],
      nextLink: "https://api.example.test/api/ops-events?continuation=abc%2B%2F%3D&pageSize=50",
    });
    expect(cursorFor(s, "next")).toBe("abc+/=");
  });
});
