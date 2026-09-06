using System;
using System.Collections.Generic;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Pins the derivation shared by the agent channel (GET agent/config) and the Global-Admin
/// report route (GET config/{tenantId}/effective-agent-config): every rule the Tenant Config
/// Report used to re-implement in the web now has exactly one implementation, and these
/// facts are its contract.
/// </summary>
public class AgentConfigResolverTests
{
    private const string TenantId = "11111111-2222-3333-4444-555555555555";
    private static readonly DateTime Now = new(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);

    private static TenantConfiguration Tenant(Action<TenantConfiguration>? mutate = null)
    {
        var c = new TenantConfiguration { TenantId = TenantId, DomainName = "contoso.example", UpdatedBy = "ga@operator.example" };
        mutate?.Invoke(c);
        return c;
    }

    private static AdminConfiguration Admin(Action<AdminConfiguration>? mutate = null)
    {
        var a = new AdminConfiguration();
        mutate?.Invoke(a);
        return a;
    }

    private static AgentConfigResponse Build(TenantConfiguration tenant, AdminConfiguration? admin = null, int agentMajor = AgentConfigResolver.CurrentAgentMajor)
        => AgentConfigResolver.Build(tenant, admin ?? Admin(), new List<GatherRule>(), new List<ImeLogPattern>(), agentMajor, Now).Response;

    [Fact]
    public void Fresh_tenant_gets_the_agent_defaults()
    {
        var r = Build(Tenant());

        Assert.Equal(AgentConfigResolver.ConfigVersion, r.ConfigVersion);
        Assert.Equal(AutopilotMonitor.Shared.Constants.DefaultUploadIntervalSeconds, r.UploadIntervalSeconds);
        Assert.Equal(10, r.UploadIntervalSeconds); // the report showed 30 for a month — pinned
        Assert.True(r.SelfDestructOnComplete);
        Assert.False(r.KeepLogFile);
        Assert.True(r.EnableGeoLocation);
        Assert.Equal(5, r.MaxAuthFailures);
        Assert.Equal(0, r.AuthFailureTimeoutMinutes);
        Assert.Equal("Info", r.LogLevel);
        Assert.Equal(10, r.RebootDelaySeconds);
        Assert.Equal(60, r.EnrollmentSummaryTimeoutSeconds);
        Assert.Equal(120, r.EnrollmentSummaryLaunchRetrySeconds);
        Assert.Equal(100, r.MaxBatchSize);
        Assert.Equal("time.windows.com", r.NtpServer);
        Assert.Equal("Off", r.DiagnosticsUploadMode);
        Assert.False(r.DiagnosticsUploadEnabled);
        Assert.False(r.UnrestrictedMode);
        Assert.False(r.DeviceBlocked);
        Assert.False(r.DeviceKillSignal);
        Assert.Null(r.UnblockAt);
        Assert.Null(r.MigrateToApiBaseUrl);

        Assert.Equal(360, r.Collectors.AgentMaxLifetimeMinutes);
        Assert.Equal(15, r.Collectors.CollectorIdleTimeoutMinutes);
        Assert.Equal(10, r.Collectors.DesktopDetectorNoCandidateTimeoutMinutes);
        Assert.Equal(new[] { 100, 1005, 1010 }, r.Collectors.ModernDeploymentHarmlessEventIds);

        Assert.True(r.Analyzers.EnableLocalAdminAnalyzer);
        Assert.Empty(r.Analyzers.LocalAdminAllowedAccounts);
        Assert.False(r.Analyzers.EnableSoftwareInventoryAnalyzer);
        Assert.True(r.Analyzers.EnableIntegrityBypassAnalyzer);
        Assert.False(r.Analyzers.EnableRealmJoinWatcher);
        Assert.False(r.Analyzers.KeepAwakeDuringUserEsp);
        Assert.True(r.Analyzers.EnableConsoleBypassDetection);
    }

    [Theory]
    [InlineData(null, "CustomerSas", "Off", false)]
    [InlineData(null, "Hosted", "Off", true)]        // hosted destination needs no SAS
    [InlineData("https://acct.blob.core.windows.net/c?sv=1", "CustomerSas", "Off", true)] // SAS enables regardless of mode
    public void Diagnostics_upload_enabled_follows_sas_or_hosted_not_the_mode(string? sas, string destination, string mode, bool expected)
    {
        var r = Build(Tenant(t =>
        {
            t.DiagnosticsBlobSasUrl = sas!;
            t.DiagnosticsUploadDestination = destination;
            t.DiagnosticsUploadMode = mode;
        }));
        Assert.Equal(expected, r.DiagnosticsUploadEnabled);
    }

