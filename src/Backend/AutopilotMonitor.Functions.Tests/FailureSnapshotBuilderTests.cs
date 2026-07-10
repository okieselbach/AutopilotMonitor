using System;
using System.Collections.Generic;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Newtonsoft.Json.Linq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Tests for the failure-state snapshot built when the maintenance 5h-timeout sweep
/// graduates a session to Failed (Hybrid User-Driven completion-gap fix, 2026-05-01).
/// The snapshot is the single point of post-mortem context for stuck sessions, so the
/// shape contract has to be stable: schemaVersion, missingSignals list, and the lifecycle
/// anchors all need to round-trip cleanly. Tests focus on the diagnostic value: would an
/// operator looking at the snapshot for session e58bcfdb-… understand what went wrong?
/// </summary>
public class FailureSnapshotBuilderTests
{
    private static readonly DateTime Now = new DateTime(2026, 5, 1, 18, 45, 31, DateTimeKind.Utc);

    private static EnrollmentEvent Event(string type, DateTime ts, string? message = null,
        Dictionary<string, object>? data = null) => new()
        {
            EventType = type,
            Timestamp = ts,
            Source = "test",
            Message = message ?? type,
            Data = data!,
        };

    [Fact]
    public void Build_returns_null_for_empty_event_list()
    {
        Assert.Null(FailureSnapshotBuilder.Build(null, Now));
        Assert.Null(FailureSnapshotBuilder.Build(Array.Empty<EnrollmentEvent>(), Now));
    }

    [Fact]
    public void Build_carries_schemaVersion_and_generation_metadata()
    {
        var json = FailureSnapshotBuilder.Build(new[]
        {
            Event("agent_started", Now.AddHours(-5)),
        }, Now);

        Assert.NotNull(json);
        var obj = JObject.Parse(json!);
        Assert.Equal(FailureSnapshotBuilder.CurrentSchemaVersion, (int)obj["schemaVersion"]!);
        Assert.NotNull(obj["generatedAtUtc"]);
        Assert.Equal(1, (int)obj["eventCount"]!);
    }

    [Fact]
    public void Build_extracts_last_esp_phase_from_esp_phase_changed_events()
    {
        var t0 = Now.AddHours(-5);
        var json = FailureSnapshotBuilder.Build(new[]
        {
            Event("esp_phase_changed", t0.AddSeconds(1),
                data: new Dictionary<string, object> { ["espPhase"] = "DeviceSetup" }),
            Event("esp_phase_changed", t0.AddSeconds(3),
                data: new Dictionary<string, object> { ["espPhase"] = "AccountSetup" }),
        }, Now);

        var obj = JObject.Parse(json!);
        Assert.Equal("AccountSetup", (string?)obj["lastEspPhase"]);
        Assert.NotNull(obj["lastEspPhaseAtUtc"]);
    }

    [Fact]
    public void Build_records_desktop_arrival_with_timestamp()
    {
        var t = Now.AddHours(-5).AddSeconds(7);
        var json = FailureSnapshotBuilder.Build(new[]
        {
            Event("desktop_arrived", t),
        }, Now);

        var obj = JObject.Parse(json!);
        Assert.True((bool)obj["desktopArrived"]!);
        Assert.NotNull(obj["desktopArrivedAtUtc"]);
    }

    [Fact]
    public void Build_records_hello_policy_disabled_state()
    {
        var json = FailureSnapshotBuilder.Build(new[]
        {
            Event("hello_policy_detected", Now.AddHours(-4),
                data: new Dictionary<string, object> { ["helloEnabled"] = "false" }),
        }, Now);

        var obj = JObject.Parse(json!);
        Assert.True((bool)obj["helloPolicyDetected"]!);
        Assert.False((bool)obj["helloPolicyEnabled"]!);
    }

    // ============================================================================
    // AAD join-state classification
    // ============================================================================

