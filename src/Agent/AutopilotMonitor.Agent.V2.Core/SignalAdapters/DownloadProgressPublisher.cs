#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.Ime;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Periodic;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.Shared.Models;
using SharedEventTypes = AutopilotMonitor.Shared.Constants.EventTypes;

namespace AutopilotMonitor.Agent.V2.Core.SignalAdapters
{
    /// <summary>
    /// The one sender of <c>download_progress</c> (D-444). The DO collector and the IME adapter hand
    /// their samples here; <see cref="DownloadProgressGate"/> decides what is sent.
    /// <para>
    /// A sent event keeps the source label and timestamp of the sample whose byte count it carries:
    /// "ImeLogTracker" events are stamped with the IME line's time (the backend's clock checks treat
    /// that label as the log writer's clock), DO events with the agent clock. A DO event also carries
    /// the app's IME state, an IME event the last DO values when there are any.
    /// </para>
    /// <para>
    /// A sample held back by the interval stays pending and is sent when the download ends or the DO
    /// collector goes idle, so the last byte count and DO breakdown always reach the backend. When the
    /// session moves into a slower stage, one <c>download_progress_throttled</c> event says so.
    /// Thread-safe: the DO poll thread and the IME tracker thread both call in.
    /// </para>
    /// </summary>
    internal sealed class DownloadProgressPublisher
    {
        internal const string SourceLabel = "DownloadProgressPublisher";
        internal const string DoSourceLabel = "DeliveryOptimizationCollector";
        internal const string ImeSourceLabel = "ImeLogTracker";

        private readonly InformationalEventPost _post;
        private readonly IClock _clock;
        private readonly AgentLogger? _logger;
        private readonly DownloadProgressBudget _budget;
        private readonly DownloadProgressGate _gate;
        private readonly object _lock = new object();
        private readonly Dictionary<string, Tick> _pending = new Dictionary<string, Tick>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DoProgressSample> _lastDoSample =
            new Dictionary<string, DoProgressSample>(StringComparer.OrdinalIgnoreCase);

        public DownloadProgressPublisher(
            InformationalEventPost post, IClock clock, DownloadProgressBudget budget, AgentLogger? logger = null)
        {
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _clock = clock ?? throw new ArgumentNullException(nameof(clock));
            _budget = budget ?? throw new ArgumentNullException(nameof(budget));
            _logger = logger;
            _gate = new DownloadProgressGate(budget, clock);
        }

        /// <summary>DO poll: the byte count of a tracked app changed.</summary>
        public void OfferDoSample(AppPackageState app, DoProgressSample sample)
        {
            if (app == null || string.IsNullOrEmpty(app.Id) || sample == null) return;
            lock (_lock)
            {
                _lastDoSample[app.Id] = sample;
                var now = _clock.UtcNow;
                var verdict = _gate.Evaluate(app.Id, DownloadProgressSource.DeliveryOptimization, sample.TotalBytes, sample.FileSize, now);
                if (verdict == DownloadProgressVerdict.Drop) return;
                Apply(verdict, BuildDoTick(app, sample, now));
            }
        }

        /// <summary>IME log: a progress line for an app that stays in Downloading.</summary>
        public void OfferImeSample(AppPackageState app, IReadOnlyDictionary<string, string> imePayload, string message, DateTime occurredAtUtc)
        {
            if (app == null || string.IsNullOrEmpty(app.Id) || imePayload == null) return;
            lock (_lock)
            {
                var verdict = _gate.Evaluate(app.Id, DownloadProgressSource.Ime, app.BytesDownloaded, app.BytesTotal, occurredAtUtc);
                if (verdict == DownloadProgressVerdict.Drop) return;
                Apply(verdict, BuildImeTick(app, imePayload, message, occurredAtUtc));
            }
        }

        /// <summary>
        /// The <c>status=completed|failed</c> tick that closes an app in the portal's download panel.
        /// Always sent; it counts toward the session total.
        /// </summary>
        public void EmitTerminalTick(AppPackageState app, IReadOnlyDictionary<string, string> payload, string message, DateTime occurredAtUtc)
        {
            if (app == null || string.IsNullOrEmpty(app.Id) || payload == null) return;
            lock (_lock)
            {
                FlushPendingLocked(app.Id);
                Send(new Tick(app.Id, app.BytesDownloaded, app.BytesTotal, occurredAtUtc, ImeSourceLabel, message,
                    stringData: payload, typedData: null));
                AnnounceStage(_gate.RecordSessionEvent());
                _gate.EndDownload(app.Id);
            }
        }

