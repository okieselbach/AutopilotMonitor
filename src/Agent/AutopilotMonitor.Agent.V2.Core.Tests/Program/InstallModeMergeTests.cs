using AutopilotMonitor.Agent.V2;
using AutopilotMonitor.Agent.V2.Core.Configuration;
using Xunit;

namespace AutopilotMonitor.Agent.V2.Core.Tests.Program
{
    /// <summary>
    /// Pinpoints the <c>--install</c> bootstrap-config merge semantics. The earlier
    /// implementation overwrote the file from CLI args alone, so a redeploy with only
    /// <c>--tenant-id-wait</c> wiped a previously-persisted BootstrapToken / TenantId
    /// and pushed devices to cert-auth before their cert was usable. The merge now
    /// preserves prior fields when their corresponding CLI flag is absent on this
    /// invocation, and honours an explicit value (including empty / 0) when present.
    /// </summary>
    public sealed class InstallModeMergeTests
    {
        [Fact]
        public void Wait_only_redeploy_preserves_existing_token_and_tenantid()
        {
            var existing = new BootstrapConfigFile
            {
                BootstrapToken = "tok-abc",
                TenantId = "tenant-xyz",
                TenantIdWaitSeconds = 0,
            };

            var merged = AutopilotMonitor.Agent.V2.Program.MergeBootstrapConfig(
                existing,
                bootstrapTokenArg: null!, bootstrapTokenGiven: false,
                tenantIdArg: null!, tenantIdGiven: false,
                tenantIdWaitSeconds: 600, tenantIdWaitGiven: true);

            Assert.Equal("tok-abc", merged.BootstrapToken);
            Assert.Equal("tenant-xyz", merged.TenantId);
            Assert.Equal(600, merged.TenantIdWaitSeconds);
        }

        [Fact]
        public void Explicit_zero_wait_overrides_existing_nonzero()
        {
            var existing = new BootstrapConfigFile
            {
                BootstrapToken = "tok-abc",
                TenantId = "tenant-xyz",
                TenantIdWaitSeconds = 600,
            };

            var merged = AutopilotMonitor.Agent.V2.Program.MergeBootstrapConfig(
                existing,
                bootstrapTokenArg: null!, bootstrapTokenGiven: false,
                tenantIdArg: null!, tenantIdGiven: false,
                tenantIdWaitSeconds: 0, tenantIdWaitGiven: true);

            // Token + tenantId untouched, wait explicitly disabled.
            Assert.Equal("tok-abc", merged.BootstrapToken);
            Assert.Equal("tenant-xyz", merged.TenantId);
            Assert.Equal(0, merged.TenantIdWaitSeconds);
        }

        [Fact]
        public void Tenant_only_redeploy_preserves_existing_wait()
        {
            var existing = new BootstrapConfigFile
            {
                BootstrapToken = "tok-abc",
                TenantId = "tenant-old",
                TenantIdWaitSeconds = 1500,
            };

            var merged = AutopilotMonitor.Agent.V2.Program.MergeBootstrapConfig(
                existing,
                bootstrapTokenArg: null!, bootstrapTokenGiven: false,
                tenantIdArg: "tenant-new", tenantIdGiven: true,
                tenantIdWaitSeconds: 0, tenantIdWaitGiven: false);

            Assert.Equal("tok-abc", merged.BootstrapToken);
            Assert.Equal("tenant-new", merged.TenantId);
            Assert.Equal(1500, merged.TenantIdWaitSeconds);
        }

        [Fact]
        public void Token_only_redeploy_preserves_existing_wait_and_tenant()
        {
            var existing = new BootstrapConfigFile
            {
                BootstrapToken = "tok-old",
                TenantId = "tenant-xyz",
                TenantIdWaitSeconds = 300,
            };

            var merged = AutopilotMonitor.Agent.V2.Program.MergeBootstrapConfig(
                existing,
                bootstrapTokenArg: "tok-new", bootstrapTokenGiven: true,
                tenantIdArg: null!, tenantIdGiven: false,
                tenantIdWaitSeconds: 0, tenantIdWaitGiven: false);

            Assert.Equal("tok-new", merged.BootstrapToken);
            Assert.Equal("tenant-xyz", merged.TenantId);
            Assert.Equal(300, merged.TenantIdWaitSeconds);
        }

        [Fact]
        public void First_install_with_no_existing_config_uses_args_only()
        {
            var merged = AutopilotMonitor.Agent.V2.Program.MergeBootstrapConfig(
                existing: null,
                bootstrapTokenArg: "tok-fresh", bootstrapTokenGiven: true,
                tenantIdArg: "tenant-fresh", tenantIdGiven: true,
                tenantIdWaitSeconds: 600, tenantIdWaitGiven: true);

            Assert.Equal("tok-fresh", merged.BootstrapToken);
            Assert.Equal("tenant-fresh", merged.TenantId);
            Assert.Equal(600, merged.TenantIdWaitSeconds);
        }

        [Fact]
        public void First_install_with_only_wait_arg_writes_null_token_and_tenant()
        {
            // PS1's default invocation: agent --install --tenant-id-wait 600
            // No existing config, no token, no tenantId. Result: nullable fields stay null,
            // wait gets the new value. Subsequent runtime config-load falls back to the
            // registry resolver for TenantId — that's the OOBE happy path.
            var merged = AutopilotMonitor.Agent.V2.Program.MergeBootstrapConfig(
                existing: null,
                bootstrapTokenArg: null!, bootstrapTokenGiven: false,
                tenantIdArg: null!, tenantIdGiven: false,
                tenantIdWaitSeconds: 600, tenantIdWaitGiven: true);

            Assert.Null(merged.BootstrapToken);
            Assert.Null(merged.TenantId);
            Assert.Equal(600, merged.TenantIdWaitSeconds);
        }

        [Fact]
        public void First_install_without_wait_arg_uses_agent_default_of_600()
        {
            // Generic bootstrap path: PS1 calls `--install` without --tenant-id-wait,
            // and there is no persisted config yet. Agent owns the default (600 s).
            var merged = AutopilotMonitor.Agent.V2.Program.MergeBootstrapConfig(
                existing: null,
                bootstrapTokenArg: null!, bootstrapTokenGiven: false,
                tenantIdArg: null!, tenantIdGiven: false,
                tenantIdWaitSeconds: 0, tenantIdWaitGiven: false);

            Assert.Null(merged.BootstrapToken);
            Assert.Null(merged.TenantId);
            Assert.Equal(600, merged.TenantIdWaitSeconds);
        }

        [Fact]
        public void Existing_config_without_wait_field_treated_as_zero()
        {
            // BackCompat: pre-fix bootstrap-config.json has no TenantIdWaitSeconds field;
            // Newtonsoft deserialises that to 0. New install with --tenant-id-wait 600
            // overrides cleanly; no extra migration logic required.
            var existing = new BootstrapConfigFile
            {
                BootstrapToken = "tok-legacy",
                TenantId = "tenant-legacy",
                TenantIdWaitSeconds = 0,
            };

            var merged = AutopilotMonitor.Agent.V2.Program.MergeBootstrapConfig(
                existing,
                bootstrapTokenArg: null!, bootstrapTokenGiven: false,
                tenantIdArg: null!, tenantIdGiven: false,
                tenantIdWaitSeconds: 600, tenantIdWaitGiven: true);

            Assert.Equal("tok-legacy", merged.BootstrapToken);
            Assert.Equal("tenant-legacy", merged.TenantId);
            Assert.Equal(600, merged.TenantIdWaitSeconds);
        }
    }
}
