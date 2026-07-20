# V2 Agent

How the on-device agent works:

* [Runtime Overview](overview.md) - lifecycle (install → boot guards → runtime → termination), collector hosts, the single-rail event pipeline to the backend, config & kill switch, on-disk layout.
* [Decision Engine](decision-engine.md) - the pure-reducer state machine (DecisionCore): signals, state, effects, the A/B/C completion arms, invariants, threading.
* [Logs, Persistence & Crash Recovery](logs-and-persistence.md) - every persisted file and why it exists; signal log vs spool; snapshot/journal; the replay-based recovery flow and quarantine.
* [Hello Wizard Un-Skip](hello-wizard-unskip.md) - why a single "WHfB disabled" policy read never decides completion: the HelloWizardStarted signal (prevention + cure) and the HelloTracker confirmation second read.
* [Autopilot ZTD Diagnostics](autopilot-ztd-diagnostics.md) - Windows' own diagnostic surfaces for the profile-download flow: ModernDeployment event IDs (807/815/164/...), the Diagnostics\Autopilot registry key, deployment-service endpoints, and the known-issue error-code map.
* [MDM Reboot Coalescing](mdm-reboot-coalescing.md) - attributing the mid-ESP coalesced reboot ("second sign-in") to the device-assigned policy URIs that forced it: 2800 watcher with cross-restart watermark, Shell-Core RebootCoalescing corroboration, ANALYZE-ESP-005 advisory.

Reading order for newcomers: overview → decision engine → persistence.
