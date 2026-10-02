using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using Microsoft.Win32;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Reads the registry state behind the OOBE quality update (D-310, S4/S5): the NDUP state
    /// Windows keeps while the update page runs, and both policy paths of the ESP setting "Install
    /// Windows quality updates". <see cref="OobeUpdateTelemetry"/> logs it and reports it as
    /// <c>oobe_update_state</c>. Read-only; the values are update-state flags and policy values,
    /// no user data.
    /// </summary>
    internal static class OobeUpdateRegistrySnapshot
    {
        /// <summary>The snapshot's keys, each with the alias it carries in <c>oobe_update_state</c>.</summary>
        internal static readonly (string Alias, string Path)[] Keys =
        {
            ("setupOobeNdup", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\OOBE\NDUP"),
            ("setupOobeNdupUpdates", @"SOFTWARE\Microsoft\Windows\CurrentVersion\Setup\OOBE\NDUP\Updates"),
            ("ndup", @"SOFTWARE\Microsoft\Windows\CurrentVersion\NDUP"),
            ("ndupUpdates", @"SOFTWARE\Microsoft\Windows\CurrentVersion\NDUP\Updates"),
            ("espSetupPolicy", @"SOFTWARE\Microsoft\Windows\Autopilot\EnrollmentStatusTracking\Device\Setup\Policy"),
            ("policyManagerSystem", PolicyManagerSystemKey),
            ("policiesOobe", @"SOFTWARE\Policies\Microsoft\Windows\OOBE"),
        };

        // Carries every System policy of the device; only the OOBE ones belong in this snapshot.
        internal const string PolicyManagerSystemKey = @"SOFTWARE\Microsoft\PolicyManager\current\device\System";

        internal const int MaxEntries = 30;
        internal const int MaxValueLength = 200;

        /// <summary>One key of the snapshot: whether it exists, whether it holds values, and its line.</summary>
        internal sealed class KeyState
        {
            public KeyState(string alias, string path, bool exists, bool hasValues, string line)
            {
                Alias = alias;
                Path = path;
                Exists = exists;
                HasValues = hasValues;
                Line = line;
            }

            public string Alias { get; }
            public string Path { get; }
            public bool Exists { get; }

            /// <summary>
            /// True when the key holds at least one value — for <see cref="PolicyManagerSystemKey"/>
            /// one with OOBE in its name. Subkeys alone are no values.
            /// </summary>
            public bool HasValues { get; }

            /// <summary><see cref="FormatEntries"/> plus the subkey names; null when the key is absent.</summary>
            public string Line { get; }
        }

        /// <summary>Reads every key from the 64-bit view. Throws on a registry failure; the caller is fail-soft.</summary>
        public static IReadOnlyList<KeyState> Read()
        {
            var states = new List<KeyState>(Keys.Length);
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            {
                foreach (var (alias, path) in Keys)
                {
                    using (var key = hklm.OpenSubKey(path))
                    {
                        if (key == null)
                        {
                            states.Add(new KeyState(alias, path, exists: false, hasValues: false, line: null));
                            continue;
                        }

                        var entries = key.GetValueNames()
                            .Select(name => new KeyValuePair<string, object>(name, key.GetValue(name)))
                            .ToList();
                        states.Add(Describe(alias, path, entries, key.GetSubKeyNames()));
                    }
                }
            }
            return states;
        }

        /// <summary>The state of a present key from its values and subkey names.</summary>
        internal static KeyState Describe(string alias, string path, IReadOnlyList<KeyValuePair<string, object>> entries, string[] subKeys)
        {
            var oobeOnly = string.Equals(path, PolicyManagerSystemKey, StringComparison.OrdinalIgnoreCase);
            var line = FormatEntries(entries, oobeOnly);
            if (subKeys != null && subKeys.Length > 0)
                line += $" | subkeys: {string.Join(", ", subKeys.Take(MaxEntries))}";
            return new KeyState(alias, path, exists: true,
                hasValues: entries.Any(e => !oobeOnly || IsOobeName(e.Key)), line: line);
        }

        /// <summary>One agent.log line per present key, one naming the absent keys.</summary>
        public static void Log(AgentLogger logger, string moment, IReadOnlyList<KeyState> states)
        {
            if (logger == null || states == null) return;

            var absent = new List<string>();
            foreach (var state in states)
            {
                if (!state.Exists)
                {
                    absent.Add(state.Path);
                    continue;
                }
                logger.Info($"OOBE update registry ({moment}): HKLM\\{state.Path}: {state.Line}");
            }

            if (absent.Count > 0)
                logger.Info($"OOBE update registry ({moment}): absent: {string.Join(", ", absent)}");
        }

        /// <summary>
        /// <c>name=value</c> pairs ordered by name, bounded in count and length.
        /// <paramref name="oobeOnly"/> keeps only names that mention OOBE.
        /// </summary>
        internal static string FormatEntries(IEnumerable<KeyValuePair<string, object>> entries, bool oobeOnly)
        {
            var selected = entries
                .Where(e => !oobeOnly || IsOobeName(e.Key))
                .OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (selected.Count == 0) return oobeOnly ? "(no OOBE values)" : "(no values)";

            var shown = selected.Take(MaxEntries)
                .Select(e => $"{(e.Key.Length == 0 ? "(default)" : e.Key)}={FormatValue(e.Value)}");
            var line = string.Join(", ", shown);
            return selected.Count > MaxEntries ? $"{line}, … (+{selected.Count - MaxEntries} more)" : line;
        }

        internal static string FormatValue(object value)
        {
            string text;
            switch (value)
            {
                case null: text = "(null)"; break;
                case int i: text = i.ToString(CultureInfo.InvariantCulture); break;
                case long l: text = l.ToString(CultureInfo.InvariantCulture); break;
                case string s: text = s; break;
                case string[] multi: text = string.Join("|", multi); break;
                case byte[] bytes: text = $"(binary {bytes.Length} bytes)"; break;
                default: text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty; break;
            }
            return text.Length > MaxValueLength ? text.Substring(0, MaxValueLength) + "…" : text;
        }

        private static bool IsOobeName(string name) =>
            name != null && name.IndexOf("OOBE", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
