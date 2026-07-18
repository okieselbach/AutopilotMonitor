using AutopilotMonitor.Agent.V2.Core.Configuration;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Configuration
{
    /// <summary>
    /// Pins the agent-side endpoint-migration contract (<see cref="EndpointMigration"/>):
    /// only a LIVE fetch is honoured, the target must pass the shared allowlist rules even
    /// though the backend already validated it, and serving the agent its own current URL is
    /// a no-op — the exact mirror of the kill-switch live-fetch hardening.
    /// </summary>
    public sealed class EndpointMigrationTests
    {
        private const string CurrentUrl = "https://autopilotmonitor-api-eu.azurewebsites.net";
        private const string TargetUrl = "https://autopilotmonitor-api-us.azurewebsites.net";

        private static AgentConfigResponse Config(string migrateUrl) =>
            new AgentConfigResponse { MigrateToApiBaseUrl = migrateUrl };

        [Fact]
        public void Live_fetch_with_valid_differing_target_migrates()
        {
            var target = EndpointMigration.ResolveTarget(
                Config(TargetUrl), RemoteConfigFetchOutcome.Succeeded, CurrentUrl, logger: null);

            Assert.Equal(TargetUrl, target);
        }

        [Fact]
        public void Target_is_normalized_to_lowercase_host_without_trailing_slash()
        {
            var target = EndpointMigration.ResolveTarget(
                Config("https://AutopilotMonitor-API-US.azurewebsites.net/"),
                RemoteConfigFetchOutcome.Succeeded, CurrentUrl, logger: null);

            Assert.Equal(TargetUrl, target);
        }

        [Theory]
        [InlineData(RemoteConfigFetchOutcome.FromCache)]
        [InlineData(RemoteConfigFetchOutcome.UsedDefaults)]
        [InlineData(RemoteConfigFetchOutcome.NotAttempted)]
        public void Non_live_fetch_is_never_honoured(RemoteConfigFetchOutcome outcome)
        {
            Assert.Null(EndpointMigration.ResolveTarget(
                Config(TargetUrl), outcome, CurrentUrl, logger: null));
        }

        [Fact]
        public void Null_config_and_empty_signal_are_noops()
        {
            Assert.Null(EndpointMigration.ResolveTarget(
                null, RemoteConfigFetchOutcome.Succeeded, CurrentUrl, logger: null));
            Assert.Null(EndpointMigration.ResolveTarget(
                Config(null), RemoteConfigFetchOutcome.Succeeded, CurrentUrl, logger: null));
            Assert.Null(EndpointMigration.ResolveTarget(
                Config("   "), RemoteConfigFetchOutcome.Succeeded, CurrentUrl, logger: null));
        }

        [Fact]
        public void Target_equal_to_current_url_is_a_noop()
        {
            // Steady state during a migration window: agent already runs on the target
            // (case/trailing-slash differences must not cause a rebuild loop).
            Assert.Null(EndpointMigration.ResolveTarget(
                Config(CurrentUrl), RemoteConfigFetchOutcome.Succeeded, CurrentUrl, logger: null));
            Assert.Null(EndpointMigration.ResolveTarget(
                Config(CurrentUrl.ToUpperInvariant() + "/"), RemoteConfigFetchOutcome.Succeeded, CurrentUrl, logger: null));
        }

        [Theory]
        [InlineData("http://autopilotmonitor-api-us.azurewebsites.net")]        // https only
        [InlineData("https://attacker.example.com")]                             // host not allowlisted
        [InlineData("https://evil-azurewebsites.net")]                           // suffix must sit on a label boundary
        [InlineData("https://.azurewebsites.net")]                               // empty host prefix
        [InlineData("https://api.azurewebsites.net:8443")]                       // non-default port
        [InlineData("https://user:pw@api.azurewebsites.net")]                    // userinfo
        [InlineData("https://api.azurewebsites.net/some/path")]                  // base URL only
        [InlineData("https://api.azurewebsites.net?x=1")]                        // no query
        [InlineData("not a url")]
        public void Invalid_targets_are_rejected(string candidate)
        {
            Assert.Null(EndpointMigration.ResolveTarget(
                Config(candidate), RemoteConfigFetchOutcome.Succeeded, CurrentUrl, logger: null));
        }

        [Fact]
        public void Dev_api_url_override_still_migrates_to_valid_target()
        {
            // The running base URL may be a non-allowlisted dev override (--api-url); the
            // comparison falls back to plain normalization and the migration still applies.
            var target = EndpointMigration.ResolveTarget(
                Config(TargetUrl), RemoteConfigFetchOutcome.Succeeded, "http://localhost:7071", logger: null);

            Assert.Equal(TargetUrl, target);
        }
    }
}
