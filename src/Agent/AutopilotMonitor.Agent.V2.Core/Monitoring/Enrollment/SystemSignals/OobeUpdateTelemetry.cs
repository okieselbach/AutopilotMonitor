using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// One CloudExperienceHost record of the OOBE update page, parsed by
    /// <see cref="ShellCoreTracker.ParseUpdatePageRecord"/>.
    /// </summary>
    internal sealed class OobeUpdatePageRecord
    {
        public const string PageStarted = "page_started";
        public const string PageStopped = "page_stopped";
        public const string EventName = "event_name";

        public OobeUpdatePageRecord(string cxhEvent, int windowsEventId, string page = null, string result = null, string name = null, string value = null)
        {
            CxhEvent = cxhEvent;
            WindowsEventId = windowsEventId;
            Page = page;
            Result = result;
            Name = name;
            Value = value;
        }

        /// <summary><see cref="PageStarted"/>, <see cref="PageStopped"/> or <see cref="EventName"/>.</summary>
        public string CxhEvent { get; }
        public int WindowsEventId { get; }

        /// <summary>The page's CXID (start), or the page a stop is attributed to.</summary>
        public string Page { get; }

        /// <summary>The page result of a stop.</summary>
        public string Result { get; }

        /// <summary>The event name of a 62406/62407 record.</summary>
        public string Name { get; }

        /// <summary>A 62407 value of a safe form (boolean, integer, HRESULT); null otherwise.</summary>
        public string Value { get; }
    }

    /// <summary>
    /// Measurement telemetry of the OOBE quality update (D-310): the update page's
    /// CloudExperienceHost records as <c>oobe_update_page</c> and the registry state behind it as
    /// <c>oobe_update_state</c>. Debug, bounded per agent run, no decisions. Owned by
    /// <see cref="ShellCoreTracker"/>, which observes the page.
    /// </summary>
    internal sealed class OobeUpdateTelemetry
    {
        internal const int MaxPageEventsPerRun = 150;
        internal const int MaxPageEventsPerKey = 3;
        internal const int MaxStateEventsPerRun = 6;
        internal const int MaxStateLineLength = 1000;

        /// <summary>When a registry snapshot is taken (<c>moment</c> of <c>oobe_update_state</c>).</summary>
        internal static class Moments
        {
            public const string AgentStart = "agent_start";
            public const string UpdatePageStarted = "update_page_started";
            public const string UpdatePageStopped = "update_page_stopped";
            public const string AgentStop = "agent_stop";
        }

        private const string Source = "ShellCoreTracker";

        private readonly string _sessionId;
        private readonly string _tenantId;
        private readonly InformationalEventPost _post;
        private readonly AgentLogger _logger;
        private readonly Func<IReadOnlyList<OobeUpdateRegistrySnapshot.KeyState>> _readRegistry;

        private readonly object _lock = new object();
        private readonly Dictionary<string, int> _pageEventsPerKey = new Dictionary<string, int>(StringComparer.Ordinal);
        private int _pageEvents;
        private bool _pageCapLogged;
        private int _stateEvents;
        private string _lastStateContent;

        public OobeUpdateTelemetry(
            string sessionId,
            string tenantId,
            InformationalEventPost post,
            AgentLogger logger,
            Func<IReadOnlyList<OobeUpdateRegistrySnapshot.KeyState>> readRegistry)
        {
            _sessionId = sessionId ?? throw new ArgumentNullException(nameof(sessionId));
            _tenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
            _post = post ?? throw new ArgumentNullException(nameof(post));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _readRegistry = readRegistry ?? throw new ArgumentNullException(nameof(readRegistry));
        }

        /// <summary>
        /// One <c>oobe_update_page</c> per update-page record, at most
        /// <see cref="MaxPageEventsPerKey"/> per page/name — a page that logs the same name in a
        /// loop cannot flood the session. Event names also share <see cref="MaxPageEventsPerRun"/>
        /// per run; a page start or stop is outside that budget, because the time attribution
        /// reads the update's span from them. True when the record was reported.
        /// </summary>
        public bool ReportPage(OobeUpdatePageRecord record, DateTime occurredAtUtc, bool isBackfill)
        {
            if (record == null) return false;

            var isEventName = record.CxhEvent == OobeUpdatePageRecord.EventName;
            var key = $"{record.CxhEvent}:{record.Page ?? record.Name}";
            int occurrence;
            lock (_lock)
            {
                if (isEventName && _pageEvents >= MaxPageEventsPerRun)
                {
                    if (!_pageCapLogged)
                    {
                        _pageCapLogged = true;
                        _logger.Info($"OOBE update page telemetry: {MaxPageEventsPerRun} event names reached, the rest of this run stays in agent.log");
                    }
                    return false;
                }
                _pageEventsPerKey.TryGetValue(key, out occurrence);
                if (occurrence >= MaxPageEventsPerKey) return false;
                occurrence++;
                _pageEventsPerKey[key] = occurrence;
                if (isEventName) _pageEvents++;
            }

            var data = new Dictionary<string, object>
            {
                { "cxhEvent", record.CxhEvent },
                { "windowsEventId", record.WindowsEventId },
                { "eventTime", occurredAtUtc.ToString("o") },
                { "backfill", isBackfill },
                { "occurrence", occurrence },
            };
            if (record.Page != null) data["page"] = record.Page;
            if (record.Result != null) data["result"] = record.Result;
            if (record.Name != null) data["name"] = record.Name;
            if (record.Value != null) data["value"] = record.Value;

            try
            {
                _post.Emit(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    Timestamp = occurredAtUtc,
                    EventType = Constants.EventTypes.OobeUpdatePage,
                    Severity = EventSeverity.Debug,
                    Source = Source,
                    Phase = EnrollmentPhase.Unknown,
                    Message = DescribePage(record),
                    Data = data,
                    ImmediateUpload = false,
                });
                return true;
            }
            catch (Exception ex)
            {
                _logger.Debug($"OOBE update page telemetry failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Reads the OOBE update registry state, logs it and reports it as <c>oobe_update_state</c>
        /// — only when at least one key holds values and the content changed since the last report
        /// of this run, at most <see cref="MaxStateEventsPerRun"/> times. Fail-soft.
        /// </summary>
        public void ReportState(string moment, string page = null)
        {
            var label = page == null ? moment : $"{moment} {page}";
            IReadOnlyList<OobeUpdateRegistrySnapshot.KeyState> states;
            try
            {
                states = _readRegistry() ?? Array.Empty<OobeUpdateRegistrySnapshot.KeyState>();
            }
            catch (Exception ex)
            {
                _logger.Debug($"OOBE update registry ({label}): snapshot failed: {ex.Message}");
                return;
            }

            OobeUpdateRegistrySnapshot.Log(_logger, label, states);

            var withValues = states.Where(s => s.HasValues).ToList();
            if (withValues.Count == 0) return;

            var content = string.Join("\n", withValues.Select(s => $"{s.Alias}={s.Line}"));
            lock (_lock)
            {
                if (string.Equals(content, _lastStateContent, StringComparison.Ordinal)) return;
                if (_stateEvents >= MaxStateEventsPerRun) return;
                _lastStateContent = content;
                _stateEvents++;
            }

            var data = new Dictionary<string, object>
            {
                { "moment", moment },
                { "keysWithValues", withValues.Count },
            };
            if (page != null) data["page"] = page;
            foreach (var state in withValues)
            {
                data[state.Alias] = state.Line.Length > MaxStateLineLength
                    ? state.Line.Substring(0, MaxStateLineLength) + "…"
                    : state.Line;
            }

            try
            {
                _post.Emit(new EnrollmentEvent
                {
                    SessionId = _sessionId,
                    TenantId = _tenantId,
                    EventType = Constants.EventTypes.OobeUpdateState,
                    Severity = EventSeverity.Debug,
                    Source = Source,
                    Phase = EnrollmentPhase.Unknown,
                    Message = $"OOBE update registry state ({label}): {withValues.Count} key(s) with values",
                    Data = data,
                    ImmediateUpload = false,
                });
            }
            catch (Exception ex)
            {
                _logger.Debug($"OOBE update state telemetry failed: {ex.Message}");
            }
        }

        private static string DescribePage(OobeUpdatePageRecord record)
        {
            switch (record.CxhEvent)
            {
                case OobeUpdatePageRecord.PageStarted:
                    return $"OOBE update page started: {record.Page}";
                case OobeUpdatePageRecord.PageStopped:
                    return $"OOBE update page stopped: {record.Page} (result: {record.Result})";
                default:
                    return record.Value == null
                        ? $"OOBE update page event: {record.Name}"
                        : $"OOBE update page event: {record.Name} = {record.Value}";
            }
        }
    }
}
