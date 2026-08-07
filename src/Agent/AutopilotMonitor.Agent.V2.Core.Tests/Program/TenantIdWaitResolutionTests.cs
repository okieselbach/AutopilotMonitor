using AutopilotMonitor.Agent.V2;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Program
{
    /// <summary>
    /// Pins the TenantIdWaitSeconds resolution order in
    /// <see cref="AutopilotMonitor.Agent.V2.Program.BuildAgentConfiguration"/>:
    /// CLI arg &gt; persisted bootstrap-config.json value &gt; agent default (600 s).
    /// The regression this guards: a legacy bootstrap-config.json written before the
    /// TenantIdWaitSeconds property existed deserialises to null — that must fall
    /// through to the 600 s default, NOT be treated as an explicit 0 opt-out
    /// (which silently disabled the wait and broke Hybrid-AAD-join devices whose
    /// registry TenantId lands minutes after first boot).
    /// </summary>
    public sealed class TenantIdWaitResolutionTests
    {
        private static AgentConfiguration Build(BootstrapConfigFile bootstrapConfig, params string[] args) =>
            AutopilotMonitor.Agent.V2.Program.BuildAgentConfiguration(
                args: args,
                tenantId: "tenant-t",
                sessionId: "session-s",
                bootstrapConfig: bootstrapConfig,
                awaitConfig: null);

        [Fact]
        public void No_cli_and_no_bootstrap_config_uses_default_600()
        {
            var cfg = Build(bootstrapConfig: null);

            Assert.Equal(600, cfg.TenantIdWaitSeconds);
        }

        [Fact]
        public void Legacy_bootstrap_config_with_null_wait_uses_default_600()
        {
            var legacy = new BootstrapConfigFile
            {
                BootstrapToken = "tok-legacy",
                TenantId = "tenant-legacy",
                TenantIdWaitSeconds = null,
            };

            var cfg = Build(legacy);

            Assert.Equal(600, cfg.TenantIdWaitSeconds);
        }

        [Fact]
        public void Explicit_zero_in_bootstrap_config_disables_wait()
        {
            var optedOut = new BootstrapConfigFile { TenantIdWaitSeconds = 0 };

            var cfg = Build(optedOut);

            Assert.Equal(0, cfg.TenantIdWaitSeconds);
        }

        [Fact]
        public void Nonzero_bootstrap_config_value_wins_over_default()
        {
            var configured = new BootstrapConfigFile { TenantIdWaitSeconds = 1800 };

            var cfg = Build(configured);

            Assert.Equal(1800, cfg.TenantIdWaitSeconds);
        }

        [Fact]
        public void Cli_arg_wins_over_bootstrap_config_value()
        {
            var configured = new BootstrapConfigFile { TenantIdWaitSeconds = 1800 };

            var cfg = Build(configured, "--tenant-id-wait", "300");

            Assert.Equal(300, cfg.TenantIdWaitSeconds);
        }

        [Fact]
        public void Cli_zero_wins_over_bootstrap_config_value()
        {
            var configured = new BootstrapConfigFile { TenantIdWaitSeconds = 1800 };

            var cfg = Build(configured, "--tenant-id-wait", "0");

            Assert.Equal(0, cfg.TenantIdWaitSeconds);
        }
    }
}