        /// <summary>The app left Downloading: send its pending sample first, then start its next download fresh.</summary>
        public void EndDownload(string appId)
        {
            if (string.IsNullOrEmpty(appId)) return;
            lock (_lock)
            {
                FlushPendingLocked(appId);
                _gate.EndDownload(appId);
            }
        }

        /// <summary>The DO collector went idle or stopped: send every pending sample.</summary>
        public void FlushPending()
        {
            lock (_lock)
            {
                if (_pending.Count == 0) return;
                foreach (var appId in new List<string>(_pending.Keys))
                    FlushPendingLocked(appId);
            }
        }

        private void Apply(DownloadProgressVerdict verdict, Tick tick)
        {
            if (verdict == DownloadProgressVerdict.Hold)
            {
                _pending[tick.AppId] = tick;
                return;
            }
            _pending.Remove(tick.AppId);
            SendAndRecord(tick);
        }

        private void FlushPendingLocked(string appId)
        {
            if (!_pending.TryGetValue(appId, out var tick)) return;
            _pending.Remove(appId);
            SendAndRecord(tick);
        }

        private void SendAndRecord(Tick tick)
        {
            Send(tick);
            AnnounceStage(_gate.RecordEmitted(tick.AppId, tick.Bytes, tick.Total, tick.OccurredAtUtc));
        }

        private void Send(Tick tick)
        {
            _post.Emit(
                eventType: SharedEventTypes.DownloadProgress,
                source: tick.Source,
                message: tick.Message,
                severity: EventSeverity.Debug,
                // Drives the live download bar in the portal.
                immediateUpload: true,
                data: tick.StringData,
                occurredAtUtc: tick.OccurredAtUtc,
                typedPayload: tick.TypedData);
        }

        private void AnnounceStage(int? enteredStage)
        {
            if (!enteredStage.HasValue) return;

            var culture = CultureInfo.InvariantCulture;
            var stage = enteredStage.Value;
            var interval = DownloadProgressGate.IntervalSecondsFor(stage);
            var liveStart = (int)DownloadProgressGate.LiveStart.TotalSeconds;
            var sent = _budget.EventsSent;
            var data = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["stage"] = stage.ToString(culture),
                ["intervalSeconds"] = interval.ToString(culture),
                ["progressEventsSent"] = sent.ToString(culture),
                ["stageThreshold"] = DownloadProgressGate.Stages[stage].FromEventsSent.ToString(culture),
                ["liveStartSeconds"] = liveStart.ToString(culture),
            };

            _post.Emit(
                eventType: SharedEventTypes.DownloadProgressThrottled,
                source: SourceLabel,
                message: $"Long downloads: download progress is now reported every {interval} s per app to protect against excessive events; each download still reports its first {liveStart} s live",
                severity: EventSeverity.Info,
                immediateUpload: true,
                data: data,
                occurredAtUtc: _clock.UtcNow);

            _logger?.Info($"DownloadProgressPublisher: stage {stage} entered after {sent} download_progress events — interval now {interval} s per app (live start {liveStart} s)");
        }

        private static Tick BuildDoTick(AppPackageState app, DoProgressSample s, DateTime atUtc)
        {
            var percent = s.PercentComplete;
            var data = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["appId"] = app.Id,
                ["appName"] = app.Name,
                // Fields the portal's download panel reads.
                ["bytesDownloaded"] = s.TotalBytes,
                ["bytesTotal"] = s.FileSize,
                ["progressPercent"] = percent,
                ["doFileSize"] = s.FileSize,
                ["doTotalBytesDownloaded"] = s.TotalBytes,
                ["doPercentComplete"] = percent,
                ["doBytesFromPeers"] = s.BytesFromPeers,
                ["doBytesFromHttp"] = s.BytesFromHttp,
                ["doPercentPeerCaching"] = s.PercentPeerCaching,
                ["doDownloadMode"] = s.DownloadMode,
                ["doBytesFromLanPeers"] = s.BytesFromLanPeers,
                ["doBytesFromGroupPeers"] = s.BytesFromGroupPeers,
                ["doBytesFromInternetPeers"] = s.BytesFromInternetPeers,
                ["doBytesFromLinkLocalPeers"] = s.BytesFromLinkLocalPeers,
                ["doBytesFromCacheServer"] = s.BytesFromCacheServer,
                ["doSource"] = "os_cmdlet",
                // The app's IME state, so the event carries the whole picture.
                ["state"] = app.InstallationState.ToString(),
                ["intent"] = app.Intent.ToString(),
                ["targeted"] = app.Targeted.ToString(),
                ["runAs"] = app.RunAs.ToString(),
            };
            if (!string.IsNullOrEmpty(s.CacheHost)) data["doCacheHost"] = s.CacheHost;
            if (!string.IsNullOrEmpty(app.AppType)) data["appType"] = app.AppType;
            if (!string.IsNullOrEmpty(app.AppVersion)) data["appVersion"] = app.AppVersion;

