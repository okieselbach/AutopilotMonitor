# V2 Agent

How the on-device agent works, in three documents:

* [Runtime Overview](overview.md) - lifecycle (install → boot guards → runtime → termination), collector hosts, the single-rail event pipeline to the backend, config & kill switch, on-disk layout.
* [Decision Engine](decision-engine.md) - the pure-reducer state machine (DecisionCore): signals, state, effects, the A/B/C completion arms, invariants, threading.
* [Logs, Persistence & Crash Recovery](logs-and-persistence.md) - every persisted file and why it exists; signal log vs spool; snapshot/journal; the replay-based recovery flow and quarantine.

Reading order for newcomers: overview → decision engine → persistence.
