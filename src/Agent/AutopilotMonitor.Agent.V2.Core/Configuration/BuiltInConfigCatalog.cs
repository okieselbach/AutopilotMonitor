using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2.Core.Configuration
{
    /// <summary>
    /// Compiled-in fallback copies of the global rule catalogs, embedded from the same
    /// CI-generated <c>rules/dist</c> artifacts the backend seeds and serves — single
    /// source by construction: each agent release snapshots the catalog state of its own
    /// build, so the offline fallback can never drift further than the running agent
    /// version itself (no manually maintained default to forget).
    /// <para>
    /// Consumed only by <see cref="RemoteConfigService"/>'s built-in default config,
    /// i.e. when a session has NO live config and NO cache. Before this existed, that
    /// path shipped an empty <c>ImeLogPatterns</c> list, which left the entire IME
    /// tracking pipeline (app installs, platform/remediation scripts, health-script
    /// compliance, IME ESP-phase signals) dead for the whole session — the core of
    /// enrollment monitoring. A live fetch (startup, post-registration recovery or
    /// rotate_config) always wins over this catalog.
    /// </para>
    /// Fail-soft: any load/parse error returns an empty list (the pre-catalog behaviour).
    /// </summary>
    internal static class BuiltInConfigCatalog
    {
        /// <summary>Enabled built-in IME log patterns from the embedded catalog.</summary>
        public static List<ImeLogPattern> GetEnabledImeLogPatterns(AgentLogger logger)
            => LoadEnabled<ImeLogPattern>("ime-log-patterns.json", p => p.Enabled, logger);

        /// <summary>Enabled built-in gather rules from the embedded catalog.</summary>
        public static List<GatherRule> GetEnabledGatherRules(AgentLogger logger)
            => LoadEnabled<GatherRule>("gather-rules.json", r => r.Enabled, logger);

        private static List<T> LoadEnabled<T>(string name, Func<T, bool> isEnabled, AgentLogger logger)
        {
            try
            {
                var assembly = typeof(BuiltInConfigCatalog).Assembly;
                using (var stream = assembly.GetManifestResourceStream(
                    "AutopilotMonitor.Agent.V2.Core.Resources." + name))
                {
                    if (stream == null)
                    {
                        logger?.Warning($"Built-in config catalog resource '{name}' not found — fallback runs without it.");
                        return new List<T>();
                    }

                    using (var reader = new StreamReader(stream))
                    {
                        var wrapper = JsonConvert.DeserializeObject<RulesFile<T>>(reader.ReadToEnd());
                        var all = wrapper?.Rules ?? new List<T>();
                        return all.Where(isEnabled).ToList();
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.Warning($"Built-in config catalog '{name}' failed to load ({ex.Message}) — fallback runs without it.");
                return new List<T>();
            }
        }

        /// <summary>Shape of the CI-combined catalog files: <c>{ "rules": [...] }</c>.</summary>
        private sealed class RulesFile<T>
        {
            [JsonProperty("rules")]
            public List<T> Rules { get; set; }
        }
    }
}
