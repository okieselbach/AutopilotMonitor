namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Bounds of the tenant setting <see cref="TenantConfiguration.HelloWaitTimeoutSeconds"/> —
    /// how long after the Enrollment Status Page closes the agent waits for Windows Hello for
    /// Business (wizard start plus setup) before it records Hello as timed out. Single source for
    /// the backend validation, the agent's Hello tracker and the decision engine's
    /// <c>hello_safety</c> deadline; the web settings input mirrors the configurable bounds.
    /// </summary>
    public static class HelloWaitTimeout
    {
        /// <summary>Smallest configurable value.</summary>
        public const int MinSeconds = 30;

        /// <summary>Largest configurable value (1 hour) — also the ceiling of the effective wait.</summary>
        public const int MaxSeconds = 3600;

        /// <summary>Default for tenants that never changed the setting.</summary>
        public const int DefaultSeconds = 30;

        /// <summary>
        /// The engine's built-in Hello window (5 minutes). Configured values up to it — the
        /// default included — keep it; only larger values extend the wait.
        /// </summary>
        public const int BuiltInSeconds = 300;

        /// <summary>
        /// The wait a configured value stands for: clamped into
        /// [<see cref="BuiltInSeconds"/>, <see cref="MaxSeconds"/>].
        /// </summary>
        public static int EffectiveSeconds(int configuredSeconds) =>
            configuredSeconds < BuiltInSeconds ? BuiltInSeconds
            : configuredSeconds > MaxSeconds ? MaxSeconds
            : configuredSeconds;
    }
}
