import { describe, expect, it } from "vitest";
import { resolveRuleTargets, toggleChannelBinding } from "../opsChannelRouting";
import type { OpsAlertRule } from "../../AdminConfigContext";
import type { NotificationChannel } from "@/app/settings/types";

const channel = (id: string, enabled = true): NotificationChannel =>
  ({ id, name: id, providerType: 20, url: `https://x.example/${id}`, enabled });

const rule = (notifyChannelIds?: string[]): OpsAlertRule =>
  ({ eventType: "TenantTrialStarted", minSeverity: "Info", enabled: true, notifyChannelIds });

describe("toggleChannelBinding", () => {
  const channels = [channel("push"), channel("sales")];

  it("starts from the empty set so a broadcast rule can be narrowed", () => {
    // The rule currently reaches everything (empty = all). Picking "sales" must mean
    // ONLY sales, not "everything plus sales".
    expect(toggleChannelBinding(rule(), "sales", channels)).toEqual(["sales"]);
  });

  it("adds and removes an explicit binding", () => {
    expect(toggleChannelBinding(rule(["sales"]), "push", channels)).toEqual(["sales", "push"]);
    expect(toggleChannelBinding(rule(["sales", "push"]), "sales", channels)).toEqual(["push"]);
  });

  it("prunes ids of channels that no longer exist", () => {
    expect(toggleChannelBinding(rule(["deleted", "sales"]), "push", channels)).toEqual(["sales", "push"]);
  });

  it("returns to broadcast when the last binding is removed", () => {
    expect(toggleChannelBinding(rule(["sales"]), "sales", channels)).toEqual([]);
  });
});

describe("resolveRuleTargets", () => {
  it("an empty binding reaches every enabled channel", () => {
    const channels = [channel("push"), channel("sales", false)];
    expect(resolveRuleTargets(rule(), channels).map((c) => c.id)).toEqual(["push"]);
  });

  it("an explicit binding reaches only those channels", () => {
    const channels = [channel("push"), channel("sales")];
    expect(resolveRuleTargets(rule(["sales"]), channels).map((c) => c.id)).toEqual(["sales"]);
  });

  it("a binding to a deleted channel reaches nothing rather than everything", () => {
    expect(resolveRuleTargets(rule(["deleted"]), [channel("push")])).toEqual([]);
  });

  it("never reaches a disabled channel", () => {
    expect(resolveRuleTargets(rule(["sales"]), [channel("sales", false)])).toEqual([]);
  });
});