            var message = $"{app.Name ?? app.Id}: {percent}% ({s.TotalBytes}/{s.FileSize}) peers={s.PercentPeerCaching}% mode={s.DownloadMode}";
            return new Tick(app.Id, s.TotalBytes, s.FileSize, atUtc, DoSourceLabel, message, stringData: null, typedData: data);
        }

        private Tick BuildImeTick(AppPackageState app, IReadOnlyDictionary<string, string> imePayload, string message, DateTime atUtc)
        {
            var data = new Dictionary<string, string>(imePayload.Count + 16, StringComparer.Ordinal);
            foreach (var kv in imePayload) data[kv.Key] = kv.Value;

            // Last DO values for the app (DO went quiet, the IME line took over): the breakdown stays
            // visible. The byte count stays the IME line's.
            if (_lastDoSample.TryGetValue(app.Id, out var s))
            {
                var culture = CultureInfo.InvariantCulture;
                AddIfAbsent(data, "doFileSize", s.FileSize.ToString(culture));
                AddIfAbsent(data, "doTotalBytesDownloaded", s.TotalBytes.ToString(culture));
                AddIfAbsent(data, "doBytesFromPeers", s.BytesFromPeers.ToString(culture));
                AddIfAbsent(data, "doBytesFromHttp", s.BytesFromHttp.ToString(culture));
                AddIfAbsent(data, "doPercentPeerCaching", s.PercentPeerCaching.ToString(culture));
                AddIfAbsent(data, "doDownloadMode", s.DownloadMode.ToString(culture));
                AddIfAbsent(data, "doBytesFromLanPeers", s.BytesFromLanPeers.ToString(culture));
                AddIfAbsent(data, "doBytesFromGroupPeers", s.BytesFromGroupPeers.ToString(culture));
                AddIfAbsent(data, "doBytesFromInternetPeers", s.BytesFromInternetPeers.ToString(culture));
                AddIfAbsent(data, "doBytesFromLinkLocalPeers", s.BytesFromLinkLocalPeers.ToString(culture));
                AddIfAbsent(data, "doBytesFromCacheServer", s.BytesFromCacheServer.ToString(culture));
                if (!string.IsNullOrEmpty(s.CacheHost)) AddIfAbsent(data, "doCacheHost", s.CacheHost!);
            }

            return new Tick(app.Id, app.BytesDownloaded, app.BytesTotal, atUtc, ImeSourceLabel, message, stringData: data, typedData: null);
        }

        private static void AddIfAbsent(Dictionary<string, string> data, string key, string value)
        {
            if (!data.ContainsKey(key)) data[key] = value;
        }

        private sealed class Tick
        {
            public Tick(string appId, long bytes, long total, DateTime occurredAtUtc, string source, string message,
                IReadOnlyDictionary<string, string>? stringData, Dictionary<string, object?>? typedData)
            {
                AppId = appId;
                Bytes = bytes;
                Total = total;
                OccurredAtUtc = occurredAtUtc;
                Source = source;
                Message = message;
                StringData = stringData;
                TypedData = typedData;
            }

            public string AppId { get; }
            public long Bytes { get; }
            public long Total { get; }
            public DateTime OccurredAtUtc { get; }
            public string Source { get; }
            public string Message { get; }
            public IReadOnlyDictionary<string, string>? StringData { get; }
            public Dictionary<string, object?>? TypedData { get; }
        }
    }
}
