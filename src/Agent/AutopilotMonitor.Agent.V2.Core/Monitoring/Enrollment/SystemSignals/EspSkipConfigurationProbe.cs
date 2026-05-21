using System;
using AutopilotMonitor.Agent.V2.Core.Logging;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Plan §6 Fix 7/9 — single source of truth for the Autopilot-enrollment ESP skip flags
    /// (<c>SkipUserStatusPage</c> / <c>SkipDeviceStatusPage</c>) that the MDM CSP writes under
    /// <c>HKLM\SOFTWARE\Microsoft\Enrollments\{guid}\FirstSync</c> during FirstSync.
    /// <para>
    /// Two consumers:
    /// <list type="bullet">
    ///   <item><see cref="Telemetry.DeviceInfo.DeviceInfoCollector"/> — emits the
    ///     <c>esp_config_detected</c> event + the <c>EspConfigDetected</c> decision signal
    ///     at agent start and at first DeviceSetup-phase detection.</item>
    ///   <item><see cref="EspAndHelloTracker"/> — guards the coordinator's
    ///     synthetic <c>EspPhaseChanged(FinalizingSetup)</c> forward: a Classic V1 enrollment
    ///     with <c>SkipUser=false</c> sees TWO <c>esp_exiting</c> events (Device-ESP exit and
    ///     Account-ESP exit) and only the second is a true final exit.</item>
    /// </list>
    /// Reading from the same authoritative registry location in both places keeps the reducer's
    /// <see cref="AutopilotMonitor.DecisionCore.State.DecisionState.SkipUserEsp"/> fact and the
    /// tracker's guard in lockstep — the alternative (passing the value around) has too many
    /// subtle lifecycle order issues to be safe.
    /// </para>
    /// <para>
    /// CSP values: <c>0xFFFFFFFF</c> = skip (ESP page not shown), <c>0</c> = show, key missing
    /// = unknown. Defensive interpretation: non-null AND non-zero = skip.
    /// </para>
    /// <para>
    /// The same <c>FirstSync</c> key also holds the user-facing ESP error-handling toggles —
    /// see <see cref="ReadFull"/> for <c>BlockInStatusPage</c> (bitmask of AllowReset /
    /// AllowTryAgain / AllowContinueAnyway) and <c>SyncFailureTimeout</c> (minutes). The
    /// bitmask semantics are documented in Microsoft's <c>Get-AutopilotDiagnostics.ps1</c>
    /// (PowerShell Gallery): bit 1 = ESP allow reset, bit 2 = ESP allow try again, bit 4 =
    /// ESP continue anyway. Used by Intune's "Allow users to use device if installation
    /// error occurs" / "Allow users to reset device if installation error occurs" settings.
    /// </para>
    /// </summary>
    internal static class EspSkipConfigurationProbe
    {
        internal const string EnrollmentsKeyPath = @"SOFTWARE\Microsoft\Enrollments";
        internal const int MdmEnrollmentType = 6;

        /// <summary>
        /// BlockInStatusPage bitmask flag — ESP allows the user to reset the device when an
        /// installation error occurs. Maps to the Intune ESP setting
        /// "Allow users to reset device if installation error occurs".
        /// </summary>
        internal const int BlockInStatusPageAllowResetBit = 1;

        /// <summary>
        /// BlockInStatusPage bitmask flag — ESP allows the user to retry installation when an
        /// installation error occurs.
        /// </summary>
        internal const int BlockInStatusPageAllowTryAgainBit = 2;

        /// <summary>
        /// BlockInStatusPage bitmask flag — ESP allows the user to continue past an installation
        /// error and use the device anyway. Maps to the Intune ESP setting
        /// "Allow users to use device if installation error occurs". When set, the ESP failure
        /// screen presents a "Continue anyway" button; the user can dismiss ESP without a
        /// successful enrollment outcome from Microsoft's side.
        /// </summary>
        internal const int BlockInStatusPageAllowContinueAnywayBit = 4;

        /// <summary>
        /// Test seam — when non-null, <see cref="Read"/> delegates to this func instead of
        /// touching the live registry. Internal so only in-repo test projects (via
        /// InternalsVisibleTo) can set it. Production code never assigns this.
        /// <para>
        /// Tests that set this MUST reset it to <c>null</c> in Dispose to avoid cross-test
        /// contamination; use the <see cref="ScopedOverride"/> helper to guarantee cleanup.
        /// </para>
        /// </summary>
        internal static Func<AgentLogger, (bool? skipUser, bool? skipDevice)> TestOverride;

        /// <summary>
        /// Test seam for <see cref="ReadFull"/> — when non-null, the full-snapshot read
        /// delegates to this func instead of touching the live registry. Use
        /// <see cref="ScopedFullOverride"/> to guarantee cleanup.
        /// </summary>
        internal static Func<AgentLogger, EspFirstSyncSnapshot> FullTestOverride;

        /// <summary>
        /// Reads the current device's <c>SkipUserStatusPage</c> / <c>SkipDeviceStatusPage</c>
        /// flags. Returns <c>(null, null)</c> when the enrollment key is missing or unreadable —
        /// callers must treat <c>null</c> as "unknown", not "false".
        /// </summary>
        /// <param name="logger">Optional logger — Debug-level trace only; no warn/error.</param>
        public static (bool? skipUser, bool? skipDevice) Read(AgentLogger logger = null)
        {
            var probe = TestOverride;
            if (probe != null) return probe(logger);

            bool? skipUser = null;
            bool? skipDevice = null;

            try
            {
                using (var enrollmentsKey = Registry.LocalMachine.OpenSubKey(EnrollmentsKeyPath))
                {
                    if (enrollmentsKey == null)
                    {
                        logger?.Debug("EspSkipConfigurationProbe: Enrollments registry key not found");
                        return (null, null);
                    }

                    foreach (var guid in enrollmentsKey.GetSubKeyNames())
                    {
                        using (var enrollmentKey = enrollmentsKey.OpenSubKey(guid))
                        {
                            if (enrollmentKey == null) continue;

                            var enrollmentType = enrollmentKey.GetValue("EnrollmentType");
                            if (enrollmentType == null || Convert.ToInt32(enrollmentType) != MdmEnrollmentType)
                                continue;

                            using (var firstSyncKey = enrollmentKey.OpenSubKey("FirstSync"))
                            {
                                if (firstSyncKey == null)
                                {
                                    logger?.Debug($"EspSkipConfigurationProbe: FirstSync subkey not found for enrollment {guid}");
                                    return (null, null);
                                }

                                var rawSkipUser = firstSyncKey.GetValue("SkipUserStatusPage");
                                if (rawSkipUser != null)
                                    skipUser = Convert.ToInt32(rawSkipUser) != 0;

                                var rawSkipDevice = firstSyncKey.GetValue("SkipDeviceStatusPage");
                                if (rawSkipDevice != null)
                                    skipDevice = Convert.ToInt32(rawSkipDevice) != 0;
                            }

                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Debug($"EspSkipConfigurationProbe: read threw: {ex.Message}");
            }

            return (skipUser, skipDevice);
        }

        /// <summary>
        /// Reads the full <c>FirstSync</c> snapshot: skip flags plus the user-facing ESP
        /// error-handling settings <c>BlockInStatusPage</c> (bitmask of AllowReset /
        /// AllowTryAgain / AllowContinueAnyway, see the <c>BlockInStatusPage*Bit</c> constants)
        /// and <c>SyncFailureTimeout</c> (in minutes, Intune ESP setting
        /// "Show error when installation takes longer than"). Any field is <c>null</c> when
        /// the corresponding registry value is missing or unreadable; callers must treat
        /// <c>null</c> as "unknown".
        /// </summary>
        /// <param name="logger">Optional logger — Debug-level trace only; no warn/error.</param>
        public static EspFirstSyncSnapshot ReadFull(AgentLogger logger = null)
        {
            var probe = FullTestOverride;
            if (probe != null) return probe(logger);

            bool? skipUser = null;
            bool? skipDevice = null;
            int? blockInStatusPage = null;
            int? syncFailureTimeout = null;

            try
            {
                using (var enrollmentsKey = Registry.LocalMachine.OpenSubKey(EnrollmentsKeyPath))
                {
                    if (enrollmentsKey == null)
                    {
                        logger?.Debug("EspSkipConfigurationProbe: Enrollments registry key not found");
                        return EspFirstSyncSnapshot.Empty;
                    }

                    foreach (var guid in enrollmentsKey.GetSubKeyNames())
                    {
                        using (var enrollmentKey = enrollmentsKey.OpenSubKey(guid))
                        {
                            if (enrollmentKey == null) continue;

                            var enrollmentType = enrollmentKey.GetValue("EnrollmentType");
                            if (enrollmentType == null || Convert.ToInt32(enrollmentType) != MdmEnrollmentType)
                                continue;

                            using (var firstSyncKey = enrollmentKey.OpenSubKey("FirstSync"))
                            {
                                if (firstSyncKey == null)
                                {
                                    logger?.Debug($"EspSkipConfigurationProbe: FirstSync subkey not found for enrollment {guid}");
                                    return EspFirstSyncSnapshot.Empty;
                                }

                                var rawSkipUser = firstSyncKey.GetValue("SkipUserStatusPage");
                                if (rawSkipUser != null)
                                    skipUser = Convert.ToInt32(rawSkipUser) != 0;

                                var rawSkipDevice = firstSyncKey.GetValue("SkipDeviceStatusPage");
                                if (rawSkipDevice != null)
                                    skipDevice = Convert.ToInt32(rawSkipDevice) != 0;

                                var rawBlock = firstSyncKey.GetValue("BlockInStatusPage");
                                if (rawBlock != null)
                                    blockInStatusPage = Convert.ToInt32(rawBlock);

                                var rawTimeout = firstSyncKey.GetValue("SyncFailureTimeout");
                                if (rawTimeout != null)
                                    syncFailureTimeout = Convert.ToInt32(rawTimeout);
                            }

                            break;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Debug($"EspSkipConfigurationProbe: ReadFull threw: {ex.Message}");
            }

            return new EspFirstSyncSnapshot(skipUser, skipDevice, blockInStatusPage, syncFailureTimeout);
        }

        /// <summary>
        /// Disposable scope that sets <see cref="TestOverride"/> for the lifetime of the
        /// scope and restores the previous value on <see cref="IDisposable.Dispose"/>. Nestable.
        /// Internal test-only helper; not exposed to production callers.
        /// </summary>
        internal sealed class ScopedOverride : IDisposable
        {
            private readonly Func<AgentLogger, (bool? skipUser, bool? skipDevice)> _previous;
            private int _disposed;

            public ScopedOverride(Func<AgentLogger, (bool? skipUser, bool? skipDevice)> probe)
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

        /// <summary>
        /// Disposable scope that sets <see cref="FullTestOverride"/> for the lifetime of the
        /// scope and restores the previous value on <see cref="IDisposable.Dispose"/>. Nestable.
        /// Internal test-only helper.
        /// </summary>
        internal sealed class ScopedFullOverride : IDisposable
        {
            private readonly Func<AgentLogger, EspFirstSyncSnapshot> _previous;
            private int _disposed;

            public ScopedFullOverride(Func<AgentLogger, EspFirstSyncSnapshot> probe)
            {
                _previous = FullTestOverride;
                FullTestOverride = probe;
            }

            public void Dispose()
            {
                if (System.Threading.Interlocked.Exchange(ref _disposed, 1) == 1) return;
                FullTestOverride = _previous;
            }
        }
    }

    /// <summary>
    /// Snapshot of the relevant values under
    /// <c>HKLM\SOFTWARE\Microsoft\Enrollments\{guid}\FirstSync</c> for the MDM enrollment.
    /// Each field is <c>null</c> when the corresponding registry value is missing or unreadable.
    /// </summary>
    internal readonly struct EspFirstSyncSnapshot
    {
        public static readonly EspFirstSyncSnapshot Empty = new EspFirstSyncSnapshot(null, null, null, null);

        public EspFirstSyncSnapshot(bool? skipUser, bool? skipDevice, int? blockInStatusPage, int? syncFailureTimeoutMinutes)
        {
            SkipUser = skipUser;
            SkipDevice = skipDevice;
            BlockInStatusPage = blockInStatusPage;
            SyncFailureTimeoutMinutes = syncFailureTimeoutMinutes;
        }

        /// <summary><c>SkipUserStatusPage</c>: <c>true</c> = User-ESP is skipped, <c>false</c> = shown, <c>null</c> = unknown.</summary>
        public bool? SkipUser { get; }

        /// <summary><c>SkipDeviceStatusPage</c>: <c>true</c> = Device-ESP is skipped, <c>false</c> = shown, <c>null</c> = unknown.</summary>
        public bool? SkipDevice { get; }

        /// <summary>
        /// <c>BlockInStatusPage</c> raw bitmask. See <see cref="EspSkipConfigurationProbe"/>'s
        /// <c>BlockInStatusPage*Bit</c> constants for the bit semantics. <c>null</c> = unknown.
        /// </summary>
        public int? BlockInStatusPage { get; }

        /// <summary>
        /// <c>SyncFailureTimeout</c> in minutes — Intune ESP setting
        /// "Show error when installation takes longer than" (default 60). <c>null</c> = unknown.
        /// </summary>
        public int? SyncFailureTimeoutMinutes { get; }

        /// <summary>Decoded flag: ESP allows the user to reset the device on error.</summary>
        public bool? AllowReset => BlockInStatusPage.HasValue
            ? (bool?)((BlockInStatusPage.Value & EspSkipConfigurationProbe.BlockInStatusPageAllowResetBit) != 0)
            : null;

        /// <summary>Decoded flag: ESP allows the user to retry installation on error.</summary>
        public bool? AllowTryAgain => BlockInStatusPage.HasValue
            ? (bool?)((BlockInStatusPage.Value & EspSkipConfigurationProbe.BlockInStatusPageAllowTryAgainBit) != 0)
            : null;

        /// <summary>Decoded flag: ESP shows "Continue anyway" — user can bypass ESP on error.</summary>
        public bool? AllowContinueAnyway => BlockInStatusPage.HasValue
            ? (bool?)((BlockInStatusPage.Value & EspSkipConfigurationProbe.BlockInStatusPageAllowContinueAnywayBit) != 0)
            : null;
    }
}
