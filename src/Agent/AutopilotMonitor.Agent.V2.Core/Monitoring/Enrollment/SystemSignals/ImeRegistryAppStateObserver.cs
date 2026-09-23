#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared.Models;
using Microsoft.Win32;
using SharedEventTypes = AutopilotMonitor.Shared.Constants.EventTypes;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Registry "second pillar" for IME app state (audit 2026-08-17, architecture decision:
    /// registry = stable truth, IME logs = narrative). IME persists its authoritative per-app
    /// state under <c>HKLM\SOFTWARE\Microsoft\IntuneManagementExtension</c>:
    /// <list type="bullet">
    /// <item><c>Win32Apps\&lt;userGuid&gt;\&lt;appGuid&gt;_&lt;rev&gt;</c> — EnforcementStateMessage JSON
    /// (EnforcementState + ErrorCode), ExitCode, Intent. Device context = Guid.Empty user.</item>
    /// <item><c>EspTrackingWin32Apps\&lt;userGuid&gt;\&lt;appGuid&gt;_&lt;rev&gt;</c> — which apps IME
    /// registered for ESP tracking and in which phase.</item>
    /// <item><c>SideCarPolicies\StatusServiceReports\&lt;userGuid&gt;\&lt;appId&gt;</c> — the exact
    /// AppInstallStatusReport the ESP page renders (Status 1000=Installed, 1001=Installing,
    /// 1003=InstallingPendingReboot, 2000=NotApplicable, 3000=Failed).</item>
    /// <item><c>Win32Apps\OperationalState\&lt;userGuid&gt;\&lt;appGuid&gt;</c> (bare GUIDs) — IME's
    /// per-app operational state: the install deadline a downloaded app is waiting for
    /// (<c>ExecutionDeadlineTime</c>, IME 1.106+) and the app-in-use deferral counters
    /// (<c>DeferralsUsed</c>, <c>DeferUntilTime</c>, ..., IME 1.105+).</item>
    /// </list>
    /// The observer is snapshot-and-diff driven (RegistryWatcher gives key-scope edges only,
    /// coalesced — a per-write parse is impossible by design): every tick re-reads the four
    /// surfaces and emits <c>registry_app_state</c> on real field changes. Pre-existing state at
    /// agent start is the silent baseline — Win32Apps keys survive re-enrollments, so replaying
    /// them as fresh events would be the registry twin of the historic-IME-replay bug.
    ///
    /// Reconciliation (= built-in log-pattern drift alarm): once a registry entry that changed
    /// during THIS session has been terminal for <see cref="ReconcileSettleDelay"/>, its outcome
    /// is compared against the log-derived <see cref="AppPackageState"/>. A divergence means the
    /// IME log patterns missed or mis-classified the app — emitted once per app as
    /// <c>app_state_reconciliation</c> (Warning). Pure observability: nothing here feeds the
    /// DecisionEngine.
    /// </summary>
    internal sealed class ImeRegistryAppStateObserver
    {
        public const string SourceLabel = "RegistryAppState";

        internal const string ImeRootKeyPath = @"SOFTWARE\Microsoft\IntuneManagementExtension";

        // Safety cap: a pathological churn (or a wrong diff) must not flood the timeline.
        // 200 covers ~25 apps x 8 state transitions; the cap itself is announced once.
        internal const int MaxStateEventsPerSession = 200;

        // The log tracker legitimately lags the registry (log write + 100 ms poll + phase
        // gating), so divergence is only evaluated after the registry state has been stable
        // terminal for this long.
        internal static readonly TimeSpan ReconcileSettleDelay = TimeSpan.FromSeconds(90);

        private readonly InformationalEventPost _post;
        private readonly AgentLogger? _logger;
        private readonly IClock _clock;
        private readonly Func<IReadOnlyList<AppPackageState>>? _trackerStateProbe;
        private readonly object _sync = new object();

        private ImeRegistrySnapshot? _last;
        private readonly Dictionary<string, DateTime> _terminalSinceUtc =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _changedDuringSession =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _reconciliationEmitted =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private int _stateEventsEmitted;
        private bool _capNoticeEmitted;

        // Test seam: replaces the live registry walk (same convention as EspTrackingInfoProbe).
        internal static Func<ImeRegistrySnapshot>? SnapshotOverride;

        internal sealed class ScopedSnapshotOverride : IDisposable
        {
            private readonly Func<ImeRegistrySnapshot>? _previous;
            private int _disposed;

            public ScopedSnapshotOverride(Func<ImeRegistrySnapshot> replacement)
            {
                _previous = SnapshotOverride;
                SnapshotOverride = replacement;
            }

            public void Dispose()
            {
                if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
                SnapshotOverride = _previous;
            }
        }

        public ImeRegistryAppStateObserver(
            InformationalEventPost post,
            AgentLogger? logger,
            IClock clock,
            Func<IReadOnlyList<AppPackageState>>? trackerStateProbe)
        {
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _logger = logger;
            _trackerStateProbe = trackerStateProbe;
        }

        /// <summary>
        /// One observation pass: snapshot → diff → emit → reconcile. Called from the debounced
        /// registry-change edge AND a periodic fallback tick (the settle-delay evaluation must
        /// happen even when the registry goes quiet). Fail-soft: never throws.
        /// </summary>
        public void Tick(string reason)
        {
            try
            {
                lock (_sync)
                {
                    var snapshot = SnapshotOverride?.Invoke() ?? ReadSnapshot();
                    var nowUtc = _clock.UtcNow;

                    if (_last == null)
                    {
                        // Silent baseline for the DIFF stream — see class doc. But the baseline
                        // itself is valuable evidence: on WDP the DPP-policy apps install in
                        // Batch 1 BEFORE the agent (bootstrap = Batch 2), so their terminal
                        // states exist ONLY here. One capped summary event closes that
                        // pre-agent gap without any log backfill (Oliver, 2026-08-18).
                        _last = snapshot;
                        _logger?.Info($"RegistryAppState: baseline captured ({snapshot.Entries.Count} app entries, trigger={reason})");
                        if (snapshot.Entries.Count > 0)
                            EmitBaselineSummary(snapshot, nowUtc);
                        return;
                    }

                    var changes = DiffSnapshots(_last, snapshot);
                    foreach (var change in changes)
                    {
                        _changedDuringSession.Add(change.Entry.Key);
                        EmitStateEvent(change, nowUtc);
                    }

                    TrackTerminalTransitions(snapshot, nowUtc);
                    EvaluateReconciliations(snapshot, nowUtc);

                    _last = snapshot;
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug($"RegistryAppState: tick failed (fail-soft): {ex.Message}");
            }
        }

        // ── snapshot ────────────────────────────────────────────────────────────

        internal static ImeRegistrySnapshot ReadSnapshot()
        {
            var snapshot = new ImeRegistrySnapshot();
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var root = baseKey.OpenSubKey(ImeRootKeyPath);
            if (root == null) return snapshot;

            ReadWin32Apps(root, snapshot);
            ReadEspTracking(root, snapshot);
            ReadStatusServiceReports(root, snapshot);
            ReadOperationalState(root, snapshot);
            return snapshot;
        }

        private static void ReadWin32Apps(RegistryKey root, ImeRegistrySnapshot snapshot)
        {
            using var win32Apps = root.OpenSubKey("Win32Apps");
            if (win32Apps == null) return;

            foreach (var userName in win32Apps.GetSubKeyNames())
            {
                if (!LooksLikeGuid(userName)) continue; // skips OperationalState / Reporting / ProvisioningProgress
                using var userKey = win32Apps.OpenSubKey(userName);
                if (userKey == null) continue;

                foreach (var appKeyName in userKey.GetSubKeyNames())
                {
                    var appId = ExtractAppId(appKeyName);
                    if (appId == null) continue; // skips GRS + non-app keys

                    using var appKey = userKey.OpenSubKey(appKeyName);
                    if (appKey == null) continue;

                    var entry = snapshot.GetOrAdd(userName, appId);
                    entry.ExitCode = TryReadInt(appKey.GetValue("ExitCode"));
                    entry.Intent = TryReadInt(appKey.GetValue("Intent"));
                    entry.LastUpdatedUtc = appKey.GetValue("LastUpdatedTimeUtc") as string;

                    using var esm = appKey.OpenSubKey("EnforcementStateMessage");
                    var json = esm?.GetValue("EnforcementStateMessage") as string;
                    if (!string.IsNullOrEmpty(json))
                    {
                        var (state, error) = ParseEnforcementStateMessage(json!);
                        entry.EnforcementState = state;
                        entry.ErrorCode = error;
                    }
                }
            }
        }

        private static void ReadEspTracking(RegistryKey root, ImeRegistrySnapshot snapshot)
        {
            using var esp = root.OpenSubKey("EspTrackingWin32Apps");
            if (esp == null) return;

            foreach (var userName in esp.GetSubKeyNames())
            {
                if (!LooksLikeGuid(userName)) continue;
                using var userKey = esp.OpenSubKey(userName);
                if (userKey == null) continue;

                foreach (var appKeyName in userKey.GetSubKeyNames())
                {
                    var appId = ExtractAppId(appKeyName);
                    if (appId == null) continue;

                    using var appKey = userKey.OpenSubKey(appKeyName);
                    var entry = snapshot.GetOrAdd(userName, appId);
                    entry.EspTracked = true;
                    entry.EspPhase = appKey?.GetValue("EspTrackingWin32AppPhase") as string;
                }
            }
        }

        private static void ReadStatusServiceReports(RegistryKey root, ImeRegistrySnapshot snapshot)
        {
            using var reports = root.OpenSubKey(@"SideCarPolicies\StatusServiceReports");
            if (reports == null) return;

            foreach (var userName in reports.GetSubKeyNames())
            {
                if (!LooksLikeGuid(userName)) continue;
                using var userKey = reports.OpenSubKey(userName);
                if (userKey == null) continue;

                foreach (var appKeyName in userKey.GetSubKeyNames())
                {
                    var appId = ExtractAppId(appKeyName);
                    if (appId == null) continue;

                    using var appKey = userKey.OpenSubKey(appKeyName);
                    if (appKey == null) continue;

                    var entry = snapshot.GetOrAdd(userName, appId);
                    entry.StatusServiceStatus = TryReadInt(appKey.GetValue("Status"));
                }
            }
        }

        // Only the two wait states are read; the download/execution bookkeeping next to them
        // (DownloadStatus, DownloadStartTime, ExecutionStatus, ...) duplicates the log narrative
        // and would double the event volume without adding a state the timeline lacks.
        private static void ReadOperationalState(RegistryKey root, ImeRegistrySnapshot snapshot)
        {
            using var operational = root.OpenSubKey(@"Win32Apps\OperationalState");
            if (operational == null) return;

            foreach (var userName in operational.GetSubKeyNames())
            {
                if (!LooksLikeGuid(userName)) continue;
                using var userKey = operational.OpenSubKey(userName);
                if (userKey == null) continue;

                foreach (var appKeyName in userKey.GetSubKeyNames())
                {
                    var appId = ExtractAppId(appKeyName);
                    if (appId == null) continue;

                    using var appKey = userKey.OpenSubKey(appKeyName);
                    if (appKey == null) continue;

                    var deadline = TryParseRegistryDateTime(appKey.GetValue("ExecutionDeadlineTime"));
                    var deferralsUsed = TryReadInt(appKey.GetValue("DeferralsUsed"));
                    if (deadline == null && deferralsUsed == null) continue; // no wait state — no entry of its own

                    var entry = snapshot.GetOrAdd(userName, appId);
                    entry.InstallDeadlineUtc = deadline;
                    entry.DeferralsUsed = deferralsUsed;
                    entry.DeferralMaxDeferrals = TryReadInt(appKey.GetValue("DeferralMaxDeferrals"));
                    entry.DeferUntilUtc = TryParseRegistryDateTime(appKey.GetValue("DeferUntilTime"));
                    entry.DeferralAutoDeferred = TryReadBool(appKey.GetValue("DeferralAutoDeferred"));
                }
            }
        }

        // ── pure helpers (unit-tested directly) ─────────────────────────────────

        internal static bool LooksLikeGuid(string name) =>
            !string.IsNullOrEmpty(name) && Guid.TryParse(name, out _);

        /// <summary>
        /// Win32Apps app keys are <c>&lt;appGuid&gt;_&lt;revision&gt;</c>; StatusServiceReports keys
        /// are the bare GUID. Returns the lowercase app GUID or null for non-app keys (GRS, ...).
        /// </summary>
        internal static string? ExtractAppId(string keyName)
        {
            if (string.IsNullOrEmpty(keyName)) return null;
            var candidate = keyName.Length >= 36 ? keyName.Substring(0, 36) : keyName;
            return Guid.TryParse(candidate, out var g) ? g.ToString("D") : null;
        }

        internal static (int? state, long? errorCode) ParseEnforcementStateMessage(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                int? state = null;
                long? error = null;
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                {
                    if (doc.RootElement.TryGetProperty("EnforcementState", out var s) && s.ValueKind == JsonValueKind.Number)
                        state = s.GetInt32();
                    if (doc.RootElement.TryGetProperty("ErrorCode", out var e) && e.ValueKind == JsonValueKind.Number)
                        error = e.GetInt64();
                }
                return (state, error);
            }
            catch
            {
                return (null, null);
            }
        }

        internal static int? TryReadInt(object? value)
        {
            switch (value)
            {
                case null: return null;
                case int i: return i;
                case long l: return unchecked((int)l);
                case string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed):
                    return parsed;
                default: return null;
            }
        }

        internal static bool? TryReadBool(object? value) =>
            value is string s && bool.TryParse(s, out var parsed) ? parsed : (bool?)null;

        /// <summary>
        /// OperationalState timestamps are UTC but stored in two InvariantCulture shapes:
        /// <c>ExecutionDeadlineTime</c> as <c>MM/dd/yyyy HH:mm:ss</c> without a kind marker,
        /// <c>DeferUntilTime</c> round-trip ("o", trailing Z). Both come back as UTC here.
        /// </summary>
        internal static DateTime? TryParseRegistryDateTime(object? value)
        {
            if (!(value is string s) || string.IsNullOrWhiteSpace(s)) return null;
            return DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
                ? parsed
                : (DateTime?)null;
        }

        /// <summary>StateMessageEnforcementState bands (verified against decompiled IME 1.97/1.104).</summary>
        internal static string ClassifyEnforcementState(int state)
        {
            if (state >= 1000 && state < 2000) return "success";
            if (state >= 2000 && state < 3000) return "inProgress";
            if (state >= 3000 && state < 4000) return "requirementsNotMet";
            if (state >= 5000 && state < 6000) return "error";
            if (state >= 6000 && state < 7000) return "notAttempted";
            return "unknown";
        }

        /// <summary>
        /// AppInstallStatus as IME writes it to StatusServiceReports (unchanged IME 1.50 .. 1.106):
        /// 1000 Installed, 1001 Installing, 1002 InstalledButDependenciesNotPresent,
        /// 1003 InstallingPendingReboot, 1004 InstalledPendingReboot, 2000 NotApplicable, 3000 Failed.
        /// The 1000 band is NOT uniformly terminal — IME maps every InProgress enforcement state to
        /// 1001 — so the mapping is explicit and an unknown value is not judged.
        /// </summary>
        internal static string? ClassifyStatusServiceStatus(int status)
        {
            switch (status)
            {
                case 1000:
                case 1002:
                case 1004:
                    return "installed";
                case 1001:
                case 1003:
                    return "installing";
                case 3000:
                    return "failed";
            }
            if (status >= 2000 && status < 3000) return "notApplicable";
            return null;
        }

        /// <summary>
        /// Human-readable suffix for the two IME wait states (install deadline, app-in-use
        /// deferral); empty when neither applies. Culture-invariant on purpose — the agent runs
        /// under whatever culture the SYSTEM account carries.
        /// </summary>
        internal static string DescribeWaitState(AppRegistryEntry entry)
        {
            var parts = new List<string>(2);
            if (entry.InstallDeadlineUtc is DateTime deadline)
                parts.Add("downloaded, install deadline " + FormatUtcMinute(deadline));

            if (entry.DeferralsUsed is int used && used > 0)
            {
                var kind = entry.DeferralAutoDeferred == true ? "auto-deferred" : "deferred by user";
                var count = entry.DeferralMaxDeferrals is int max
                    ? used.ToString(CultureInfo.InvariantCulture) + "/" + max.ToString(CultureInfo.InvariantCulture)
                    : used.ToString(CultureInfo.InvariantCulture);
                parts.Add(entry.DeferUntilUtc is DateTime until
                    ? kind + " " + count + " until " + FormatUtcMinute(until)
                    : kind + " " + count);
            }

            return parts.Count == 0 ? string.Empty : ", " + string.Join(", ", parts);
        }

        private static string FormatUtcMinute(DateTime utc) =>
            utc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

        internal static List<AppRegistryChange> DiffSnapshots(ImeRegistrySnapshot previous, ImeRegistrySnapshot next)
        {
            var changes = new List<AppRegistryChange>();
            foreach (var kv in next.Entries)
            {
                previous.Entries.TryGetValue(kv.Key, out var prev);
                var changed = ChangedFields(prev, kv.Value);
                if (changed.Count > 0)
                    changes.Add(new AppRegistryChange(kv.Value, changed, isNew: prev == null));
            }
            return changes;
        }

        internal static List<string> ChangedFields(AppRegistryEntry? prev, AppRegistryEntry next)
        {
            var changed = new List<string>();
            if (prev?.EnforcementState != next.EnforcementState) changed.Add("enforcementState");
            if (prev?.ErrorCode != next.ErrorCode) changed.Add("errorCode");
            if (prev?.ExitCode != next.ExitCode) changed.Add("exitCode");
            if (prev?.StatusServiceStatus != next.StatusServiceStatus) changed.Add("statusServiceStatus");
            if ((prev?.EspTracked ?? false) != next.EspTracked) changed.Add("espTracked");
            if (!string.Equals(prev?.EspPhase, next.EspPhase, StringComparison.OrdinalIgnoreCase)) changed.Add("espPhase");
            // Wait states: the deadline and each recorded deferral are transitions worth an event;
            // MaxDeferrals / AutoDeferred only travel along as data.
            if (prev?.InstallDeadlineUtc != next.InstallDeadlineUtc) changed.Add("installDeadline");
            if (prev?.DeferralsUsed != next.DeferralsUsed) changed.Add("deferralsUsed");
            if (prev?.DeferUntilUtc != next.DeferUntilUtc) changed.Add("deferUntil");
            return changed;
        }

        /// <summary>"success" / "error" when the registry outcome is terminal, else null.</summary>
        internal static string? TerminalOutcome(AppRegistryEntry entry)
        {
            if (entry.EnforcementState is int es)
            {
                var cls = ClassifyEnforcementState(es);
                if (cls == "success") return "success";
                if (cls == "error") return "error";
            }
            if (entry.StatusServiceStatus is int ss)
            {
                var cls = ClassifyStatusServiceStatus(ss);
                if (cls == "installed") return "success";
                if (cls == "failed") return "error";
            }
            return null;
        }

        /// <summary>
        /// Divergence rules (observability, conservative — prefer false-negative over noise):
        /// registry error vs log Installed, registry success vs log Error, or registry terminal
        /// while the tracker (actively tracking other apps) never saw the app at all.
        /// </summary>
        internal static bool IsDivergent(
            string registryOutcome,
            AppPackageState? logState,
            bool trackerHasAnyApps,
            out string reasonCode)
        {
            if (logState == null)
            {
                if (trackerHasAnyApps)
                {
                    reasonCode = "app_unknown_to_log_tracking";
                    return true;
                }
                reasonCode = string.Empty; // tracker idle (e.g. pre-ESP) — not judgeable
                return false;
            }

            if (registryOutcome == "error" && logState.InstallationState == AppInstallationState.Installed)
            {
                reasonCode = "registry_error_log_installed";
                return true;
            }
            if (registryOutcome == "success" && logState.InstallationState == AppInstallationState.Error)
            {
                reasonCode = "registry_success_log_error";
                return true;
            }

            reasonCode = string.Empty;
            return false;
        }

        // ── emission ────────────────────────────────────────────────────────────

        private void EmitStateEvent(AppRegistryChange change, DateTime nowUtc)
        {
            if (_stateEventsEmitted >= MaxStateEventsPerSession)
            {
                if (!_capNoticeEmitted)
                {
                    _capNoticeEmitted = true;
                    _post.Emit(
                        eventType: SharedEventTypes.RegistryAppState,
                        source: SourceLabel,
                        message: $"registry_app_state cap reached ({MaxStateEventsPerSession}) — further registry app-state changes are logged locally only",
                        severity: EventSeverity.Warning,
                        occurredAtUtc: nowUtc);
                }
                _logger?.Debug($"RegistryAppState: cap reached, suppressing {change.Entry.AppId}");
                return;
            }
            _stateEventsEmitted++;

            var e = change.Entry;
            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["appId"] = e.AppId,
                ["userContext"] = e.UserContext,
                ["changedFields"] = string.Join(",", change.ChangedFields),
            };
            if (e.EnforcementState is int es)
            {
                data["enforcementState"] = es.ToString(CultureInfo.InvariantCulture);
                data["enforcementClass"] = ClassifyEnforcementState(es);
            }
            if (e.ErrorCode is long ec) data["errorCode"] = ec.ToString(CultureInfo.InvariantCulture);
            if (e.ExitCode is int xc) data["exitCode"] = xc.ToString(CultureInfo.InvariantCulture);
            if (e.Intent is int intent) data["intent"] = intent.ToString(CultureInfo.InvariantCulture);
            if (e.StatusServiceStatus is int ss)
            {
                data["statusServiceStatus"] = ss.ToString(CultureInfo.InvariantCulture);
                var cls = ClassifyStatusServiceStatus(ss);
                if (cls != null) data["statusServiceClass"] = cls;
            }
            if (e.EspTracked) data["espTracked"] = "true";
            if (!string.IsNullOrEmpty(e.EspPhase)) data["espPhase"] = e.EspPhase!;
            if (e.InstallDeadlineUtc is DateTime deadline) data["installDeadlineUtc"] = deadline.ToString("o", CultureInfo.InvariantCulture);
            if (e.DeferralsUsed is int used) data["deferralsUsed"] = used.ToString(CultureInfo.InvariantCulture);
            if (e.DeferralMaxDeferrals is int max) data["deferralMaxDeferrals"] = max.ToString(CultureInfo.InvariantCulture);
            if (e.DeferUntilUtc is DateTime until) data["deferUntilUtc"] = until.ToString("o", CultureInfo.InvariantCulture);
            if (e.DeferralAutoDeferred is bool auto) data["deferralAutoDeferred"] = auto ? "true" : "false";

            var summary = data.TryGetValue("enforcementClass", out var encls) ? encls
                        : data.TryGetValue("statusServiceClass", out var sscls) ? sscls
                        : "updated";

            _post.Emit(
                eventType: SharedEventTypes.RegistryAppState,
                source: SourceLabel,
                message: $"Registry app state: {e.AppId} ({(e.UserContext == ImeRegistrySnapshot.DeviceContext ? "device" : "user")}) -> {summary}{DescribeWaitState(e)}",
                severity: EventSeverity.Info,
                data: data,
                occurredAtUtc: nowUtc);
        }

        // Baseline entries in the summary's typed payload are capped — a long-lived device
        // with many historical Win32Apps entries must not produce an oversized event.
        internal const int MaxBaselineSummaryApps = 50;

        private void EmitBaselineSummary(ImeRegistrySnapshot snapshot, DateTime nowUtc)
        {
            int success = 0, error = 0, inProgress = 0, other = 0;
            var apps = new List<Dictionary<string, string>>();
            foreach (var entry in snapshot.Entries.Values)
            {
                var cls = entry.EnforcementState is int es ? ClassifyEnforcementState(es) : null;
                switch (cls)
                {
                    case "success": success++; break;
                    case "error": error++; break;
                    case "inProgress": inProgress++; break;
                    default: other++; break;
                }

                if (apps.Count >= MaxBaselineSummaryApps) continue;
                var app = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["appId"] = entry.AppId,
                    ["userContext"] = entry.UserContext,
                };
                if (entry.EnforcementState is int state)
                {
                    app["enforcementState"] = state.ToString(CultureInfo.InvariantCulture);
                    app["enforcementClass"] = ClassifyEnforcementState(state);
                }
                if (entry.ErrorCode is long ec) app["errorCode"] = ec.ToString(CultureInfo.InvariantCulture);
                if (entry.ExitCode is int xc) app["exitCode"] = xc.ToString(CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(entry.LastUpdatedUtc)) app["lastUpdatedUtc"] = entry.LastUpdatedUtc!;
                if (entry.EspTracked) app["espTracked"] = "true";
                apps.Add(app);
            }

            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["totalApps"] = snapshot.Entries.Count.ToString(CultureInfo.InvariantCulture),
                ["successCount"] = success.ToString(CultureInfo.InvariantCulture),
                ["errorCount"] = error.ToString(CultureInfo.InvariantCulture),
                ["inProgressCount"] = inProgress.ToString(CultureInfo.InvariantCulture),
                ["otherCount"] = other.ToString(CultureInfo.InvariantCulture),
            };
            if (snapshot.Entries.Count > MaxBaselineSummaryApps)
                data["appListTruncated"] = "true";

            _post.Emit(
                eventType: SharedEventTypes.RegistryAppBaseline,
                source: SourceLabel,
                message: $"Registry app baseline at agent start: {snapshot.Entries.Count} apps ({success} success, {error} error, {inProgress} in progress) — state that predates this agent run",
                severity: EventSeverity.Info,
                data: data,
                occurredAtUtc: nowUtc,
                typedPayload: new { apps });
        }

        private void TrackTerminalTransitions(ImeRegistrySnapshot snapshot, DateTime nowUtc)
        {
            foreach (var kv in snapshot.Entries)
            {
                if (!_changedDuringSession.Contains(kv.Key)) continue; // baseline state — not ours to judge
                var outcome = TerminalOutcome(kv.Value);
                if (outcome == null)
                {
                    _terminalSinceUtc.Remove(kv.Key); // regressed to non-terminal — reset dwell
                    continue;
                }
                if (!_terminalSinceUtc.ContainsKey(kv.Key))
                    _terminalSinceUtc[kv.Key] = nowUtc;
            }
        }

        private void EvaluateReconciliations(ImeRegistrySnapshot snapshot, DateTime nowUtc)
        {
            if (_trackerStateProbe == null) return;

            List<AppPackageState>? trackerStates = null;
            foreach (var kv in _terminalSinceUtc.ToList())
            {
                if (_reconciliationEmitted.Contains(kv.Key)) continue;
                if (nowUtc - kv.Value < ReconcileSettleDelay) continue;
                if (!snapshot.Entries.TryGetValue(kv.Key, out var entry)) continue;

                var outcome = TerminalOutcome(entry);
                if (outcome == null) continue;

                trackerStates ??= (_trackerStateProbe() ?? Array.Empty<AppPackageState>()).ToList();
                var logState = trackerStates.FirstOrDefault(p =>
                    string.Equals(p.Id, entry.AppId, StringComparison.OrdinalIgnoreCase));

                if (!IsDivergent(outcome, logState, trackerStates.Count > 0, out var reasonCode))
                {
                    _reconciliationEmitted.Add(kv.Key); // agreed (or not judgeable) — done with this app
                    continue;
                }

                _reconciliationEmitted.Add(kv.Key);
                var data = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["appId"] = entry.AppId,
                    ["userContext"] = entry.UserContext,
                    ["registryOutcome"] = outcome,
                    ["reason"] = reasonCode,
                    ["logState"] = logState?.InstallationState.ToString() ?? "unknown",
                };
                if (logState != null && !string.IsNullOrEmpty(logState.Name)) data["appName"] = logState.Name;
                if (entry.EnforcementState is int es) data["enforcementState"] = es.ToString(CultureInfo.InvariantCulture);
                if (entry.ErrorCode is long ec) data["errorCode"] = ec.ToString(CultureInfo.InvariantCulture);

                _post.Emit(
                    eventType: SharedEventTypes.AppStateReconciliation,
                    source: SourceLabel,
                    message: $"Registry/log divergence for app {entry.AppId}: registry={outcome}, log={data["logState"]} ({reasonCode}) — IME log patterns may have drifted",
                    severity: EventSeverity.Warning,
                    immediateUpload: true,
                    data: data,
                    occurredAtUtc: nowUtc);

                _logger?.Warning($"RegistryAppState: divergence for {entry.AppId} — registry={outcome}, log={data["logState"]} ({reasonCode})");
            }
        }
    }

    /// <summary>Immutable-ish snapshot of the three IME registry surfaces, keyed per user-context + app.</summary>
    internal sealed class ImeRegistrySnapshot
    {
        public const string DeviceContext = "00000000-0000-0000-0000-000000000000";

        public Dictionary<string, AppRegistryEntry> Entries { get; } =
            new Dictionary<string, AppRegistryEntry>(StringComparer.OrdinalIgnoreCase);

        public AppRegistryEntry GetOrAdd(string userContext, string appId)
        {
            var normalizedUser = Guid.TryParse(userContext, out var g) ? g.ToString("D") : userContext;
            var key = normalizedUser + "|" + appId;
            if (!Entries.TryGetValue(key, out var entry))
            {
                entry = new AppRegistryEntry(key, normalizedUser, appId);
                Entries[key] = entry;
            }
            return entry;
        }
    }

    internal sealed class AppRegistryEntry
    {
        public AppRegistryEntry(string key, string userContext, string appId)
        {
            Key = key;
            UserContext = userContext;
            AppId = appId;
        }

        public string Key { get; }
        public string UserContext { get; }
        public string AppId { get; }
        public string? LastUpdatedUtc { get; set; }
        public int? EnforcementState { get; set; }
        public long? ErrorCode { get; set; }
        public int? ExitCode { get; set; }
        public int? Intent { get; set; }
        public int? StatusServiceStatus { get; set; }
        public bool EspTracked { get; set; }
        public string? EspPhase { get; set; }

        // Win32Apps\OperationalState — the two IME wait states (all UTC).
        public DateTime? InstallDeadlineUtc { get; set; }
        public int? DeferralsUsed { get; set; }
        public int? DeferralMaxDeferrals { get; set; }
        public DateTime? DeferUntilUtc { get; set; }
        public bool? DeferralAutoDeferred { get; set; }
    }

    internal sealed class AppRegistryChange
    {
        public AppRegistryChange(AppRegistryEntry entry, List<string> changedFields, bool isNew)
        {
            Entry = entry;
            ChangedFields = changedFields;
            IsNew = isNew;
        }

        public AppRegistryEntry Entry { get; }
        public List<string> ChangedFields { get; }
        public bool IsNew { get; }
    }
}