    [Fact]
    public void Build_classifies_placeholder_state_from_aad_placeholder_event()
    {
        var json = FailureSnapshotBuilder.Build(new[]
        {
            Event("aad_placeholder_user_detected", Now.AddHours(-4),
                data: new Dictionary<string, object> { ["placeholderType"] = "foouser" }),
        }, Now);

        var obj = JObject.Parse(json!);
        Assert.Equal("placeholder", (string?)obj["aadJoinState"]);
    }

    [Theory]
    // V2 since 2026-05-04 emits the renamed event...
    [InlineData("aad_user_joined_observed")]
    // ...but historical data + V1 agents still emit the legacy name. Snapshot must classify both.
    [InlineData("aad_user_joined_late")]
    public void Build_classifies_real_user_state_from_real_user_join_event(string eventType)
    {
        var json = FailureSnapshotBuilder.Build(new[]
        {
            Event(eventType, Now.AddHours(-3)),
        }, Now);

        var obj = JObject.Parse(json!);
        Assert.Equal("real_user", (string?)obj["aadJoinState"]);
    }

    [Theory]
    [InlineData("aad_user_joined_observed")]  // V2 canonical since 2026-05-04
    [InlineData("aad_user_joined_late")]      // V1 + historical data
    public void Build_aad_user_join_slot_is_satisfied_by_part_1_event(string eventType)
    {
        // Codex review 2026-05-01 (Finding 1): the dual-emitted informational event
        // is what makes this work. Before the fix, AadJoinWatcherAdapter only posted
        // a DecisionSignal — HandleAadUserJoinedLateV1 emits no timeline effect, so
        // the Events table never held a real-user-join row. The snapshot then always
        // marked it missing AND classified aadJoinState as placeholder/unknown even
        // on healthy real-user joins. Both legacy and renamed wire strings must work.
        var json = FailureSnapshotBuilder.Build(new[]
        {
            Event(eventType, Now.AddHours(-3)),
        }, Now);

        var obj = JObject.Parse(json!);
        var missing = obj["missingSignals"]!.ToObject<List<string>>()!;
        Assert.DoesNotContain("aad_user_join", missing);              // slot satisfied
        Assert.DoesNotContain("aad_user_joined_observed", missing);   // literal must never appear
        Assert.DoesNotContain("aad_user_joined_late", missing);       // legacy literal either
    }

    [Fact]
    public void Build_aad_user_join_slot_is_satisfied_by_part_2_event()
    {
        // Codex review 2026-05-01 (Finding 4): Part 2 (post-reboot user sign-in) emits
        // user_aad_signin_complete instead of aad_user_joined_late. Both must satisfy
        // the same conceptual slot — analog to hello_terminal admitting any of the
        // four Hello terminal event types. Without the new branch, every healthy Part-2
        // session would falsely list aad_user_joined_late as missing.
        var json = FailureSnapshotBuilder.Build(new[]
        {
            Event("user_aad_signin_complete", Now.AddHours(-3)),
        }, Now);

        var obj = JObject.Parse(json!);
        Assert.Equal("real_user", (string?)obj["aadJoinState"]);

        var missing = obj["missingSignals"]!.ToObject<List<string>>()!;
        Assert.DoesNotContain("aad_user_join", missing);              // slot satisfied
        Assert.DoesNotContain("aad_user_joined_observed", missing);   // literal must never appear
        Assert.DoesNotContain("aad_user_joined_late", missing);       // legacy literal either
        Assert.DoesNotContain("user_aad_signin_complete", missing);   // ditto
    }

    [Fact]
    public void Build_aad_user_join_slot_is_listed_missing_when_neither_part_event_present()
    {
        // The trigger session e58bcfdb saw no real-user join at all — the slot must
        // be reported missing. Bare event list (just agent_started) keeps the test
        // focused on the slot logic; the e58bcfdb-replay test below covers the
        // realistic full anchor set.
        var json = FailureSnapshotBuilder.Build(new[]
        {
            Event("agent_started", Now.AddHours(-5)),
        }, Now);

        var obj = JObject.Parse(json!);
        var missing = obj["missingSignals"]!.ToObject<List<string>>()!;
        Assert.Contains("aad_user_join", missing);
    }

