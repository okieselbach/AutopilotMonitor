#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Runtime;
using AutopilotMonitor.Shared.Models;

namespace AutopilotMonitor.Agent.V2.Core.Monitoring.Enrollment.SystemSignals
{
    /// <summary>
    /// Pure state machine behind <c>PowerStateWatcherHost</c>: diffs successive
    /// <see cref="PowerStateResult"/> snapshots into <c>power_state_change</c> emissions.
    /// No WMI, no timers, no logging — fully deterministic and unit-testable.
    /// <para>
    /// Semantics:
    /// <list type="bullet">
    ///   <item><b>AC↔battery transitions</b> — one emission per accepted edge
    ///         (<c>ac_to_battery</c> Warning/immediate, <c>battery_to_ac</c> Info).</item>
    ///   <item><b>Threshold ladder</b> (default 50/30/15 %) — evaluated only while ON battery;
    ///         each level latches once per tracker lifetime (no re-emit when the device charges
    ///         back up and drains again — avoids plug-cycle alarm spam). A multi-level jump
    ///         emits only the LOWEST newly-crossed level. Entering battery power already below
    ///         a level counts as crossing it (covers "unplugged at 12 %" and, via
    ///         <see cref="Baseline"/>, "enrollment started at 12 %").</item>
    ///   <item><b>Storm cap</b> — a lifetime emission cap (default 20) as backstop against a
    ///         flapping dock / dying battery controller; <see cref="EmissionCapReached"/> flips
    ///         once so the host can log a single warning.</item>
    /// </list>
    /// A snapshot with <see cref="PowerStateResult.ProbeError"/> is ignored (previous state
    /// preserved); a null <see cref="PowerStateResult.BatteryPercent"/> skips threshold
    /// evaluation but AC diffing still works.
    /// </para>
    /// </summary>
    internal sealed class PowerStateTransitionTracker
    {
        public const string TransitionAcToBattery = "ac_to_battery";
        public const string TransitionBatteryToAc = "battery_to_ac";
        public const string TransitionThresholdCrossed = "threshold_crossed";

        internal static readonly int[] DefaultThresholdLadder = { 50, 30, 15 };
        internal const int DefaultMaxEmissions = 20;

        private readonly int[] _ladder;              // strictly descending
        private readonly int _maxEmissions;
        private readonly HashSet<int> _latchedLevels = new HashSet<int>();

        private bool _hasState;
        private bool _lastOnAc;
        private int _emitted;

        public PowerStateTransitionTracker(IReadOnlyList<int>? thresholdLadder = null, int maxEmissions = DefaultMaxEmissions)
        {
            _ladder = (thresholdLadder ?? DefaultThresholdLadder).OrderByDescending(l => l).ToArray();
            _maxEmissions = maxEmissions;
        }

        /// <summary>True once the lifetime emission cap suppressed at least one emission.</summary>
        public bool EmissionCapReached { get; private set; }

        /// <summary>
        /// Latches the arm-time state silently — except when the device is already on battery
        /// at/below a ladder level, which yields one threshold emission for the lowest crossed
        /// level (an enrollment STARTING at 12 % must still produce the event the analyze rule
        /// matches on). No AC-transition emission is ever produced from a baseline.
        /// </summary>
        public IReadOnlyList<PowerStateEmission> Baseline(PowerStateResult probe)
        {
            if (probe == null || probe.ProbeError != null || !probe.HasBattery)
                return Array.Empty<PowerStateEmission>();

            _hasState = true;
            _lastOnAc = probe.OnAcPower;

            var emissions = new List<PowerStateEmission>();
            EvaluateThresholds(probe, emissions);
            return emissions;
        }

