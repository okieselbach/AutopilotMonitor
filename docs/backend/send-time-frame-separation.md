---
type: concept
title: Send-Time Frame Separation (X-Send-Time-Utc)
description: One device-clock send timestamp per upload batch splits ReceivedAt − OccurredUtc into pure spool delay and pure device-vs-server clock offset — turning clock diagnostics from median statistics into direct per-request measurement.
resource: src/Backend/AutopilotMonitor.Functions/Functions/Ingest/IngestTelemetryFunction.cs
tags: [backend, agent, ingest, timestamps, clock-skew, telemetry, provenance]
timestamp: 2026-08-21
---

# Problem

Events carry two time anchors: `OccurredUtc` (device clock at emission) and `ReceivedAt`
(server clock at ingest). Their difference conflates two physically distinct phenomena:

* **Spool delay** — the event waited offline or in retry backoff before upload. Expected,
  harmless, minutes to hours.
* **Device-vs-server clock offset** — the actual diagnostic signal.

A uniform spool backlog (events emitted close together, uploaded after an outage) is
indistinguishable from a clock offset in that delta; a draining backlog fakes a clock jump
ramp. Every consumer had to filter this statistically (spread caps, plateau guards,
persistence tests) — and the first field clock-jump measurement failed exactly on this
ambiguity.

# Schema

The agent stamps one header per upload attempt (`BackendTelemetryUploader`):

```
X-Send-Time-Utc: <DateTime.UtcNow, ISO-8601 round-trip>
```

Per **attempt**, not per batch content: a retry after backoff is a new send moment. The
ingest function parses it (`ParseSendTimeHeader`: round-trip parse, UTC-normalized, values
before year 2000 rejected as garbage — deliberately **no upper clamp**, a future-dated
send time IS the clock-error measurement), and `StampServerFields` writes it as `SentAt`
onto every event of the request — server-stamped, request-level, never trusted per-event
from the wire. Stored as the nullable `SentAt` column on Events (absent for pre-header
agents), mapped back everywhere events are read, exposed as `sentAt` in the `fields=`
projection and verbatim on the raw endpoints.

The separation is then exact:

```
SentAt − OccurredUtc   = pure spool delay            (both device frame; offset cancels)
ReceivedAt − SentAt    = network latency + clock offset   (the only frame crossing)
```

Network latency is seconds; anything beyond is device clock error, per request, no
statistics required.

# Consumers

* **`clock_skew` analyze condition** (ANALYZE-DEV-008): batches whose events carry
  `SentAt` measure the batch offset directly as `SentAt − ReceivedAt` — spool-immune by
  construction, so the spread cap and the IME/clamped exclusions are unnecessary there
  (no event timestamps are read at all; IME-only sessions become measurable). Batches
  without `SentAt` keep the legacy per-event median + spread-cap path. Downstream logic
  (stability check, plateau step detection, end-state persistence) is shared; evidence
  carries `sentAtBatchCount`.
* **`CmTraceSkewTripwire` deliberately unchanged**: `median(Δ_IME) − median(Δ_other)`
  cancels the device clock offset as common mode — send time adds nothing to that
  differential measurement.

# Constraints

* `SentAt` comes from the same device wall clock as the event timestamps — a clock jump
  between emission and send lands in the spool-delay term. That is the intended place for
  it to become visible, not a defect.
* Signals and DecisionTransitions do not carry `SentAt` (no consumer); the header is
  request-level, so extending them later is a storage-only change.
* Fleet coverage grows with agent rollout; all analyses must treat `SentAt == null`
  (legacy rows, old agents) as "legacy path", never as zero.

# Citations

* `BackendTelemetryUploader.UploadBatchAsync` — header stamping per attempt.
* `IngestTelemetryFunction.ParseSendTimeHeader` + `IngestTelemetryFunctionTests` — parse
  contract incl. the no-upper-clamp decision.
* `EventIngestProcessor.StampServerFields` + `IngestCriticalPathTests` — server-side
  stamping, forged per-event values overridden.
* `RuleEngine.ConditionEvaluators.EvaluateClockSkewCondition` + `RuleEngineClockSkewTests`
  — the two measurement paths (the backlog-alias test pair pins why SentAt exists).
* `tasks/todo.md` P14 (2026-08-21) — decision trail incl. why the tripwire stays as is.
