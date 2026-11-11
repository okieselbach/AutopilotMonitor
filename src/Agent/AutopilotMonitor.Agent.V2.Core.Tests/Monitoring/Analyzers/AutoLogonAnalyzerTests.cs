#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Agent.V2.Core.Monitoring.Telemetry.Analyzers;
using AutopilotMonitor.Agent.V2.Core.Orchestration;
using AutopilotMonitor.Agent.V2.Core.Tests.Harness;
using AutopilotMonitor.Agent.V2.Core.Tests.Orchestration;
using AutopilotMonitor.Shared;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Monitoring.Analyzers
{
    /// <summary>
    /// AutoLogonAnalyzer — pure classification (BuildPayload) plus emission contract. The analyzer
    /// reports raw Winlogon facts at Info severity; the escalation grading lives in backend
    /// analyze-rules (ANALYZE-SEC-003 plaintext password), so these tests assert facts, not severity
    /// judgements. AutoLogon-enabled alone is deliberately ungraded (it matches Windows' own ESP
    /// auto-logon on every normal Autopilot enrollment).
    /// </summary>
    public sealed class AutoLogonAnalyzerTests
    {
        private static AgentLogger NewLogger(string dir) => new AgentLogger(dir, AgentLogLevel.Info);

        private static Dictionary<string, object> Checks(Dictionary<string, object> payload)
            => (Dictionary<string, object>)payload["checks"];

        // -------------------------------------------------------------- BuildPayload classification

        [Fact]
        public void BuildPayload_empty_snapshot_reports_no_autologon()
        {
            var data = AutoLogonAnalyzer.BuildPayload(new AutoLogonSnapshot(), "shutdown");

            Assert.Equal("no_autologon", data["finding"]);
            Assert.Equal("info", data["severity"]);
            Assert.Equal("shutdown", data["triggered_at"]);
            Assert.Empty((List<string>)data["findings"]);

            var checks = Checks(data);
            Assert.False((bool)checks["autologon_enabled"]);
            Assert.False((bool)checks["default_password_present"]);
            Assert.False((bool)checks["default_identity_present"]);
        }

        [Fact]
        public void BuildPayload_auto_admin_logon_set_marks_autologon_active()
        {
            var snap = new AutoLogonSnapshot { WinlogonKeyPresent = true, AutoAdminLogon = "1" };
            var data = AutoLogonAnalyzer.BuildPayload(snap, "device_setup_complete");

            Assert.Equal("autologon_active", data["finding"]);
            Assert.Equal("device_setup_complete", data["triggered_at"]);
            Assert.Contains("auto_admin_logon_enabled", (List<string>)data["findings"]);

            var checks = Checks(data);
            Assert.True((bool)checks["auto_admin_logon_enabled"]);
            Assert.True((bool)checks["autologon_enabled"]);
        }

        [Fact]
        public void BuildPayload_force_auto_logon_alone_enables_autologon()
        {
            var snap = new AutoLogonSnapshot { WinlogonKeyPresent = true, ForceAutoLogon = "1" };
            var data = AutoLogonAnalyzer.BuildPayload(snap, "shutdown");

            var checks = Checks(data);
            Assert.False((bool)checks["auto_admin_logon_enabled"]);
            Assert.True((bool)checks["force_auto_logon_enabled"]);
            Assert.True((bool)checks["autologon_enabled"]);
            Assert.Equal("autologon_active", data["finding"]);
        }

        [Fact]
        public void BuildPayload_plaintext_password_is_primary_finding_and_value_never_emitted()
        {
            var snap = new AutoLogonSnapshot
            {
                WinlogonKeyPresent = true,
                AutoAdminLogon = "1",
                DefaultUserName = "kioskuser",
                DefaultPasswordPresent = true,
            };
            var data = AutoLogonAnalyzer.BuildPayload(snap, "shutdown");

            Assert.Equal("plaintext_password_present", data["finding"]);
            Assert.Contains("default_password_present", (List<string>)data["findings"]);

            var checks = Checks(data);
            Assert.True((bool)checks["default_password_present"]);
            // Presence-only: the password value must never appear anywhere in the payload.
            Assert.False(checks.ContainsKey("default_password"));
            Assert.DoesNotContain("DefaultPassword", checks.Keys);
        }

        [Fact]
        public void BuildPayload_enabled_user_no_password_does_not_flag_sysinternals()
        {
            // Regression guard: "AutoLogon enabled + a default user + no registry password" is the
            // exact fingerprint of Windows' own ESP auto-logon on a normal Autopilot enrollment.
            // The old sysinternals heuristic flagged it on every device — it must be gone now: no
            // sysinternals check, no sysinternals finding, just the factual autologon_active label.
            var snap = new AutoLogonSnapshot
            {
                WinlogonKeyPresent = true,
                AutoAdminLogon = "1",
                DefaultUserName = "luke.skywalker@example.net",
                DefaultPasswordPresent = false,
            };
            var data = AutoLogonAnalyzer.BuildPayload(snap, "device_setup_complete");

            var checks = Checks(data);
            Assert.DoesNotContain("sysinternals_autologon_suspected", checks.Keys);
            Assert.DoesNotContain("sysinternals_autologon_suspected", (List<string>)data["findings"]);
            Assert.Equal("autologon_active", data["finding"]);
        }

        [Fact]
        public void BuildPayload_default_identity_only_is_benign_baseline()
        {
            var snap = new AutoLogonSnapshot
            {
                WinlogonKeyPresent = true,
                DefaultUserName = "alice",
                DefaultDomainName = "CONTOSO",
            };
            var data = AutoLogonAnalyzer.BuildPayload(snap, "shutdown");

            Assert.Equal("default_identity_present", data["finding"]);
            var checks = Checks(data);
            Assert.False((bool)checks["autologon_enabled"]);
            Assert.True((bool)checks["default_identity_present"]);
            Assert.Equal("alice", checks["default_user_name"]);
            Assert.Equal("CONTOSO", checks["default_domain_name"]);
        }

        [Fact]
        public void BuildPayload_reports_a_sign_in_address_as_its_domain_only()
        {
            var snap = new AutoLogonSnapshot
            {
                WinlogonKeyPresent = true,
                AutoAdminLogon = "1",
                DefaultUserName = "luke.skywalker@example.net",
                AltDefaultUserName = @"AzureAD\luke.skywalker@example.net",
            };
            var data = AutoLogonAnalyzer.BuildPayload(snap, "shutdown");

            var checks = Checks(data);
            Assert.Equal("[redacted]@example.net", checks["default_user_name"]);
            Assert.Equal("[redacted]@example.net", checks["alt_default_user_name"]);
            Assert.DoesNotContain(checks.Values.OfType<string>(), v => v.Contains("skywalker"));
            // The reduction is presentation only — the identity is still reported as present.
            Assert.True((bool)checks["default_identity_present"]);
        }

        [Theory]
        [InlineData("kioskuser", "kioskuser")]
        [InlineData("defaultuser0", "defaultuser0")]
        [InlineData("foouser@example.net", "foouser@example.net")]
        [InlineData("autopilot@example.net", "autopilot@example.net")]
        [InlineData("luke.skywalker@example.net", "[redacted]@example.net")]
        [InlineData("luke@skywalker@example.net", "[redacted]@example.net")]
        [InlineData("luke.skywalker@", "[redacted]@unknown")]
        [InlineData("", "")]
        [InlineData(null, "")]
        public void ReduceUserName_keeps_local_accounts_and_placeholders_readable(string? raw, string expected)
        {
            Assert.Equal(expected, AutoLogonAnalyzer.ReduceUserName(raw));
        }

        [Theory]
        [InlineData("1", true)]
        [InlineData("true", true)]
        [InlineData("TRUE", true)]
        [InlineData("0", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void BuildPayload_interprets_auto_admin_logon_flag(string? raw, bool expectedEnabled)
        {
            var snap = new AutoLogonSnapshot { WinlogonKeyPresent = true, AutoAdminLogon = raw };
            var data = AutoLogonAnalyzer.BuildPayload(snap, "shutdown");
            Assert.Equal(expectedEnabled, (bool)Checks(data)["auto_admin_logon_enabled"]);
        }

        // -------------------------------------------------------------- Emission contract

        [Fact]
        public void RunScan_via_override_emits_single_info_autologon_event_with_trigger()
        {
            using var tmp = new TempDirectory();
            var sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(sink, new VirtualClock(new DateTime(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc)));
            var sut = new AutoLogonAnalyzer("S1", "T1", post, NewLogger(tmp.Path))
            {
                SnapshotOverride = new AutoLogonSnapshot { WinlogonKeyPresent = true, AutoAdminLogon = "1", DefaultUserName = "kioskuser" },
            };

            sut.AnalyzeAtDeviceSetupComplete();

            var events = sink.Posted.Where(p => p.Payload != null
                && p.Payload.TryGetValue("eventType", out var et) && et == Constants.EventTypes.AutoLogonAnalysis).ToList();
            Assert.Single(events);

            var data = Assert.IsType<Dictionary<string, object>>(events[0].TypedPayload);
            Assert.Equal("device_setup_complete", data["triggered_at"]);
            Assert.Equal("info", data["severity"]);
            Assert.Equal("autologon_active", data["finding"]);
        }

        [Fact]
        public void AnalyzeAtStartup_is_a_no_op_and_emits_nothing()
        {
            using var tmp = new TempDirectory();
            var sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(sink, new VirtualClock(new DateTime(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc)));
            var sut = new AutoLogonAnalyzer("S1", "T1", post, NewLogger(tmp.Path))
            {
                SnapshotOverride = new AutoLogonSnapshot { WinlogonKeyPresent = true, AutoAdminLogon = "1" },
            };

            sut.AnalyzeAtStartup();

            Assert.Empty(sink.Posted);
        }

        [Fact]
        public void AnalyzeAtShutdown_reads_real_registry_fail_soft_and_emits_one_event()
        {
            using var tmp = new TempDirectory();
            var sink = new FakeSignalIngressSink();
            var post = new InformationalEventPost(sink, new VirtualClock(new DateTime(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc)));
            var sut = new AutoLogonAnalyzer("S1", "T1", post, NewLogger(tmp.Path));

            var ex = Record.Exception(() => sut.AnalyzeAtShutdown());
            Assert.Null(ex);

            var events = sink.Posted.Where(p => p.Payload != null
                && p.Payload.TryGetValue("eventType", out var et) && et == Constants.EventTypes.AutoLogonAnalysis).ToList();
            Assert.Single(events);
            var data = Assert.IsType<Dictionary<string, object>>(events[0].TypedPayload);
            Assert.Equal("shutdown", data["triggered_at"]);
        }

        [Fact]
        public void Ctor_rejects_null_required_dependencies()
        {
            using var tmp = new TempDirectory();
            var logger = NewLogger(tmp.Path);
            var post = new InformationalEventPost(new FakeSignalIngressSink(), new VirtualClock(new DateTime(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc)));
            Assert.Throws<ArgumentNullException>(() => new AutoLogonAnalyzer(null!, "T1", post, logger));
            Assert.Throws<ArgumentNullException>(() => new AutoLogonAnalyzer("S1", null!, post, logger));
            Assert.Throws<ArgumentNullException>(() => new AutoLogonAnalyzer("S1", "T1", null!, logger));
            Assert.Throws<ArgumentNullException>(() => new AutoLogonAnalyzer("S1", "T1", post, null!));
        }
    }
}
