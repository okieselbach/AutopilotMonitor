/**
 * Navigation started by code asks only while the settings area holds unsaved edits, and through the
 * handler the settings area published; with nothing unsaved it leaves at once.
 */
import { afterEach, describe, expect, it, vi } from "vitest";
import { leaveOrAsk, publishUnsavedChanges, type LeaveRequest } from "../unsavedChanges";

afterEach(() => publishUnsavedChanges([], null));

function request(): LeaveRequest {
  return { href: "/dashboard/", via: "search", proceed: vi.fn() };
}

describe("leaveOrAsk", () => {
  it("leaves at once while nothing is unsaved", () => {
    const leave = request();
    leaveOrAsk(leave);
    expect(leave.proceed).toHaveBeenCalledOnce();
  });

  it("hands the request to the published handler while pages hold unsaved edits", () => {
    const handler = vi.fn();
    publishUnsavedChanges(["/settings/agent/settings"], handler);
    const leave = request();
    leaveOrAsk(leave);
    expect(handler).toHaveBeenCalledWith(leave);
    expect(leave.proceed).not.toHaveBeenCalled();
  });

  it("an empty list withdraws the handler", () => {
    const handler = vi.fn();
    publishUnsavedChanges(["/settings/agent/settings"], handler);
    publishUnsavedChanges([], handler);
    const leave = request();
    leaveOrAsk(leave);
    expect(handler).not.toHaveBeenCalled();
    expect(leave.proceed).toHaveBeenCalledOnce();
  });
});
