using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// One-shot read of the Autopilot ESP phase from the values <see cref="ProvisioningStatusTracker"/>
    /// watches (<c>HKLM\SOFTWARE\Microsoft\Provisioning\AutopilotSettings</c>), for reports taken
    /// without the tracker — a failed registration has no session and no tracker yet.
    /// <para>
    /// <b>Contract:</b> observational only, never decision-engine input. Unlike the tracker it has
    /// no settle or fallback timers: a category whose <c>categorySucceeded</c> Windows never sets
    /// counts as done once all its subcategories read succeeded or notRequired.
    /// </para>
    /// </summary>
    internal static class EspPhaseSnapshot
    {
        internal const string None = "none";
        internal const string Unavailable = "unavailable";
        internal const string DevicePreparation = "device_preparation";
        private const string FailedSuffix = "_failed";

        internal enum CategoryState { Absent, Running, Done, Failed }

        // ESP order: device preparation, device setup, account setup.
        private static readonly (string ValueName, string Phase)[] Categories =
        {
            ("DevicePreparationCategory.Status", DevicePreparation),
            ("DeviceSetupCategory.Status", "device_setup"),
            ("AccountSetupCategory.Status", "account_setup"),
        };

        /// <summary>
        /// <c>device_preparation</c> / <c>device_setup</c> / <c>account_setup</c> while that
        /// category runs, <c>&lt;category&gt;_failed</c>, <c>device_preparation_done</c> /
        /// <c>device_setup_done</c> when the next category has not appeared (yet, or skipped),
        /// <c>esp_done</c>, <c>none</c> without any ESP value, <c>unavailable</c> when the
        /// registry cannot be read. Never throws.
        /// </summary>
        public static string Read()
        {
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(ProvisioningStatusTracker.ProvisioningStatusRegistryPath, writable: false))
                {
                    if (key == null) return None;
                    return Derive(Categories.Select(c => key.GetValue(c.ValueName) as string).ToList());
                }
            }
            catch
            {
                return Unavailable;
            }
        }

        /// <param name="statusJson">The three category values in ESP order; null where absent.</param>
        internal static string Derive(IReadOnlyList<string> statusJson)
        {
            var states = Categories.Select((_, i) => Classify(i < statusJson.Count ? statusJson[i] : null)).ToList();

            for (var i = 0; i < Categories.Length; i++)
                if (states[i] == CategoryState.Failed) return Categories[i].Phase + FailedSuffix;
            for (var i = 0; i < Categories.Length; i++)
                if (states[i] == CategoryState.Running) return Categories[i].Phase;

            var lastDone = states.FindLastIndex(s => s == CategoryState.Done);
            if (lastDone < 0) return None;
            return lastDone == Categories.Length - 1 ? "esp_done" : Categories[lastDone].Phase + "_done";
        }

        /// <summary>
        /// Whether device preparation can still hold the ESP in a <see cref="Read"/> result: it
        /// runs or failed, or the phase is unknown (<see cref="None"/>, <see cref="Unavailable"/>).
        /// False once device preparation is done, whichever later phase the ESP is in.
        /// </summary>
        internal static bool DevicePreparationOpen(string phase) =>
            string.IsNullOrEmpty(phase)
            || phase == None
            || phase == Unavailable
            || phase == DevicePreparation
            || phase == DevicePreparation + FailedSuffix;

        /// <summary>
        /// One category value: done (<c>categorySucceeded=true</c>, or every subcategory succeeded
        /// or notRequired), failed (<c>false</c>, or a failed subcategory), running (anything
        /// else that is present, also unparseable JSON), absent (no value).
        /// </summary>
        internal static CategoryState Classify(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return CategoryState.Absent;
            try
            {
                using (var doc = JsonDocument.Parse(json))
                {
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) return CategoryState.Running;

                    var succeeded = ProvisioningStatusTracker.SafeGetBool(root, "categorySucceeded");
                    if (succeeded == true) return CategoryState.Done;
                    if (succeeded == false) return CategoryState.Failed;

                    var subcategories = ProvisioningStatusTracker.ParseSubcategories(root);
                    if (subcategories.Any(s => string.Equals(s.State, "failed", StringComparison.OrdinalIgnoreCase)))
                        return CategoryState.Failed;
                    if (subcategories.Count > 0 && subcategories.All(s =>
                            string.Equals(s.State, "succeeded", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(s.State, "notRequired", StringComparison.OrdinalIgnoreCase)))
                        return CategoryState.Done;
                    return CategoryState.Running;
                }
            }
            catch (JsonException)
            {
                return CategoryState.Running;
            }
        }
    }
}
