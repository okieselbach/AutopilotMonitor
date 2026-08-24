using System;
using System.Net.NetworkInformation;
using System.Threading.Tasks;
using AutopilotMonitor.Agent.V2.Core.Logging;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Transport
{
    /// <summary>
    /// Boot-time NIC grace shared by every first-contact backend call (remote-config fetch,
    /// session registration, emergency-break reporter pattern): polls the cheap link-level
    /// signal once per second for at most <paramref name="maxWait"/>. It cannot prove backend
    /// reachability — the callers' retry loops handle that — it only keeps retry budgets from
    /// being burned into a link that is provably still down (BootTrigger relaunch after a
    /// mid-enrollment reboot, Wi-Fi still associating; tenant aebdce78 audits 2026-08-23/24).
    /// Free on the normal path — a live link returns immediately. Probe errors end the wait,
    /// never the caller's operation.
    /// </summary>
    internal static class NetworkLinkWait
    {
        internal static async Task WaitAsync(AgentLogger logger, TimeSpan maxWait, string context)
        {
            if (NetworkInterface.GetIsNetworkAvailable()) return;

            logger?.Info($"{context}: no network link yet — waiting up to {maxWait.TotalSeconds:F0}s.");
            var deadline = DateTime.UtcNow + maxWait;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(1000).ConfigureAwait(false);
                if (NetworkInterface.GetIsNetworkAvailable())
                {
                    logger?.Info($"{context}: network link is up.");
                    return;
                }
            }
            logger?.Warning($"{context}: still no network link after {maxWait.TotalSeconds:F0}s — attempting anyway.");
        }
    }
}
