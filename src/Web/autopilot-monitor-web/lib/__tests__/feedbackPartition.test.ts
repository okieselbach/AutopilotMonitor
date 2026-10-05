import { describe, it, expect } from "vitest";
import {
  partitionFeedback,
  paginate,
  avgRating,
  type FeedbackEntryLike,
} from "../feedbackPartition";

function mkInApp(overrides: Partial<FeedbackEntryLike> = {}): FeedbackEntryLike {
  return {
    type: "InApp",
    upn: "alice@contoso.invalid",
    tenantId: "tenant-1",
    rating: 5,
    comment: "Great tool.",
    dismissed: false,
    submitted: true,
    interactedAt: "2026-05-19T09:00:00Z",
    historyRowKey: null,
    domainName: null,
    ...overrides,
  };
}

function mkOffb(overrides: Partial<FeedbackEntryLike> = {}): FeedbackEntryLike {
  return {
    type: "Offboarding",
    upn: "bob@fabrikam.invalid",
    tenantId: "tenant-2",
    rating: null,
    comment: "Pricing too high.",
    dismissed: false,
    submitted: false,
    interactedAt: "2026-05-19T09:30:00Z",
    historyRowKey: "20260519093000000_tenant-2",
    domainName: "fabrikam.invalid",
    ...overrides,
  };
}

function mkGeneral(overrides: Partial<FeedbackEntryLike> = {}): FeedbackEntryLike {
  return {
    type: "General",
    upn: "dave@contoso.invalid",
    tenantId: "tenant-1",
    rating: null,
    comment: "The timeline filter could remember my choice.",
    dismissed: false,
    submitted: false,
    interactedAt: "2026-10-05T09:30:00Z",
    historyRowKey: null,
    domainName: "contoso.invalid",
    feedbackId: "2516114433936827287_0123456789ab",
    contactEmail: "dave.support@contoso.invalid",
    ...overrides,
  };
}

describe("partitionFeedback", () => {
  it("splits mixed list by type discriminator", () => {
    const { general, inApp, offboarding } = partitionFeedback([
      mkInApp(),
      mkOffb(),
      mkGeneral(),
      mkInApp({ upn: "carol@contoso.invalid" }),
    ]);
    expect(general).toHaveLength(1);
    expect(general[0].feedbackId).toBe("2516114433936827287_0123456789ab");
    expect(inApp).toHaveLength(2);
    expect(offboarding).toHaveLength(1);
    expect(offboarding[0].historyRowKey).toBe("20260519093000000_tenant-2");
  });

  it("drops entries with unknown discriminator (future-proofing)", () => {
    const { general, inApp, offboarding } = partitionFeedback([
      mkInApp(),
      { ...mkInApp(), type: "FutureKind" as never },
    ]);
    expect(general).toHaveLength(0);
    expect(inApp).toHaveLength(1);
    expect(offboarding).toHaveLength(0);
  });

  it("returns empty partitions when input is empty", () => {
    const { general, inApp, offboarding } = partitionFeedback<FeedbackEntryLike>([]);
    expect(general).toHaveLength(0);
    expect(inApp).toHaveLength(0);
    expect(offboarding).toHaveLength(0);
  });

  it("accepts the wire shape, where absent keys are undefined", () => {
    const { general } = partitionFeedback([{ type: "General" }, { type: undefined }, {}]);
    expect(general).toHaveLength(1);
  });
});

describe("paginate", () => {
  it("returns the right slice + total page count", () => {
    const items = [1, 2, 3, 4, 5, 6, 7];
    expect(paginate(items, 0, 3)).toEqual({ items: [1, 2, 3], totalPages: 3 });
    expect(paginate(items, 1, 3)).toEqual({ items: [4, 5, 6], totalPages: 3 });
    expect(paginate(items, 2, 3)).toEqual({ items: [7], totalPages: 3 });
  });

  it("zero perPage gives empty result (defense against UI divide-by-zero)", () => {
    expect(paginate([1, 2, 3], 0, 0)).toEqual({ items: [], totalPages: 0 });
  });

  it("empty list collapses to zero pages so pagination UI hides", () => {
    expect(paginate([], 0, 5)).toEqual({ items: [], totalPages: 0 });
  });
});

describe("avgRating", () => {
  it("averages submitted ratings with one decimal", () => {
    expect(avgRating([
      { submitted: true, rating: 5 },
      { submitted: true, rating: 4 },
      { submitted: true, rating: 3 },
    ])).toBe("4.0");
  });

  it("ignores dismissed / non-submitted entries", () => {
    expect(avgRating([
      { submitted: true, rating: 5 },
      { submitted: false, rating: null },
      { submitted: false, rating: 2 }, // not submitted → skip
    ])).toBe("5.0");
  });

  it("returns the em-dash when no submitted ratings exist", () => {
    expect(avgRating([
      { submitted: false, rating: null },
      { submitted: false, rating: 4 },
    ])).toBe("—");
  });
});
