using System;
using System.Reflection;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Interop
{
    /// <summary>
    /// Reads WinRT <c>Windows.System.Profile.SystemSetupInfo.OutOfBoxExperienceState</c>
    /// (NotStarted=0 / InProgress=1 / Completed=2) via the CLR's built-in WinRT projection —
    /// no NuGet package, no winmd reference; the type is resolved at runtime with
    /// <c>ContentType=WindowsRuntime</c>, the same mechanism the bootstrap script uses from
    /// PowerShell 5.1.
    /// <para>
    /// <b>Contract:</b> observational only — never feed this into decision-engine logic.
    /// Requires Windows 10 1809+ (build 17763, UniversalApiContract 7.0) and is callable
    /// from the SYSTEM service context (empirically validated, probe session 9c404ae9).
    /// On older builds or SKUs without the contract the type resolves to null and
    /// <see cref="Read"/> returns <c>"unavailable"</c>. <see cref="Read"/> never throws.
    /// </para>
    /// <para>
    /// Empirical timing (Win11 24H2 probe): the state flips InProgress→Completed at desktop
    /// arrival — it is a corroboration signal, not an early one. WDP reseal and placeholder
    /// (fooUser) flip semantics are unverified; another reason this must stay observational.
    /// </para>
    /// </summary>
    internal static class OobeStateReader
    {
        public const string Unavailable = "unavailable";

        // Resolved once per process. Benign race: concurrent first calls resolve the same
        // PropertyInfo and the assignment is idempotent — no lock needed.
        private static PropertyInfo _stateProperty;
        private static bool _initTried;

        /// <summary>
        /// Returns <c>"not_started"</c> / <c>"in_progress"</c> / <c>"completed"</c>,
        /// <c>"unknown_&lt;n&gt;"</c> for unexpected enum values, or <c>"unavailable"</c>
        /// when the WinRT contract is absent or any part of the read fails. Never throws.
        /// </summary>
        public static string Read()
        {
            try
            {
                if (!_initTried)
                {
                    var type = Type.GetType(
                        "Windows.System.Profile.SystemSetupInfo, Windows.System.Profile, ContentType=WindowsRuntime",
                        throwOnError: false);
                    _stateProperty = type?.GetProperty(
                        "OutOfBoxExperienceState",
                        BindingFlags.Public | BindingFlags.Static);
                    _initTried = true;
                }

                if (_stateProperty == null)
                    return Unavailable;

                var value = Convert.ToInt32(_stateProperty.GetValue(null));
                switch (value)
                {
                    case 0: return "not_started";
                    case 1: return "in_progress";
                    case 2: return "completed";
                    default: return "unknown_" + value;
                }
            }
            catch
            {
                return Unavailable;
            }
        }
    }
}