    [Fact]
    public void Build_falls_back_to_aad_join_status_isFooUser_when_only_that_is_present()
    {
        var json = FailureSnapshotBuilder.Build(new[]
        {
            Event("aad_join_status", Now.AddHours(-4),
                data: new Dictionary<string, object> { ["isFooUser"] = "true" }),
        }, Now);

        var obj = JObject.Parse(json!);
        Assert.Equal("placeholder", (string?)obj["aadJoinState"]);
    }

    [Fact]
    public void Build_real_user_event_overrides_earlier_placeholder()
    {
        // The temporal pattern in Hybrid User-Driven: foo desktop appears (placeholder),
        // reboot, real user signs in (real_user). Snapshot should report the LATEST state.
        var json = FailureSnapshotBuilder.Build(new[]
        {
            Event("aad_placeholder_user_detected", Now.AddHours(-5)),
            Event("aad_user_joined_late", Now.AddHours(-3)),
        }, Now);

        var obj = JObject.Parse(json!);
        Assert.Equal("real_user", (string?)obj["aadJoinState"]);
    }

    // ============================================================================
    // Reboot + Hybrid context
    // ============================================================================

    [Fact]
    public void Build_records_reboot_observation_and_hybrid_flag()
    {
        var json = FailureSnapshotBuilder.Build(new[]
        {
            Event("autopilot_profile", Now.AddHours(-5),
                data: new Dictionary<string, object> { ["isHybridJoin"] = "true", ["enrollmentType"] = "v1" }),
            Event("system_reboot_detected", Now.AddHours(-4)),
        }, Now);

        var obj = JObject.Parse(json!);
        Assert.True((bool)obj["rebootObserved"]!);
        Assert.True((bool)obj["isHybridJoin"]!);
        Assert.Equal("v1", (string?)obj["enrollmentType"]);
    }

    // ============================================================================
    // Missing-signal computation — the diagnostic core of the snapshot
    // ============================================================================

    [Fact]
    public void Build_lists_missing_signals_for_e58bcfdb_style_session()
    {
        // Replays the canonical anchors from session e58bcfdb-… (the trigger session for
        // this fix). Confirms the snapshot would have surfaced exactly the gaps we
        // identified in the diagnosis: no esp_exiting, no hello terminal, no
        // aad_user_joined_late, no enrollment_complete.
        var t0 = new DateTime(2026, 5, 1, 13, 45, 31, DateTimeKind.Utc);
        var nowAtTimeout = t0.AddHours(5);

        var events = new[]
        {
            Event("agent_started", t0),
            Event("autopilot_profile", t0.AddSeconds(1),
                data: new Dictionary<string, object> { ["isHybridJoin"] = "true", ["enrollmentType"] = "v1" }),
            Event("esp_phase_changed", t0.AddSeconds(2),
                data: new Dictionary<string, object> { ["espPhase"] = "DeviceSetup" }),
            Event("aad_join_status", t0.AddSeconds(2),
                data: new Dictionary<string, object> { ["isFooUser"] = "true" }),
            Event("esp_phase_changed", t0.AddSeconds(3),
                data: new Dictionary<string, object> { ["espPhase"] = "AccountSetup" }),
            Event("desktop_arrived", t0.AddSeconds(6)),
            Event("system_reboot_detected", t0.AddMinutes(25).AddSeconds(53)),
        };

        var json = FailureSnapshotBuilder.Build(events, nowAtTimeout);
        var obj = JObject.Parse(json!);

        var missing = obj["missingSignals"]!.ToObject<List<string>>()!;
        Assert.Contains("esp_exiting", missing);
        Assert.Contains("hello_policy_detected", missing);
        Assert.Contains("hello_terminal", missing);
        Assert.Contains("aad_user_join", missing);  // conceptual slot — Part 1 OR Part 2
        Assert.Contains("enrollment_complete", missing);

        // Things the session DID see should NOT be in the missing list.
        Assert.DoesNotContain("esp_phase_changed", missing);
        Assert.DoesNotContain("desktop_arrived", missing);

        // Slot label is the conceptual name; the literal Part 1 / Part 2 event-type
        // names must NOT appear directly in missingSignals (that would double-count).
        Assert.DoesNotContain("aad_user_joined_observed", missing);
        Assert.DoesNotContain("aad_user_joined_late", missing);
        Assert.DoesNotContain("user_aad_signin_complete", missing);

        // High-level diagnostic fields render correctly.
        Assert.Equal("AccountSetup", (string?)obj["lastEspPhase"]);
        Assert.True((bool)obj["desktopArrived"]!);
        Assert.True((bool)obj["rebootObserved"]!);
        Assert.True((bool)obj["isHybridJoin"]!);
        Assert.Equal("placeholder", (string?)obj["aadJoinState"]);
    }

