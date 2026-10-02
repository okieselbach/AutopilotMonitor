using System;
using System.Text.RegularExpressions;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Sorts a Windows Update client event into the update class that decides how it is reported
    /// (D-310). Only <see cref="Os"/> updates — Windows quality/cumulative/servicing-stack/feature
    /// updates, .NET Framework and the Malicious Software Removal Tool — are enrollment-relevant and
    /// emitted one by one. Defender intelligence and Store app updates arrive every few minutes on a
    /// fresh device (up to 171 downloads per hour in the field census) and are only counted.
    /// </summary>
    internal static class WindowsUpdateClassifier
    {
        public const string Os = "os";
        public const string Defender = "defender";
        public const string Store = "store";
        public const string Other = "other";

        /// <summary>The Microsoft Store service; WU events carry it as <c>serviceGuid</c>.</summary>
        internal const string StoreServiceGuid = "855e8a7c-ecb4-4ca3-b045-1dfa50104289";

        // Store packages are titled "<12-char product id>-<Publisher.App>", e.g. "9NBLGGH4NNS1-Microsoft.DesktopAppInstaller";
        // bundles add an "ApplicationSet-" prefix.
        private static readonly Regex StoreProductTitle =
            new Regex(@"^(ApplicationSet-)?[0-9A-Z]{12}-\S", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

        private static readonly Regex DefenderTitle =
            new Regex(@"Microsoft Defender|Windows Defender", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

        // "Windows" plus "Update"/"Upgrade" covers cumulative, quality, security, servicing-stack,
        // dynamic and feature updates in both the GA and the Insider title forms.
        private static readonly Regex WindowsUpdateTitle =
            new Regex(@"\bWindows\b.*\b(Update|Upgrade)\b|\b(Update|Upgrade)\b.*\bWindows\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

        private static readonly Regex OsTitleExtras =
            new Regex(@"\.NET Framework|Servicing Stack|Malicious Software Removal Tool",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

        public static string Classify(string updateTitle, string serviceGuid)
        {
            if (!string.IsNullOrEmpty(serviceGuid)
                && serviceGuid.Trim('{', '}', ' ').Equals(StoreServiceGuid, StringComparison.OrdinalIgnoreCase))
            {
                return Store;
            }

            if (string.IsNullOrWhiteSpace(updateTitle)) return Other;

            try
            {
                if (StoreProductTitle.IsMatch(updateTitle)) return Store;
                if (DefenderTitle.IsMatch(updateTitle)) return Defender;
                if (OsTitleExtras.IsMatch(updateTitle) || WindowsUpdateTitle.IsMatch(updateTitle)) return Os;
            }
            catch (RegexMatchTimeoutException)
            {
                // A pathological title must never cost the event — it is reported as "other".
            }

            return Other;
        }
    }
}
