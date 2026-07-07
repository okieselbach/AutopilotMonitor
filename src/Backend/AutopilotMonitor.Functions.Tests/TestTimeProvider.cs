namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Shared fixed-clock <see cref="TimeProvider"/> for tests that need deterministic time math
/// (trial expiry, quota windows). Mutable via <see cref="SetUtcNow"/> to simulate passage of time.
/// </summary>
internal sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public TestTimeProvider(DateTimeOffset now) => _now = now;

    public void SetUtcNow(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;
}