        /// <summary>
        /// Diffs a fresh (host-debounced) snapshot against the last accepted state and returns
        /// 0..n emissions — an AC transition and a threshold crossing can co-occur (unplugged
        /// at 10 % ⇒ <c>ac_to_battery</c> + <c>threshold_crossed:15</c>).
        /// </summary>
        public IReadOnlyList<PowerStateEmission> Evaluate(PowerStateResult probe)
        {
            if (probe == null || probe.ProbeError != null)
                return Array.Empty<PowerStateEmission>();
            if (!_hasState)
                return Baseline(probe);

            var emissions = new List<PowerStateEmission>();

            if (_lastOnAc && !probe.OnAcPower)
            {
                Emit(emissions, new PowerStateEmission(
                    transition: TransitionAcToBattery,
                    thresholdPercent: null,
                    probe: probe,
                    severity: EventSeverity.Warning,
                    immediateUpload: true,
                    message: $"Power source switched from AC to battery ({FormatPercent(probe)} remaining) during enrollment"));
            }
            else if (!_lastOnAc && probe.OnAcPower)
            {
                Emit(emissions, new PowerStateEmission(
                    transition: TransitionBatteryToAc,
                    thresholdPercent: null,
                    probe: probe,
                    severity: EventSeverity.Info,
                    immediateUpload: false,
                    message: $"Power source switched back to AC ({FormatPercent(probe)}{(probe.IsCharging ? ", charging" : string.Empty)})"));
            }

            _lastOnAc = probe.OnAcPower;
            EvaluateThresholds(probe, emissions);
            return emissions;
        }

        /// <summary>
        /// While on battery with a known percentage: emit the LOWEST ladder level at/above the
        /// current charge that is not yet latched, then latch every level at/above the charge
        /// (a 60 %→10 % jump emits threshold 15 only, latching 50/30/15).
        /// </summary>
        private void EvaluateThresholds(PowerStateResult probe, List<PowerStateEmission> emissions)
        {
            if (probe.OnAcPower || !probe.BatteryPercent.HasValue) return;
            var percent = probe.BatteryPercent.Value;

            int? lowestCrossed = null;
            foreach (var level in _ladder) // descending — the last match is the lowest crossed level
            {
                if (percent <= level && !_latchedLevels.Contains(level))
                    lowestCrossed = level;
            }
            if (!lowestCrossed.HasValue) return;

            foreach (var level in _ladder)
            {
                if (percent <= level) _latchedLevels.Add(level);
            }

            var threshold = lowestCrossed.Value;
            var severity = threshold <= 15 ? EventSeverity.Error
                         : threshold <= 30 ? EventSeverity.Warning
                         : EventSeverity.Info;
            Emit(emissions, new PowerStateEmission(
                transition: TransitionThresholdCrossed,
                thresholdPercent: threshold,
                probe: probe,
                severity: severity,
                immediateUpload: severity >= EventSeverity.Warning,
                message: $"Battery dropped below {threshold}% ({FormatPercent(probe)} remaining) while enrolling on battery power"));
        }

        private void Emit(List<PowerStateEmission> emissions, PowerStateEmission emission)
        {
            if (_emitted >= _maxEmissions)
            {
                EmissionCapReached = true;
                return;
            }
            _emitted++;
            emissions.Add(emission);
        }

        private static string FormatPercent(PowerStateResult probe)
            => probe.BatteryPercent.HasValue ? $"battery {probe.BatteryPercent.Value}%" : "battery level unknown";
    }

    /// <summary>One decided <c>power_state_change</c> emission — the host maps this 1:1 onto an event.</summary>
    internal sealed class PowerStateEmission
    {
        public PowerStateEmission(string transition, int? thresholdPercent, PowerStateResult probe,
            EventSeverity severity, bool immediateUpload, string message)
        {
            Transition = transition;
            ThresholdPercent = thresholdPercent;
            OnAcPower = probe.OnAcPower;
            BatteryPercent = probe.BatteryPercent;
            IsCharging = probe.IsCharging;
            BatteryLifeMinutes = probe.BatteryLifeMinutes;
            Severity = severity;
            ImmediateUpload = immediateUpload;
            Message = message;
        }

        public string Transition { get; }
        public int? ThresholdPercent { get; }
        public bool OnAcPower { get; }
        public int? BatteryPercent { get; }
        public bool IsCharging { get; }
        public int? BatteryLifeMinutes { get; }
        public EventSeverity Severity { get; }
        public bool ImmediateUpload { get; }
        public string Message { get; }
    }
}
