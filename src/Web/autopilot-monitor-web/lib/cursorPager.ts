import { extractContinuation } from "@/lib/paginationLink";

/**
 * Click-next paging over a backend that hands out opaque continuation tokens (Pattern B1): one
 * page on screen, Next follows its nextLink, Previous returns to the cursor it came from. Pure
 * transitions, driven by hooks/useCursorPager.
 *
 * A move changes nothing until its page has loaded: a failed Next keeps the page, its number and
 * its cursors, so the counter can never run ahead of the rows on screen.
 */

/** One backend page as the caller's fetch returns it. */
export interface CursorPage<T> {
  items: T[];
  nextLink: string | null;
}

export type PageMove = "reset" | "next" | "prev" | "refresh";

export interface CursorPagerState<T> {
  items: T[];
  /** Cursor of the page on screen; null = first page. */
  continuation: string | null;
  /** Cursors of the pages before it, oldest first. */
  stack: Array<string | null>;
  /** nextLink of the page on screen; null = last page. */
  nextLink: string | null;
}

export function initialCursorPagerState<T>(): CursorPagerState<T> {
  return { items: [], continuation: null, stack: [], nextLink: null };
}

/** The cursor a move has to fetch, or undefined when the move is not possible. */
export function cursorFor<T>(state: CursorPagerState<T>, move: PageMove): string | null | undefined {
  switch (move) {
    case "reset":
      return null;
    case "refresh":
      return state.continuation;
    case "next":
      return extractContinuation(state.nextLink) ?? undefined;
    case "prev":
      return state.stack.length > 0 ? state.stack[state.stack.length - 1] : undefined;
  }
}

/** The state once the page a move fetched at `cursor` has loaded. */
export function afterSuccess<T>(
  state: CursorPagerState<T>,
  move: PageMove,
  cursor: string | null,
  page: CursorPage<T>,
): CursorPagerState<T> {
  const loaded = { items: page.items, nextLink: page.nextLink };
  switch (move) {
    case "reset":
      return { ...loaded, continuation: null, stack: [] };
    case "refresh":
      return { ...loaded, continuation: state.continuation, stack: state.stack };
    case "next":
      return { ...loaded, continuation: cursor, stack: [...state.stack, state.continuation] };
    case "prev":
      return { ...loaded, continuation: cursor, stack: state.stack.slice(0, -1) };
  }
}

export function pageNumberOf<T>(state: CursorPagerState<T>): number {
  return state.stack.length + 1;
}
