import { describe, it, expect } from "vitest";
import { describeChannelHealth } from "@/lib/channelHealth";
import type { NotificationChannelHealthDto } from "@/utils/wire-types.generated";

const NOW = Date.parse("2026-10-10T12:00:00Z");

function dto(overrides: Partial<NotificationChannelHealthDto>): NotificationChannelHealthDto {
  return { channelId: "c1", status: "Unknown", recentAttempts: 0, recentFailures: 0, consecutiveFailures: 0, ...overrides };
}

describe("describeChannelHealth", () => {
  it("shows a healthy channel as operating normally with its last delivery", () => {
    const view = describeChannelHealth(
      dto({ status: "Ok", recentAttempts: 12, lastSuccessUtc: "2026-10-10T10:00:00Z", lastAttemptUtc: "2026-10-10T10:00:00Z" }),
      NOW,
    );
    expect(view).toEqual({ tone: "green", label: "Operating normally", title: "Last delivery 2h ago.", hint: null });
  });

  it("shows a degraded channel with its failure rate and the counts behind it", () => {
    const view = describeChannelHealth(
      dto({
        status: "Degraded",
        recentAttempts: 20,
        recentFailures: 3,
        lastSuccessUtc: "2026-10-10T11:30:00Z",
        lastFailureUtc: "2026-10-08T12:00:00Z",
        lastError: "HTTP 502 Bad Gateway",
      }),
      NOW,
    );
    expect(view.tone).toBe("amber");
    expect(view.label).toBe("Failure rate 15% (3 of 20)");
    expect(view.title).toBe("Last delivery 30m ago; last failure 2d ago: HTTP 502 Bad Gateway.");
    expect(view.hint).toBeNull();
  });

  it("shows a failing channel as an error with guidance naming the cause", () => {
    const view = describeChannelHealth(
      dto({
        status: "Failing",
        recentAttempts: 3,
        recentFailures: 3,
        consecutiveFailures: 3,
        lastFailureUtc: "2026-10-10T11:00:00Z",
        failingSinceUtc: "2026-10-08T09:00:00Z",
        lastError: "HTTP 401 Unauthorized",
      }),
      NOW,
    );
    expect(view.tone).toBe("red");
    expect(view.label).toBe("Error");
    expect(view.title).toBe("Nothing delivered yet; last failure 1h ago: HTTP 401 Unauthorized.");
    expect(view.hint).toMatch(/^The last 3 deliveries failed since .+: HTTP 401 Unauthorized\. Fix the destination, then send a test\.$/);
  });

  it("shows a channel without deliveries for its current destination as gray", () => {
    const view = describeChannelHealth(dto({ status: "Unknown" }), NOW);
    expect(view.tone).toBe("gray");
    expect(view.label).toBe("No deliveries yet");
    expect(view.hint).toBeNull();
  });
});
