import { describe, expect, it } from "vitest";
import { filterOpsEventCatalog } from "../opsAlertRuleSearch";

const CATALOG = {
  Consent: ["ConsentFlowStarted", "ConsentFlowFailed"],
  Tenant: ["OffboardingFeedbackReceived", "FeedbackReceived", "FeedbackRatingReceived", "TenantSignup"],
  Platform: ["PushDeliveryFailed", "PushEndpointRefused"],
};

describe("filterOpsEventCatalog", () => {
  it("keeps the whole catalog for an empty or blank query", () => {
    expect(filterOpsEventCatalog(CATALOG, "")).toEqual(Object.entries(CATALOG));
    expect(filterOpsEventCatalog(CATALOG, "  \t ")).toEqual(Object.entries(CATALOG));
  });

  it("finds every type that contains the term, ignoring case", () => {
    expect(filterOpsEventCatalog(CATALOG, "FEEDBACK")).toEqual([
      ["Tenant", ["OffboardingFeedbackReceived", "FeedbackReceived", "FeedbackRatingReceived"]],
    ]);
  });

  it("requires every term, so words narrow the way the type name reads", () => {
    expect(filterOpsEventCatalog(CATALOG, "feedback rating")).toEqual([["Tenant", ["FeedbackRatingReceived"]]]);
    expect(filterOpsEventCatalog(CATALOG, "push failed")).toEqual([["Platform", ["PushDeliveryFailed"]]]);
  });

  it("keeps a whole group when the term names its category", () => {
    expect(filterOpsEventCatalog(CATALOG, "consent")).toEqual([["Consent", ["ConsentFlowStarted", "ConsentFlowFailed"]]]);
    expect(filterOpsEventCatalog(CATALOG, "platform refused")).toEqual([["Platform", ["PushEndpointRefused"]]]);
  });

  it("drops categories without a match and returns nothing when no type matches", () => {
    expect(filterOpsEventCatalog(CATALOG, "failed")).toEqual([
      ["Consent", ["ConsentFlowFailed"]],
      ["Platform", ["PushDeliveryFailed"]],
    ]);
    expect(filterOpsEventCatalog(CATALOG, "telegram")).toEqual([]);
  });
});
