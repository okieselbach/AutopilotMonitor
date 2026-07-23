using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Session 5d735290 (2026-07-23) — reads the ESP's registered policy providers from
    /// <c>HKLM\SOFTWARE\Microsoft\Windows\Autopilot\EnrollmentStatusTracking</c>, the registry
    /// mirror of the EnrollmentStatusTracking CSP
    /// (https://learn.microsoft.com/en-us/windows/client-management/mdm/enrollmentstatustracking-csp).
    /// <para>
    /// CSP contract the ESP enforces (quotes from the doc above):
    /// <list type="bullet">
    ///   <item><c>Setup/Apps/PolicyProviders/&lt;Name&gt;</c> — "Existence of this node indicates
    ///     to the ESP that it shouldn't show the tracking status message until the
    ///     TrackingPoliciesCreated node has been set to true." This wait has NO timeout: a
    ///     registered provider that never sets <c>TrackingPoliciesCreated=1</c> parks the ESP at
    ///     "Apps (Identifying)" indefinitely (observed for days in the field).</item>
    ///   <item><c>DevicePreparation/PolicyProviders/&lt;Name&gt;/InstallationState</c> — must reach
    ///     2 (NotRequired) or 3 (Completed); 1=NotInstalled, 4=Error. ESP applies a default
    ///     15-minute timeout here, so stalls in this stage are self-surfacing — still reported
    ///     for the diagnostic trail.</item>
    /// </list>
    /// Registry layout: <c>Device</c> subkey for device scope, <c>S-&lt;SID&gt;</c> subkeys for user
    /// scope (appear at sign-in), each containing <c>Setup\Apps\PolicyProviders\&lt;Name&gt;</c>;
    /// <c>DevicePreparation\PolicyProviders</c> exists in device scope only.
    /// </para>
    /// <para>
    /// Known provider names: <c>Sidecar</c> is the Intune Management Extension ("Intune's agents,
    /// such as SideCar" per the CSP doc); <c>ConfigMgr</c> is the Configuration Manager client
    /// (registered during Autopilot-into-co-management, see the troubleshooting registry paths in
    /// https://learn.microsoft.com/en-us/intune/configmgr/comanage/autopilot-enrollment). Microsoft
    /// warns that ESP policy providers are not aware of each other and that pre-provisioning is
    /// unsupported with co-management. Field case that motivated this probe: a pre-provisioned
    /// device with the ConfigMgr client installed as a blocking app ended up with ONLY
    /// <c>ConfigMgr</c> registered under <c>Device\Setup\Apps\PolicyProviders</c> (no
    /// <c>Sidecar</c>) and <c>TrackingPoliciesCreated</c> never set — the user ESP hung at
    /// "Apps (Identifying)" for hours to days; manually renaming the key to <c>Sidecar</c>
    /// unblocked it immediately. <see cref="EspPolicyProviderStallDetector"/> turns this contract
    /// into the <c>esp_policy_provider_stalled</c> tripwire.
    /// </para>
    /// <para>
    /// Fail-soft like <see cref="EspTrackingInfoProbe"/>: any failure is Debug-logged and yields
    /// <see cref="EspPolicyProviderSnapshot.Empty"/>; a missing root key (early enrollment,
    /// non-Autopilot device) is not an error. Values are parsed defensively — a missing or
    /// unparseable value counts as incomplete (fail-toward-observation).
    /// </para>
    /// </summary>
    internal static class EspPolicyProviderProbe
    {
        internal const string TrackingRootKeyPath =
            @"SOFTWARE\Microsoft\Windows\Autopilot\EnrollmentStatusTracking";

        /// <summary>Provider kind: <c>Setup\Apps\PolicyProviders</c> (TrackingPoliciesCreated wait, no timeout).</summary>
        internal const string KindSetupApps = "setupApps";

        /// <summary>Provider kind: <c>DevicePreparation\PolicyProviders</c> (InstallationState wait, 15-min OS timeout).</summary>
        internal const string KindDevicePreparation = "devicePreparation";

        /// <summary>The IME's provider name — its absence while other providers are registered is the field-case signature.</summary>
        internal const string SidecarProviderName = "Sidecar";

        internal const string DeviceScope = "device";

        private const string TrackingPoliciesCreatedValueName = "TrackingPoliciesCreated";
        private const string InstallationStateValueName = "InstallationState";

        /// <summary>
        /// Test seam — when non-null, <see cref="Read"/> delegates to this func instead of
        /// touching the live registry. Use <see cref="ScopedOverride"/> to guarantee cleanup.
        /// </summary>
        internal static Func<AgentLogger, EspPolicyProviderSnapshot> TestOverride;

        /// <summary>
        /// Reads all registered ESP policy providers. <see cref="EspPolicyProviderSnapshot.HasData"/>
        /// is <c>true</c> when the EnrollmentStatusTracking root key existed and was readable
        /// (even with no providers registered); <see cref="EspPolicyProviderSnapshot.Empty"/> otherwise.
        /// </summary>
        /// <param name="logger">Optional logger — Debug-level trace only; no warn/error.</param>
        public static EspPolicyProviderSnapshot Read(AgentLogger logger = null)
        {
            var probe = TestOverride;
            if (probe != null) return probe(logger);

            try
            {
                using (var rootKey = Registry.LocalMachine.OpenSubKey(TrackingRootKeyPath))
                {
                    if (rootKey == null)
                    {
                        logger?.Debug("EspPolicyProviderProbe: EnrollmentStatusTracking registry key not found");
                        return EspPolicyProviderSnapshot.Empty;
                    }

                    var providers = new List<PolicyProviderState>();

                    foreach (var scopeName in rootKey.GetSubKeyNames())
                    {
                        var isDevice = string.Equals(scopeName, "Device", StringComparison.OrdinalIgnoreCase);
                        // User scope repeats the Setup structure under the signed-in user's SID.
                        var isUserSid = scopeName.StartsWith("S-", StringComparison.OrdinalIgnoreCase);
                        if (!isDevice && !isUserSid) continue;

                        var scopeLabel = isDevice ? DeviceScope : "user:" + scopeName;
                        using (var scopeKey = rootKey.OpenSubKey(scopeName))
                        {
                            if (scopeKey == null) continue;

                            CollectProviders(
                                scopeKey, @"Setup\Apps\PolicyProviders", scopeLabel, KindSetupApps,
                                TrackingPoliciesCreatedValueName, providers, logger);

                            if (isDevice)
                            {
                                CollectProviders(
                                    scopeKey, @"DevicePreparation\PolicyProviders", scopeLabel, KindDevicePreparation,
                                    InstallationStateValueName, providers, logger);
                            }
                        }
                    }

                    return new EspPolicyProviderSnapshot(providers);
                }
            }
            catch (Exception ex)
            {
                logger?.Debug($"EspPolicyProviderProbe: read threw: {ex.Message}");
                return EspPolicyProviderSnapshot.Empty;
            }
        }

        private static void CollectProviders(
            RegistryKey scopeKey,
            string providersSubPath,
            string scopeLabel,
            string kind,
            string stateValueName,
            List<PolicyProviderState> providers,
            AgentLogger logger)
        {
            try
            {
                using (var providersKey = scopeKey.OpenSubKey(providersSubPath))
                {
                    if (providersKey == null) return;

                    foreach (var providerName in providersKey.GetSubKeyNames())
                    {
                        using (var providerKey = providersKey.OpenSubKey(providerName))
                        {
                            if (providerKey == null) continue;
                            var value = TryReadInt(providerKey.GetValue(stateValueName));
                            providers.Add(new PolicyProviderState(
                                name: providerName,
                                scope: scopeLabel,
                                kind: kind,
                                trackingPoliciesCreated: kind == KindSetupApps ? value : null,
                                installationState: kind == KindDevicePreparation ? value : null));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Debug($"EspPolicyProviderProbe: '{providersSubPath}' ({scopeLabel}) read threw: {ex.Message}");
            }
        }

        /// <summary>
        /// Defensive value parse: REG_DWORD expected per CSP (booleans stored as 0/1), but string
        /// forms are tolerated. Anything missing or unparseable is <c>null</c> — which the
        /// completion rule treats as incomplete.
        /// </summary>
        internal static int? TryReadInt(object rawValue)
        {
            switch (rawValue)
            {
                case null: return null;
                case int i: return i;
                case long l when l >= int.MinValue && l <= int.MaxValue: return (int)l;
                case string s when int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                    return parsed;
                case bool b: return b ? 1 : 0;
                default: return null;
            }
        }

        /// <summary>
        /// Pure completion rule per the CSP contract: <c>setupApps</c> providers are complete when
        /// <c>TrackingPoliciesCreated == 1</c>; <c>devicePreparation</c> providers when
        /// <c>InstallationState</c> is 2 (NotRequired) or 3 (Completed).
        /// </summary>
        internal static bool IsProviderComplete(string kind, int? trackingPoliciesCreated, int? installationState)
            => kind == KindSetupApps
                ? trackingPoliciesCreated == 1
                : installationState == 2 || installationState == 3;

        /// <summary>
        /// Disposable scope that sets <see cref="TestOverride"/> for the lifetime of the scope
        /// and restores the previous value on Dispose. Nestable. Internal test-only helper.
        /// </summary>
        internal sealed class ScopedOverride : IDisposable
        {
            private readonly Func<AgentLogger, EspPolicyProviderSnapshot> _previous;
            private int _disposed;

            public ScopedOverride(Func<AgentLogger, EspPolicyProviderSnapshot> probe)
            {
                _previous = TestOverride;
                TestOverride = probe;
            }

            public void Dispose()
            {
                if (System.Threading.Interlocked.Exchange(ref _disposed, 1) == 1) return;
                TestOverride = _previous;
            }
        }
    }

    /// <summary>
    /// One registered ESP policy provider with its completion-relevant state.
    /// <see cref="Key"/> is the stable identity used for dwell tracking and one-shot latching.
    /// </summary>
    internal readonly struct PolicyProviderState
    {
        public PolicyProviderState(string name, string scope, string kind, int? trackingPoliciesCreated, int? installationState)
        {
            Name = name ?? string.Empty;
            Scope = scope ?? string.Empty;
            Kind = kind ?? string.Empty;
            TrackingPoliciesCreated = trackingPoliciesCreated;
            InstallationState = installationState;
            IsComplete = EspPolicyProviderProbe.IsProviderComplete(kind, trackingPoliciesCreated, installationState);
        }

        /// <summary>Provider key name, e.g. "Sidecar" or "ConfigMgr".</summary>
        public string Name { get; }

        /// <summary><c>"device"</c> or <c>"user:S-1-5-21-…"</c>.</summary>
        public string Scope { get; }

        /// <summary><see cref="EspPolicyProviderProbe.KindSetupApps"/> or <see cref="EspPolicyProviderProbe.KindDevicePreparation"/>.</summary>
        public string Kind { get; }

        /// <summary>Raw <c>TrackingPoliciesCreated</c> value (setupApps only); <c>null</c> when missing/unparseable.</summary>
        public int? TrackingPoliciesCreated { get; }

        /// <summary>Raw <c>InstallationState</c> value (devicePreparation only); <c>null</c> when missing/unparseable.</summary>
        public int? InstallationState { get; }

        /// <summary>Completion per <see cref="EspPolicyProviderProbe.IsProviderComplete"/>.</summary>
        public bool IsComplete { get; }

        /// <summary>Stable dwell/latch identity: <c>kind|scope|name</c>.</summary>
        public string Key => Kind + "|" + Scope + "|" + Name;
    }

    /// <summary>
    /// Snapshot of all registered ESP policy providers. <see cref="HasData"/> is <c>false</c> only
    /// when the EnrollmentStatusTracking root key was missing or unreadable — an empty provider
    /// list with <see cref="HasData"/> = <c>true</c> is valid data (nothing registered yet).
    /// </summary>
    internal readonly struct EspPolicyProviderSnapshot
    {
        public static readonly EspPolicyProviderSnapshot Empty = default;

        public EspPolicyProviderSnapshot(IReadOnlyList<PolicyProviderState> providers)
        {
            HasData = true;
            Providers = providers ?? Array.Empty<PolicyProviderState>();
        }

        /// <summary><c>true</c> when the EnrollmentStatusTracking root key existed and was readable.</summary>
        public bool HasData { get; }

        /// <summary>All registered providers across scopes and kinds; empty when none registered.</summary>
        public IReadOnlyList<PolicyProviderState> Providers { get; }

        /// <summary>
        /// <c>true</c> when a <c>Setup\Apps</c> provider named "Sidecar" (the IME) is registered in
        /// any scope — its absence while other providers are registered is the co-management
        /// field-case signature.
        /// </summary>
        public bool SidecarRegistered
        {
            get
            {
                var providers = Providers;
                if (providers == null) return false;
                return providers.Any(p =>
                    p.Kind == EspPolicyProviderProbe.KindSetupApps &&
                    string.Equals(p.Name, EspPolicyProviderProbe.SidecarProviderName, StringComparison.OrdinalIgnoreCase));
            }
        }
    }
}
