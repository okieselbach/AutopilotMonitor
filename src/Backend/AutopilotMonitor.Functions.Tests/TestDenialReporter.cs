using System.Collections.Generic;
using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Recording stand-in for <see cref="IPrivilegedDenialReporter"/> so middleware tests can build a
/// <c>PolicyEnforcementMiddleware</c> without the ops-event/notification stack and still assert
/// what the deny path would have reported.
/// </summary>
internal sealed class RecordingDenialReporter : IPrivilegedDenialReporter
{
    public List<PrivilegedDenial> Reported { get; } = new();

    public void Report(PrivilegedDenial denial)
    {
        lock (Reported) Reported.Add(denial);
    }
}
