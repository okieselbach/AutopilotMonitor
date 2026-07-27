#nullable enable
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.DecisionCore.Engine;
using AutopilotMonitor.DecisionCore.Signals;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Orchestration
{
    /// <summary>
    /// DeviceInfoHost phase-driven re-collection triggers (V1 parity / plan §5.8 TODO closure):
    /// CollectAtEnrollmentStart on the first DeviceSetup phase, CollectAtEnd on FinalizingSetup
    /// or desktop arrival. Covers the trigger predicates; the one-shot mechanics mirror the
    /// proven ProvisioningPackageHost pattern and the heavy collection itself is OS-bound.
    /// </summary>
    public sealed class DeviceInfoHostTriggerTests
    {
        private static IReadOnlyDictionary<string, string> Phase(string phase) =>
            new Dictionary<string, string> { { SignalPayloadKeys.EspPhase, phase } };

        [Fact]
        public void DeviceSetup_phase_is_the_enrollment_start_trigger()
        {
            Assert.True(DeviceInfoHost.IsEnrollmentStartTrigger(DecisionSignalKind.EspPhaseChanged, Phase("DeviceSetup")));
            Assert.False(DeviceInfoHost.IsEnrollmentStartTrigger(DecisionSignalKind.EspPhaseChanged, Phase("AccountSetup")));
            Assert.False(DeviceInfoHost.IsEnrollmentStartTrigger(DecisionSignalKind.EspPhaseChanged, Phase("FinalizingSetup")));
            Assert.False(DeviceInfoHost.IsEnrollmentStartTrigger(DecisionSignalKind.EspPhaseChanged, null));
            Assert.False(DeviceInfoHost.IsEnrollmentStartTrigger(DecisionSignalKind.DesktopArrived, null));
        }

        [Fact]
        public void FinalizingSetup_or_desktop_arrival_is_the_end_trigger()
        {
            Assert.True(DeviceInfoHost.IsEndTrigger(DecisionSignalKind.EspPhaseChanged, Phase("FinalizingSetup")));
            Assert.True(DeviceInfoHost.IsEndTrigger(DecisionSignalKind.DesktopArrived, null)); // no-ESP / WDP v2 fallback
            Assert.False(DeviceInfoHost.IsEndTrigger(DecisionSignalKind.EspPhaseChanged, Phase("DeviceSetup")));
            Assert.False(DeviceInfoHost.IsEndTrigger(DecisionSignalKind.EspPhaseChanged, null));
            Assert.False(DeviceInfoHost.IsEndTrigger(DecisionSignalKind.InformationalEvent, Phase("FinalizingSetup")));
        }

        // Audit Q2 — the payload shape the adapter's apps sub-phase declaration produces on the
        // ingress rail (InformationalEventPost: eventType + declared phase enum name).
        private static IReadOnlyDictionary<string, string> SubPhaseDeclaration(string declaredPhase) =>
            new Dictionary<string, string>
            {
                { SignalPayloadKeys.EventType, AutopilotMonitor.Shared.Constants.EventTypes.PhaseTransition },
                { AutopilotMonitor.Agent.V2.Core.Telemetry.Events.EventTimelineEmitter.PhaseParamKey, declaredPhase },
            };

        [Fact]
        public void Apps_subphase_declarations_classify_as_esp_config_refresh_triggers()
        {
            Assert.Equal(DeviceInfoHost.EspConfigTriggerAppsDevice,
                DeviceInfoHost.ClassifyEspConfigRefreshTrigger(DecisionSignalKind.InformationalEvent, SubPhaseDeclaration("AppsDevice")));
            Assert.Equal(DeviceInfoHost.EspConfigTriggerAppsUser,
                DeviceInfoHost.ClassifyEspConfigRefreshTrigger(DecisionSignalKind.InformationalEvent, SubPhaseDeclaration("AppsUser")));
            // Non-apps phase declarations (e.g. the reducer's Finalizing entry) never classify.
            Assert.Null(DeviceInfoHost.ClassifyEspConfigRefreshTrigger(DecisionSignalKind.InformationalEvent, SubPhaseDeclaration("FinalizingSetup")));
            // A phase_transition without a declared phase never classifies.
            Assert.Null(DeviceInfoHost.ClassifyEspConfigRefreshTrigger(DecisionSignalKind.InformationalEvent,
                new Dictionary<string, string> { { SignalPayloadKeys.EventType, AutopilotMonitor.Shared.Constants.EventTypes.PhaseTransition } }));
        }

        [Fact]
        public void AccountSetup_phase_classifies_as_the_user_setup_refresh_trigger()
        {
            Assert.Equal(DeviceInfoHost.EspConfigTriggerAccountSetup,
                DeviceInfoHost.ClassifyEspConfigRefreshTrigger(DecisionSignalKind.EspPhaseChanged, Phase("AccountSetup")));
            // DeviceSetup is the enrollment-start trigger, not an ESP-config refresh.
            Assert.Null(DeviceInfoHost.ClassifyEspConfigRefreshTrigger(DecisionSignalKind.EspPhaseChanged, Phase("DeviceSetup")));
            Assert.Null(DeviceInfoHost.ClassifyEspConfigRefreshTrigger(DecisionSignalKind.EspPhaseChanged, null));
            // Unrelated informational events never classify.
            Assert.Null(DeviceInfoHost.ClassifyEspConfigRefreshTrigger(DecisionSignalKind.InformationalEvent,
                new Dictionary<string, string> { { SignalPayloadKeys.EventType, "esp_config_detected" } }));
        }
    }
}
