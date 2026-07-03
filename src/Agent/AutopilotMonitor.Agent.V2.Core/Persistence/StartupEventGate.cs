#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AutopilotMonitor.Agent.V2.Core.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Agent.V2.Core.Persistence
{
    /// <summary>
    /// Cross-cutting restart dedup for the agent's one-shot startup checks. An enrollment session
    /// commonly spans several reboots; every reboot restarts the agent process, which re-runs the
    /// startup probes (geo / timezone / NTP), the DeviceInfoCollector sweep and the startup
    /// analyzers — each re-emitting events that carry no new information (~20 duplicate events per
    /// restart, multiplied across multi-reboot sessions).
    /// <para>
    /// The gate persists per-key state in <c>startup-event-state.json</c> (state directory,
    /// per-enrollment lifecycle) and offers two policies:
    /// <list type="bullet">
    ///   <item><b>Emit-on-change</b> — <see cref="ShouldEmit"/>: the event goes out when its payload
    ///     fingerprint differs from the last emitted one (or none was recorded). Identical repeats
    ///     are suppressed; a REAL change (e.g. <c>aad_join_status</c> flipping to Joined after the
    ///     Hybrid-Join reboot) always re-emits. This is why the gate must only ever wrap the EVENT
    ///     emission — collection, decision signals and derived facts must keep running unchanged.</item>
    ///   <item><b>Retry-until-success</b> — <see cref="AlreadySucceeded"/>/<see cref="MarkSucceeded"/>:
    ///     for probes whose re-run is only valuable while they keep failing (geo location, timezone
    ///     auto-set, NTP offset). Success latches across restarts; failure leaves the key unset so
    ///     the next agent run retries.</item>
    /// </list>
    /// </para>
    /// Fail-soft: a missing/corrupt state file loads as empty (everything emits, like today) and
    /// save errors never throw. Thread-safe — startup emissions happen on several background tasks.
    /// </summary>
    public sealed class StartupEventGate
    {
        private readonly string _stateDirectory;
        private readonly string _stateFilePath;
        private readonly AgentLogger _logger;
        private readonly object _lock = new object();

        // M4 (delta review 2026-07-02): two views. `_entries` is the in-process truth that
        // ShouldEmit claims against (immediate, so concurrent/repeated in-process checks stay
        // deduped). `_persisted` is what actually reaches disk — a key is copied over only by
        // MarkEmitted/MarkSucceeded, i.e. AFTER the caller's emission went through. Persisting
        // inside ShouldEmit meant a process death between the fingerprint save and the spool
        // append suppressed that event for the rest of the enrollment (fail-closed on telemetry,
        // permanent for static payloads like tpm_info or console_prefetch_detected).
        private readonly Dictionary<string, GateEntry> _entries;
        private readonly Dictionary<string, GateEntry> _persisted;

        public StartupEventGate(string stateDirectory, AgentLogger logger)
        {
            _stateDirectory = Environment.ExpandEnvironmentVariables(stateDirectory);
            _stateFilePath = Path.Combine(_stateDirectory, "startup-event-state.json");
            _logger = logger;
            _persisted = LoadSafe();
            _entries = new Dictionary<string, GateEntry>(_persisted, StringComparer.Ordinal);
        }

        public string StateFilePath => _stateFilePath;

        /// <summary>
        /// Emit-on-change: true when no fingerprint is recorded for <paramref name="key"/> or the
        /// recorded one differs. The new fingerprint is claimed IN MEMORY only — the caller must
        /// invoke <see cref="MarkEmitted"/> after the emission actually went out, which is what
        /// persists the claim across restarts. A crash (or an emit failure) between the two calls
        /// therefore re-emits on the next run instead of silently suppressing forever.
        /// False means "identical payload already emitted in this session" (possibly by a
        /// previous agent run).
        /// </summary>
        public bool ShouldEmit(string key, string fingerprint)
        {
            if (string.IsNullOrEmpty(key)) return true;
            lock (_lock)
            {
                if (_entries.TryGetValue(key, out var existing)
                    && string.Equals(existing.Fingerprint, fingerprint, StringComparison.Ordinal))
                {
                    return false;
                }

                _entries[key] = new GateEntry
                {
                    Fingerprint = fingerprint,
                    Succeeded = existing?.Succeeded ?? false,
                    UpdatedUtc = DateTime.UtcNow,
                };
                return true;
            }
        }

        /// <summary>
        /// Commits the in-memory claim made by the preceding <see cref="ShouldEmit"/> to disk.
        /// Call after the event emission succeeded. No-op when the key was never claimed
        /// (e.g. gate-exempt event types).
        /// </summary>
        public void MarkEmitted(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_lock)
            {
                if (!_entries.TryGetValue(key, out var entry)) return;
                _persisted[key] = entry;
                SaveSafe();
            }
        }

        /// <summary>
        /// Read-only peek: true when the key's current fingerprint (possibly restored from a
        /// previous agent run) equals <paramref name="fingerprint"/>. Unlike
        /// <see cref="ShouldEmit"/> this makes NO claim — use it to seed in-memory latches
        /// (e.g. the disk_space_low hysteresis) from persisted state at construction time.
        /// </summary>
        public bool HasFingerprint(string key, string fingerprint)
        {
            if (string.IsNullOrEmpty(key)) return false;
            lock (_lock)
            {
                return _entries.TryGetValue(key, out var entry)
                    && string.Equals(entry.Fingerprint, fingerprint, StringComparison.Ordinal);
            }
        }

        /// <summary>Retry-until-success: true when <see cref="MarkSucceeded"/> latched this key in any run.</summary>
        public bool AlreadySucceeded(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            lock (_lock)
            {
                return _entries.TryGetValue(key, out var entry) && entry.Succeeded;
            }
        }

        /// <summary>
        /// Latches a retry-until-success key; subsequent runs skip the probe entirely.
        /// Persists immediately — success latches happen AFTER the probe's emission by contract
        /// (see StartupEnvironmentProbes), so there is no suppression window here.
        /// </summary>
        public void MarkSucceeded(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (_lock)
            {
                _entries.TryGetValue(key, out var existing);
                var entry = new GateEntry
                {
                    Fingerprint = existing?.Fingerprint,
                    Succeeded = true,
                    UpdatedUtc = DateTime.UtcNow,
                };
                _entries[key] = entry;
                _persisted[key] = entry;
                SaveSafe();
            }
        }

        /// <summary>
        /// Stable payload fingerprint: top-level keys sorted ordinally (nested structures are
        /// serialized as built — their order is deterministic per code path), optional volatile
        /// top-level fields excluded (e.g. a negotiated WiFi link speed that varies per
        /// association without the adapter actually changing), SHA256 over the JSON.
        /// </summary>
        public static string ComputeFingerprint(IReadOnlyDictionary<string, object> data, string[]? excludedKeys = null)
        {
            if (data == null) return "empty";

            var stable = new SortedDictionary<string, object>(StringComparer.Ordinal);
            foreach (var kv in data)
            {
                if (excludedKeys != null && excludedKeys.Contains(kv.Key, StringComparer.OrdinalIgnoreCase)) continue;
                stable[kv.Key] = kv.Value;
            }

            var json = JsonConvert.SerializeObject(stable, Formatting.None);
            return HashString(json);
        }

        /// <summary>SHA256 hex (first 32 chars — ample for change detection) over an arbitrary string.</summary>
        public static string HashString(string value)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty));
                var sb = new StringBuilder(32);
                for (var i = 0; i < hash.Length && sb.Length < 32; i++)
                    sb.Append(hash[i].ToString("x2"));
                return sb.ToString();
            }
        }

        // -----------------------------------------------------------------------
        // Persistence (fail-soft, atomic write — same pattern as the other state files)
        // -----------------------------------------------------------------------

        private Dictionary<string, GateEntry> LoadSafe()
        {
            try
            {
                if (!File.Exists(_stateFilePath))
                    return new Dictionary<string, GateEntry>(StringComparer.Ordinal);

                var json = File.ReadAllText(_stateFilePath);
                var loaded = JsonConvert.DeserializeObject<Dictionary<string, GateEntry>>(json);
                if (loaded == null) return new Dictionary<string, GateEntry>(StringComparer.Ordinal);
                return new Dictionary<string, GateEntry>(loaded, StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                _logger.Warning($"StartupEventGate: failed to load state, starting fresh (everything emits): {ex.Message}");
                return new Dictionary<string, GateEntry>(StringComparer.Ordinal);
            }
        }

        private void SaveSafe()
        {
            try
            {
                Directory.CreateDirectory(_stateDirectory);
                var tempPath = _stateFilePath + ".tmp";
                // Persist the committed view only — uncommitted ShouldEmit claims of OTHER keys
                // must not ride along, or their emissions could be suppressed by a crash too.
                File.WriteAllText(tempPath, JsonConvert.SerializeObject(_persisted));
                if (File.Exists(_stateFilePath))
                {
                    File.Replace(tempPath, _stateFilePath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, _stateFilePath);
                }
            }
            catch (Exception ex)
            {
                _logger.Debug($"StartupEventGate: failed to save state: {ex.Message}");
            }
        }

        private sealed class GateEntry
        {
            public string? Fingerprint { get; set; }
            public bool Succeeded { get; set; }
            public DateTime UpdatedUtc { get; set; }
        }
    }
}