    [Fact]
    public void Build_treats_any_hello_terminal_as_satisfying_the_slot()
    {
        // hello_terminal is the conceptual slot — any of {completed, failed, blocked, skipped}
        // satisfies it. Tested with hello_skipped which is the most common Hybrid path.
        var json = FailureSnapshotBuilder.Build(new[]
        {
            Event("hello_skipped", Now.AddHours(-4)),
        }, Now);

        var obj = JObject.Parse(json!);
        var missing = obj["missingSignals"]!.ToObject<List<string>>()!;
        Assert.DoesNotContain("hello_terminal", missing);
    }

    [Fact]
    public void Build_records_user_completion_evidence_for_reconcile_rule()
    {
        // Schema v3 (session 294ab5b4): helloResolved + realmJoin* are the evidence behind the
        // "user completed setup" reconcile verdict — the snapshot must show operators the same
        // facts the classifier used.
        var json = FailureSnapshotBuilder.Build(new[]
        {
            Event("desktop_arrived", Now.AddHours(-5)),
            Event("hello_provisioning_completed", Now.AddHours(-5).AddMinutes(6)),
            Event("realmjoin_detected", Now.AddHours(-5).AddMinutes(1)),
        }, Now);

        var obj = JObject.Parse(json!);
        Assert.True((bool)obj["helloResolved"]!);
        Assert.True((bool)obj["realmJoinDetected"]!);
        Assert.False((bool)obj["realmJoinResolved"]!);
    }

    // ============================================================================
    // Timing / stale silence
    // ============================================================================

    [Fact]
    public void Build_computes_silenceMinutes_from_last_event_to_now()
    {
        var t = Now.AddHours(-3); // last event was 3h ago
        var json = FailureSnapshotBuilder.Build(new[] { Event("agent_started", t) }, Now);

        var obj = JObject.Parse(json!);
        var silence = (int)obj["silenceMinutes"]!;
        Assert.InRange(silence, 179, 181);
    }

    [Fact]
    public void Build_handles_unsorted_input_via_secondary_sort()
    {
        // The contract says events come sorted, but the builder defensively re-sorts.
        var t0 = Now.AddHours(-5);
        var json = FailureSnapshotBuilder.Build(new[]
        {
            Event("esp_phase_changed", t0.AddSeconds(3),
                data: new Dictionary<string, object> { ["espPhase"] = "AccountSetup" }),
            Event("esp_phase_changed", t0.AddSeconds(1),
                data: new Dictionary<string, object> { ["espPhase"] = "DeviceSetup" }),
        }, Now);

        var obj = JObject.Parse(json!);
        Assert.Equal("AccountSetup", (string?)obj["lastEspPhase"]);
    }

    [Fact]
    public void Build_tolerates_events_with_null_or_missing_data()
    {
        // Events stored before the Data field was added still have to parse cleanly.
        var json = FailureSnapshotBuilder.Build(new[]
        {
            new EnrollmentEvent { EventType = "agent_started", Timestamp = Now.AddHours(-5), Source = "test", Data = null! },
        }, Now);

        Assert.NotNull(json);
        var obj = JObject.Parse(json!);
        Assert.Equal(1, (int)obj["eventCount"]!);
    }
}
