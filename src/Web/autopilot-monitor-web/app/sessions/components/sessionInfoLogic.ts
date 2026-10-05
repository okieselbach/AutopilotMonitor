/**
 * Direction of the device clock relative to NTP, read from the agent's `ntp_time_check`
 * event. The agent computes `offsetSeconds = NTP time − device time`
 * (`NtpTimeCheckService`), so a positive value means the device clock is BEHIND.
 */
export function deviceClockDirection(offsetSeconds: number): "ahead of" | "behind" {
  return offsetSeconds > 0 ? "behind" : "ahead of";
}