    [Theory]
    [InlineData("free", true, true, false)]  // Community: entitlement missing
    [InlineData("pro", false, true, false)]  // GA gate off
    [InlineData("pro", true, false, false)]  // tenant toggle off
    [InlineData("pro", true, true, true)]
    public void Unrestricted_mode_needs_entitlement_and_gate_and_toggle(string planTier, bool gaGate, bool toggle, bool expected)
    {
        var r = Build(Tenant(t =>
        {
            t.PlanTier = planTier;
            t.UnrestrictedModeEnabled = gaGate;
            t.UnrestrictedMode = toggle;
        }));
        Assert.Equal(expected, r.UnrestrictedMode);
    }

    [Fact]
    public void Operator_knobs_come_from_the_admin_configuration()
    {
        var admin = Admin(a =>
        {
            a.CollectorIdleTimeoutMinutes = 25;
            a.DesktopDetectorNoCandidateTimeoutMinutes = 7;
            a.AllowAgentDowngrade = true;
            a.ModernDeploymentHarmlessEventIdsJson = "[100, 42]";
            a.LatestAgentV2Sha256 = "zip-v2";
            a.LatestAgentV2ExeSha256 = "exe-v2";
        });

        var r = Build(Tenant(), admin);

        Assert.Equal(25, r.Collectors.CollectorIdleTimeoutMinutes);
        Assert.Equal(7, r.Collectors.DesktopDetectorNoCandidateTimeoutMinutes);
        Assert.True(r.AllowAgentDowngrade);
        Assert.Equal(new[] { 100, 42 }, r.Collectors.ModernDeploymentHarmlessEventIds);
        Assert.Equal("zip-v2", r.LatestAgentSha256);
        Assert.Equal("exe-v2", r.LatestAgentExeSha256);
    }

    [Fact]
    public void Legacy_major_gets_empty_hashes_the_current_major_gets_v2()
    {
        var admin = Admin(a => { a.LatestAgentV2Sha256 = "zip-v2"; a.LatestAgentV2ExeSha256 = "exe-v2"; });

        var legacy = Build(Tenant(), admin, agentMajor: 1);
        Assert.Equal(string.Empty, legacy.LatestAgentSha256);

        var current = Build(Tenant(), admin, agentMajor: AgentConfigResolver.CurrentAgentMajor);
        Assert.Equal("zip-v2", current.LatestAgentSha256);
    }

    [Fact]
    public void Tenant_overrides_win_over_defaults()
    {
        var r = Build(Tenant(t =>
        {
            t.MaxBatchSize = 25;
            t.LogLevel = "Debug";
            t.NtpServer = "";
            t.LocalAdminAllowedAccountsJson = "[\"Admin\",\"Helpdesk\"]";
            t.AgentMaxLifetimeMinutes = 120;
            t.EnableConsoleBypassDetection = false;
        }));

        Assert.Equal(25, r.MaxBatchSize);
        Assert.Equal("Debug", r.LogLevel);
        Assert.Equal("time.windows.com", r.NtpServer); // empty string falls back like null
        Assert.Equal(new[] { "Admin", "Helpdesk" }, r.Analyzers.LocalAdminAllowedAccounts);
        Assert.Equal(120, r.Collectors.AgentMaxLifetimeMinutes);
        Assert.False(r.Analyzers.EnableConsoleBypassDetection);
    }

    [Fact]
    public void Migration_target_is_served_when_valid_and_reported_when_rejected()
    {
        const string Us = "https://autopilotmonitor-api-us.azurewebsites.net";
        var served = AgentConfigResolver.Build(Tenant(), Admin(a => a.AgentMigrateApiBaseUrl = Us),
            new List<GatherRule>(), new List<ImeLogPattern>(), AgentConfigResolver.CurrentAgentMajor, Now);
        Assert.Equal(Us, served.Response.MigrateToApiBaseUrl);
        Assert.Null(served.RejectedMigrateCandidate);

        var rejected = AgentConfigResolver.Build(Tenant(), Admin(a => a.AgentMigrateApiBaseUrl = "https://evil.example"),
            new List<GatherRule>(), new List<ImeLogPattern>(), AgentConfigResolver.CurrentAgentMajor, Now);
        Assert.Null(rejected.Response.MigrateToApiBaseUrl);
        Assert.Equal("https://evil.example", rejected.RejectedMigrateCandidate);
    }

    [Fact]
    public void Catalogs_pass_through_unchanged()
    {
        var rules = new List<GatherRule> { new() { RuleId = "GATHER-TEST-001" } };
        var patterns = new List<ImeLogPattern> { new() { PatternId = "IME-TEST-001" } };
        var r = AgentConfigResolver.Build(Tenant(), Admin(), rules, patterns, AgentConfigResolver.CurrentAgentMajor, Now).Response;
        Assert.Same(rules, r.GatherRules);
        Assert.Same(patterns, r.ImeLogPatterns);
    }
}
