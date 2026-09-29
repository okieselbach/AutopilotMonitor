namespace AutopilotMonitor.Shared.Models
{
    /// <summary>
    /// Bounds of the tenant setting <see cref="TenantConfiguration.HelloWaitTimeoutSeconds"/>: how
    /// long after the Enrollment Status Page closes the agent waits for Windows Hello for Business
    /// (wizard start plus setup) before it records Hello as timed out and lets the enrollment
    /// complete. Single source for the backend validation, the agent's Hello tracker and the
    /// decision engine's <c>hello_safety</c> deadline; the web settings input mirrors the bounds.
    /// </summary>
    public static class HelloWaitTimeout
    {
        /// <summary>
        /// Floor (5 minutes): the engine's built-in Hello window. Smaller stored values — the old
        /// 30 s default is still stored for many tenants — resolve to it and keep that behaviour.
        /// </summary>
        public const int MinSeconds = 300;

        /// <summary>Ceiling (1 hour).</summary>
        public const int MaxSeconds = 3600;

        /// <summary>Default for tenants that never changed the setting: the floor.</summary>
        public const int DefaultSeconds = MinSeconds;

        /// <summary>
        /// The wait a configured value stands for: clamped into
        /// [<see cref="MinSeconds"/>, <see cref="MaxSeconds"/>].
        /// </summary>
        public static int EffectiveSeconds(int configuredSeconds) =>
            configuredSeconds < MinSeconds ? MinSeconds
            : configuredSeconds > MaxSeconds ? MaxSeconds
            : configuredSeconds;
    }
}
