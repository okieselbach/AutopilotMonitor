using System;
using System.Collections.Generic;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Agent.V2.Core.Logging;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Configuration
{
    /// <summary>
    /// Tests for <see cref="RemoteConfigMerger"/>. This merger is the single source of
    /// V1-parity: every field delivered by the backend that has a live V2 consumer must land
    /// on <see cref="AgentConfiguration"/> after <c>FetchConfigAsync</c>.
    /// <para>
    /// V1 parity: remote values OVERWRITE the runtime configuration unconditionally. CLI
    /// flags only seed the initial <see cref="AgentConfiguration"/> and yield to tenant
    /// policy once Merge runs. This is asserted by a regression test below.
    /// </para>
    /// </summary>
    public sealed class RemoteConfigMergerTests
    {
        private static AgentConfiguration NewAgentConfig() => new AgentConfiguration
        {
            ApiBaseUrl = "https://example",
            TenantId = "t",
            SessionId = "s",
        };

        private static AgentConfigResponse FullRemote() => new AgentConfigResponse
        {
            ConfigVersion = 26,
            UploadIntervalSeconds = 45,
            MaxBatchSize = 250,
            SelfDestructOnComplete = false,
            KeepLogFile = true,
            EnableGeoLocation = false,
            RebootOnComplete = true,
            RebootDelaySeconds = 30,
            ShowEnrollmentSummary = true,
            EnrollmentSummaryTimeoutSeconds = 90,
            EnrollmentSummaryBrandingImageUrl = "https://cdn.example/logo.png",
            EnrollmentSummaryLaunchRetrySeconds = 300,
            NtpServer = "pool.ntp.org",
            EnableTimezoneAutoSet = true,
            EnableDoGroupIdAutoSet = true,
            DiagnosticsUploadEnabled = true,
            DiagnosticsUploadMode = "OnFailure",
            DiagnosticsLogPaths = new List<DiagnosticsLogPath>
            {
                new DiagnosticsLogPath { Path = @"%ProgramData%\Foo\*.log", IsBuiltIn = false },
            },
            LogLevel = "Debug",
            SendTraceEvents = false,
            UnrestrictedMode = true,
            MaxAuthFailures = 7,
            AuthFailureTimeoutMinutes = 15,
            EnableImeMatchLog = true,
            Collectors = new CollectorConfiguration
            {
                AgentMaxLifetimeMinutes = 120,
                HelloWaitTimeoutSeconds = 45,
            },
        };

        // ============================================================= Happy path

        [Fact]
        public void Merge_applies_all_tenant_fields()
        {
            var agent = NewAgentConfig();
            var remote = FullRemote();

            var result = RemoteConfigMerger.Merge(agent, remote);

            Assert.False(agent.SelfDestructOnComplete);
            Assert.True(agent.KeepLogFile);
            Assert.False(agent.EnableGeoLocation);
            Assert.True(agent.RebootOnComplete);
            Assert.Equal(30, agent.RebootDelaySeconds);
            Assert.True(agent.ShowEnrollmentSummary);
            Assert.Equal(90, agent.EnrollmentSummaryTimeoutSeconds);
            Assert.Equal("https://cdn.example/logo.png", agent.EnrollmentSummaryBrandingImageUrl);
            Assert.Equal(300, agent.EnrollmentSummaryLaunchRetrySeconds);
            Assert.Equal("pool.ntp.org", agent.NtpServer);
            Assert.True(agent.EnableTimezoneAutoSet);
            Assert.True(agent.EnableDoGroupIdAutoSet);
            Assert.True(agent.DiagnosticsUploadEnabled);
            Assert.Equal("OnFailure", agent.DiagnosticsUploadMode);
            Assert.Single(agent.DiagnosticsLogPaths);
            Assert.Equal(@"%ProgramData%\Foo\*.log", agent.DiagnosticsLogPaths[0].Path);
            Assert.Equal(AgentLogLevel.Debug, agent.LogLevel);
            Assert.False(agent.SendTraceEvents);
            Assert.True(agent.UnrestrictedMode);
            Assert.Equal(45, agent.UploadIntervalSeconds);
            Assert.Equal(250, agent.MaxBatchSize);
            Assert.Equal(120, agent.AgentMaxLifetimeMinutes);
            Assert.Equal(45, agent.HelloWaitTimeoutSeconds);
            Assert.Equal(7, agent.MaxAuthFailures);
            Assert.Equal(15, agent.AuthFailureTimeoutMinutes);

            Assert.NotNull(result);
            Assert.True(result.UnrestrictedModeChanged);
            Assert.False(result.OldUnrestrictedMode);
            Assert.True(result.NewUnrestrictedMode);
            Assert.True(result.LogLevelChanged);
            Assert.Equal(AgentLogLevel.Info, result.OldLogLevel);
            Assert.Equal(AgentLogLevel.Debug, result.NewLogLevel);
        }

        // ============================================================= Remote-wins-always (V1 parity)

        [Fact]
        public void Merge_remote_wins_over_prior_agent_self_destruct()
        {
            // BuildAgentConfiguration may have set SelfDestructOnComplete from the --no-cleanup CLI
            // flag. Once remote config arrives it takes over — V1 behaviour (operator cannot
            // override tenant policy from the command line).
            var agent = NewAgentConfig();
            agent.SelfDestructOnComplete = false;

            var remote = FullRemote();
            remote.SelfDestructOnComplete = true;

            RemoteConfigMerger.Merge(agent, remote);

            Assert.True(agent.SelfDestructOnComplete);
        }

        [Fact]
        public void Merge_remote_wins_over_prior_agent_reboot_on_complete()
        {
            var agent = NewAgentConfig();
            agent.RebootOnComplete = true;

            var remote = FullRemote();
            remote.RebootOnComplete = false;

            RemoteConfigMerger.Merge(agent, remote);

            Assert.False(agent.RebootOnComplete);
        }

        // ============================================================= LogLevel parsing

        [Theory]
        // Valid levels (Info/Debug/Verbose/Trace) — remote wins, case-insensitive TryParse
        // (RemoteConfigMerger.cs:59).
        [InlineData("Debug", AgentLogLevel.Verbose, AgentLogLevel.Debug)]
        [InlineData("trace", AgentLogLevel.Info, AgentLogLevel.Trace)]
        [InlineData("VERBOSE", AgentLogLevel.Info, AgentLogLevel.Verbose)]
        [InlineData("Info", AgentLogLevel.Trace, AgentLogLevel.Info)]
        // Invalid / empty / whitespace / null → IsNullOrWhiteSpace or TryParse-fail → keeps prior.
        [InlineData("NotAValidLevel", AgentLogLevel.Info, AgentLogLevel.Info)]
        [InlineData("Warning", AgentLogLevel.Info, AgentLogLevel.Info)] // not in enum → keeps
        [InlineData("", AgentLogLevel.Info, AgentLogLevel.Info)]
        [InlineData("   ", AgentLogLevel.Info, AgentLogLevel.Info)]
        [InlineData(null, AgentLogLevel.Info, AgentLogLevel.Info)]
        public void Merge_log_level_applies_valid_values_and_ignores_invalid_ones(
            string? remoteLogLevel, AgentLogLevel priorLevel, AgentLogLevel expectedLevel)
        {
            var agent = NewAgentConfig();
            agent.LogLevel = priorLevel;

            var remote = FullRemote();
            remote.LogLevel = remoteLogLevel!;

            var result = RemoteConfigMerger.Merge(agent, remote);

            Assert.Equal(expectedLevel, agent.LogLevel);
            Assert.Equal(priorLevel != expectedLevel, result.LogLevelChanged);
            Assert.Equal(priorLevel, result.OldLogLevel);
            Assert.Equal(expectedLevel, result.NewLogLevel);
        }

        [Fact]
        public void Merge_remote_null_ntp_server_clears_prior_value()
        {
            // V1 parity: remote null overwrites local without a IsNullOrWhiteSpace gate.
            var agent = NewAgentConfig();
            agent.NtpServer = "local.ntp";

            var remote = FullRemote();
            remote.NtpServer = null!;

            RemoteConfigMerger.Merge(agent, remote);

            Assert.Null(agent.NtpServer);
        }

        [Fact]
        public void Merge_remote_off_overrides_prior_diagnostics_upload_mode()
        {
            // Tenant disables diagnostics upload — must take effect even when the agent was
            // previously configured (e.g. by a prior config fetch cached on disk) to something
            // else. V1 parity.
            var agent = NewAgentConfig();
            agent.DiagnosticsUploadMode = "OnFailure";
            agent.DiagnosticsUploadEnabled = true;

            var remote = FullRemote();
            remote.DiagnosticsUploadMode = "Off";
            remote.DiagnosticsUploadEnabled = false;

            RemoteConfigMerger.Merge(agent, remote);

            Assert.Equal("Off", agent.DiagnosticsUploadMode);
            Assert.False(agent.DiagnosticsUploadEnabled);
        }

        [Fact]
        public void Merge_unrestricted_mode_change_is_reported_via_result()
        {
            var agent = NewAgentConfig();
            agent.UnrestrictedMode = false;

            var remote = FullRemote();
            remote.UnrestrictedMode = true;

            var result = RemoteConfigMerger.Merge(agent, remote);

            Assert.True(result.UnrestrictedModeChanged);
            Assert.False(result.OldUnrestrictedMode);
            Assert.True(result.NewUnrestrictedMode);
            Assert.True(agent.UnrestrictedMode);
        }

        [Fact]
        public void Merge_unrestricted_mode_stable_is_not_reported_as_change()
        {
            var agent = NewAgentConfig();
            agent.UnrestrictedMode = true;

            var remote = FullRemote();
            remote.UnrestrictedMode = true;

            var result = RemoteConfigMerger.Merge(agent, remote);

            Assert.False(result.UnrestrictedModeChanged);
        }

        [Fact]
        public void Merge_maps_enable_ime_match_log_true_to_default_path()
        {
            var agent = NewAgentConfig();
            agent.ImeMatchLogPath = null;

            var remote = FullRemote();
            remote.EnableImeMatchLog = true;

            RemoteConfigMerger.Merge(agent, remote);

            Assert.False(string.IsNullOrEmpty(agent.ImeMatchLogPath));
            Assert.Equal(
                Environment.ExpandEnvironmentVariables(Constants.ImeMatchLogPath),
                agent.ImeMatchLogPath);
        }

        [Fact]
        public void Merge_maps_enable_ime_match_log_false_clears_path()
        {
            var agent = NewAgentConfig();
            agent.ImeMatchLogPath = Environment.ExpandEnvironmentVariables(Constants.ImeMatchLogPath);

            var remote = FullRemote();
            remote.EnableImeMatchLog = false;

            RemoteConfigMerger.Merge(agent, remote);

            Assert.Null(agent.ImeMatchLogPath);
        }

        [Fact]
        public void Merge_preserves_custom_ime_match_log_path_when_remote_disables()
        {
            // A dev-time --ime-match-log <custom> override seeds a non-default path on the
            // AgentConfiguration. When the tenant remote flips EnableImeMatchLog off we keep
            // the custom path rather than clobbering a deliberate dev override.
            var agent = NewAgentConfig();
            agent.ImeMatchLogPath = @"C:\Temp\custom-ime.log";

            var remote = FullRemote();
            remote.EnableImeMatchLog = false;

            RemoteConfigMerger.Merge(agent, remote);

            Assert.Equal(@"C:\Temp\custom-ime.log", agent.ImeMatchLogPath);
        }

        [Fact]
        public void Merge_maps_enable_gather_rule_debug_log_true_to_default_path()
        {
            var agent = NewAgentConfig();
            agent.GatherRuleDebugLogPath = null;

            var remote = FullRemote();
            remote.EnableGatherRuleDebugLog = true;

            RemoteConfigMerger.Merge(agent, remote);

            Assert.False(string.IsNullOrEmpty(agent.GatherRuleDebugLogPath));
            Assert.Equal(
                Environment.ExpandEnvironmentVariables(Constants.GatherRuleDebugLogPath),
                agent.GatherRuleDebugLogPath);
        }

        [Fact]
        public void Merge_maps_enable_gather_rule_debug_log_false_clears_path()
        {
            var agent = NewAgentConfig();
            agent.GatherRuleDebugLogPath = Environment.ExpandEnvironmentVariables(Constants.GatherRuleDebugLogPath);

            var remote = FullRemote();
            remote.EnableGatherRuleDebugLog = false;

            RemoteConfigMerger.Merge(agent, remote);

            Assert.Null(agent.GatherRuleDebugLogPath);
        }

        [Fact]
        public void Merge_preserves_custom_gather_rule_debug_log_path_when_remote_disables()
        {
            // A dev-time --gather-debug-log <custom> override seeds a non-default path on the
            // AgentConfiguration. When the tenant remote flips EnableGatherRuleDebugLog off we
            // keep the custom path rather than clobbering a deliberate dev override.
            var agent = NewAgentConfig();
            agent.GatherRuleDebugLogPath = @"C:\Temp\custom-gather.log";

            var remote = FullRemote();
            remote.EnableGatherRuleDebugLog = false;

            RemoteConfigMerger.Merge(agent, remote);

            Assert.Equal(@"C:\Temp\custom-gather.log", agent.GatherRuleDebugLogPath);
        }

        // ============================================================= Null / partial tolerance

        [Fact]
        public void Merge_ignores_null_remote_config()
        {
            var agent = NewAgentConfig();
            agent.NtpServer = "local.ntp";
            agent.SelfDestructOnComplete = true;

            var result = RemoteConfigMerger.Merge(agent, null!);

            Assert.Equal("local.ntp", agent.NtpServer);
            Assert.True(agent.SelfDestructOnComplete);
            Assert.NotNull(result);
            Assert.False(result.UnrestrictedModeChanged);
        }

        [Fact]
        public void Merge_tolerates_null_nested_collectors()
        {
            var agent = NewAgentConfig();
            agent.AgentMaxLifetimeMinutes = 360;
            agent.HelloWaitTimeoutSeconds = 30;

            var remote = FullRemote();
            remote.Collectors = null!;

            RemoteConfigMerger.Merge(agent, remote);

            Assert.Equal(360, agent.AgentMaxLifetimeMinutes);
            Assert.Equal(30, agent.HelloWaitTimeoutSeconds);
        }

        [Fact]
        public void Merge_replaces_diagnostics_log_paths_with_empty_list_when_remote_null()
        {
            var agent = NewAgentConfig();
            agent.DiagnosticsLogPaths = new List<DiagnosticsLogPath>
            {
                new DiagnosticsLogPath { Path = "stale", IsBuiltIn = false },
            };

            var remote = FullRemote();
            remote.DiagnosticsLogPaths = null!;

            RemoteConfigMerger.Merge(agent, remote);

            Assert.NotNull(agent.DiagnosticsLogPaths);
            Assert.Empty(agent.DiagnosticsLogPaths);
        }

        // ============================================================= Int clamping

        [Fact]
        public void Merge_skips_non_positive_max_batch_size()
        {
            var agent = NewAgentConfig();
            agent.MaxBatchSize = 100;

            var remote = FullRemote();
            remote.MaxBatchSize = -1;

            RemoteConfigMerger.Merge(agent, remote);

            Assert.Equal(100, agent.MaxBatchSize);
        }

        [Fact]
        public void Merge_skips_non_positive_hello_wait_timeout()
        {
            // V1 parity: MonitoringService.ApplyRuntimeSettingsFromRemoteConfig only applies the
            // remote value when > 0 so an accidentally misconfigured tenant does not drop the
            // Hello-wait to zero.
            var agent = NewAgentConfig();
            agent.HelloWaitTimeoutSeconds = 30;

            var remote = FullRemote();
            remote.Collectors = new CollectorConfiguration
            {
                AgentMaxLifetimeMinutes = 360,
                HelloWaitTimeoutSeconds = 0,
            };

            RemoteConfigMerger.Merge(agent, remote);

            Assert.Equal(30, agent.HelloWaitTimeoutSeconds);
        }

        [Fact]
        public void Merge_allows_zero_agent_max_lifetime_to_disable_watchdog()
        {
            var agent = NewAgentConfig();
            agent.AgentMaxLifetimeMinutes = 360;

            var remote = FullRemote();
            remote.Collectors = new CollectorConfiguration
            {
                AgentMaxLifetimeMinutes = 0, // explicit "disabled"
                HelloWaitTimeoutSeconds = 30,
            };

            RemoteConfigMerger.Merge(agent, remote);

            Assert.Equal(0, agent.AgentMaxLifetimeMinutes);
        }

        // ============================================================= Guard

        [Fact]
        public void Merge_throws_on_null_agent_config()
        {
            Assert.Throws<ArgumentNullException>(() =>
                RemoteConfigMerger.Merge(null!, FullRemote()));
        }
    }
}
