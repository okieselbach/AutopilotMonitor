import { describe, expect, it } from "vitest";
import { deviceClockDirection } from "../sessionInfoLogic";

describe("deviceClockDirection", () => {
  it("reads a positive NTP offset as a device clock behind UTC", () => {
    // NTP 12:37:07, device 02:36:56 -> offsetSeconds = NTP - device = +36010.3
    expect(deviceClockDirection(36010.3)).toBe("behind");
  });

  it("reads a negative NTP offset as a device clock ahead of UTC", () => {
    // NTP 10:21:22, device 19:21:22 -> offsetSeconds = -32400.3
    expect(deviceClockDirection(-32400.3)).toBe("ahead of");
  });
});
