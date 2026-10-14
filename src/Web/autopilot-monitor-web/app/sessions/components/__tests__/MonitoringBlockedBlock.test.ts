import { describe, it, expect } from "vitest";
import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { MonitoringBlockedBlock } from "../MonitoringBlockedBlock";
import FailureSnapshotBlock from "../FailureSnapshotBlock";
import { sessionBlockOf, sessionBlockTooltip } from "@/lib/sessionBlock";
import { makeSession } from "@/test/factories";

const BLOCKED_AT = "2026-10-14T12:00:00Z";
const BLOCKED_UNTIL = "2026-10-15T12:00:00Z";

const eventLimit = (blockAction: string) =>
  makeSession({
    blockedAt: BLOCKED_AT,
    blockedUntil: BLOCKED_UNTIL,
    blockAction,
    blockCause: "event_limit",
    blockEventCount: 2553,
    blockEventThreshold: 2500,
  });

const byOperator = (blockAction: string) =>
  makeSession({ blockedAt: BLOCKED_AT, blockedUntil: BLOCKED_UNTIL, blockAction, blockCause: "operator" });

const render = (session = makeSession()) =>
  renderToStaticMarkup(createElement(MonitoringBlockedBlock, { block: sessionBlockOf(session) }));

describe("MonitoringBlockedBlock", () => {
  it("renders nothing for a session the platform never blocked", () => {
    expect(render()).toBe("");
  });

  it("explains an event-limit pause with the session's numbers and its window", () => {
    const html = render(eventLimit("Block"));
    expect(html).toContain("Monitoring paused — event limit reached");
    expect(html).toContain("This session sent 2,553 events, above the limit of 2,500.");
    expect(html).toContain("uploads from this session are paused from");
    expect(html).toContain("the device&#x27;s next enrollment starts unblocked");
    expect(html).toContain("bg-orange-50");
    expect(html).not.toContain("bg-red-50");
  });

  it("says a kill also keeps the device's next enrollments unmonitored", () => {
    const html = render(eventLimit("Kill"));
    expect(html).toContain("Monitoring ended — event limit reached");
    expect(html).toContain("the agent stopped monitoring on");
    expect(html).toContain("new enrollments on this device are not monitored either");
    expect(html).not.toContain("starts unblocked");
    expect(html).toContain("bg-red-50");
  });

  it("names the operator and no volume for an operator block", () => {
    const html = render(byOperator("Block"));
    expect(html).toContain("Monitoring paused by the platform operator");
    expect(html).toContain("Uploads from this session are paused from");
    expect(html).not.toContain("events, above the limit");
    expect(html).not.toContain("To protect your fleet data");
  });

  it("links to the protective-limits article", () => {
    expect(render(eventLimit("Block"))).toContain("/concepts/protective-limits");
  });
});

describe("sessionBlock", () => {
  it("maps the record and words the session list's pill line", () => {
    const block = sessionBlockOf(eventLimit("Kill"))!;
    expect(block.isKill).toBe(true);
    expect(block.byOperator).toBe(false);
    expect(sessionBlockTooltip(block)).toMatch(/^Monitoring ended by the event limit on /);
    expect(sessionBlockTooltip(sessionBlockOf(byOperator("Block"))!)).toMatch(/^Monitoring paused by the platform operator on /);
  });
});

describe("FailureSnapshotBlock after a block", () => {
  const snapshot = JSON.stringify({ silenceMinutes: 300, eventCount: 2553 });

  it("names the pause instead of a silent agent", () => {
    const html = renderToStaticMarkup(createElement(FailureSnapshotBlock, { failureSnapshotJson: snapshot, afterBlock: true }));
    expect(html).toContain("last known state before monitoring stopped");
    expect(html).not.toContain("agent went silent");
  });

  it("keeps the silence title for every other timeout", () => {
    const html = renderToStaticMarkup(createElement(FailureSnapshotBlock, { failureSnapshotJson: snapshot }));
    expect(html).toContain("agent went silent");
  });
});
