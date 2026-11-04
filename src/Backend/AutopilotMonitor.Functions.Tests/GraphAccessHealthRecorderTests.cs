using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Graph;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The two stages of a lost Graph access: one refusal only stamps a suspicion, a refusal at least
/// 15 minutes later confirms it and alerts once, a freshly issued token ends the episode. Runs on
/// the real configuration service over a stored row, so the storage round trip of the three fields
/// and the decision on the fresh row are part of what is proven.
/// </summary>
public class GraphAccessHealthRecorderTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private static readonly DateTimeOffset Start = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);
    private static readonly EntraTokenRejection MissingApp = new(401, EntraTokenRejection.ServicePrincipalMissingCode, "missing");
    private static readonly EntraTokenRejection ConditionalAccess = new(400, "AADSTS53003", "blocked");
    private static readonly EntraTokenRejection ExpiredSecret = new(401, "AADSTS7000222", "expired");
    private static readonly string[] CoreRoles = { GraphAppPermissions.DeviceManagementServiceConfigReadAll };
    private static readonly string[] CoreAndBindingRoles =
        { GraphAppPermissions.DeviceManagementServiceConfigReadAll, GraphAppPermissions.DeviceManagementManagedDevicesReadAll };
    private static readonly IReadOnlyList<AddOnValidation> Binding = new[] { AddOnValidation.IntuneDeviceBinding };

    private readonly Mock<IConfigRepository> _repo = new();
    private readonly StoredTenantConfig _store;
    private readonly List<OpsEventEntry> _events = new();
    private readonly TestTimeProvider _time = new(Start);
    private readonly GraphAccessHealthRecorder _sut;

    public GraphAccessHealthRecorderTests()
    {
        _store = _repo.BackWithRow(ValidatedRow());
        _sut = NewInstance();
    }

    private static TenantConfiguration ValidatedRow()
    {
        var row = TenantConfiguration.CreateDefault(TenantId);
        row.ValidateAutopilotDevice = true;
        return row;
    }

    /// <summary>A backend instance of its own: its own config cache over the same stored row.</summary>
    private GraphAccessHealthRecorder NewInstance()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var tenantConfig = new TenantConfigurationService(_repo.Object, NullLogger<TenantConfigurationService>.Instance, cache);

        var opsRepo = new Mock<IOpsEventRepository>();
        opsRepo.Setup(r => r.SaveOpsEventAsync(It.IsAny<OpsEventEntry>()))
            .Callback<OpsEventEntry>(e => _events.Add(e))
            .Returns(Task.CompletedTask);
        var adminConfig = new Mock<AdminConfigurationService>(
            _repo.Object, NullLogger<AdminConfigurationService>.Instance, cache) { CallBase = false };
        var opsEvents = new OpsEventService(
            opsRepo.Object, NullLogger<OpsEventService>.Instance, TestNotifications.InertOpsAlertDispatch(adminConfig.Object));

        return new GraphAccessHealthRecorder(
            tenantConfig, new Lazy<OpsEventService>(() => opsEvents), NullLogger<GraphAccessHealthRecorder>.Instance, _time);
    }

    private void Advance(TimeSpan by) => _time.SetUtcNow(_time.GetUtcNow() + by);

    // ── Rejected: the two stages ────────────────────────────────────────────

    [Fact]
    public async Task First_rejection_only_stamps_the_suspicion()
    {
        await _sut.RecordRejectedAsync(TenantId, MissingApp);

        Assert.Equal(Start.UtcDateTime, _store.Row!.GraphAccessRejectedSince);
        Assert.Equal(EntraTokenRejection.ServicePrincipalMissingCode, _store.Row.GraphAccessRejectedCode);
        Assert.Null(_store.Row.GraphAccessRejectionConfirmedAt);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task Further_rejections_inside_the_window_write_nothing()
    {
        await _sut.RecordRejectedAsync(TenantId, MissingApp);
        Advance(TimeSpan.FromMinutes(14));
        await _sut.RecordRejectedAsync(TenantId, MissingApp);

        Assert.Single(_store.Writes);
        Assert.Null(_store.Row!.GraphAccessRejectionConfirmedAt);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task Rejection_after_the_window_confirms_and_alerts_once()
    {
        await _sut.RecordRejectedAsync(TenantId, ConditionalAccess);
        Advance(GraphAccessHealthRecorder.ConfirmAfter);
        await _sut.RecordRejectedAsync(TenantId, MissingApp);
        Advance(TimeSpan.FromMinutes(5));
        await _sut.RecordRejectedAsync(TenantId, MissingApp);

        Assert.Equal(2, _store.Writes.Count);
        Assert.Equal(Start.UtcDateTime, _store.Row!.GraphAccessRejectedSince);
        Assert.Equal(Start.UtcDateTime + GraphAccessHealthRecorder.ConfirmAfter, _store.Row.GraphAccessRejectionConfirmedAt);
        // The code of the latest refusal counts: it selects the text and the repair.
        Assert.Equal(EntraTokenRejection.ServicePrincipalMissingCode, _store.Row.GraphAccessRejectedCode);
        var alert = Assert.Single(_events);
        Assert.Equal(OpsEventTypes.GraphAccessRejected, alert.EventType);
        Assert.Equal(OpsEventSeverity.Error, alert.Severity);
        Assert.Equal(TenantId, alert.TenantId);
        Assert.Contains(EntraTokenRejection.ServicePrincipalMissingCode, alert.Details);
    }

    [Fact]
    public async Task Across_instances_only_one_confirms_and_alerts()
    {
        var other = NewInstance();
        // The other instance holds a view from before the suspicion.
        await other.RecordRejectedAsync(TenantId, MissingApp);
        await _sut.RecordRejectedAsync(TenantId, MissingApp);
        Advance(GraphAccessHealthRecorder.ConfirmAfter);

        await _sut.RecordRejectedAsync(TenantId, MissingApp);
        await other.RecordRejectedAsync(TenantId, MissingApp);

        Assert.NotNull(_store.Row!.GraphAccessRejectionConfirmedAt);
        Assert.Single(_events);
    }

    [Fact]
    public async Task A_lost_write_alerts_nothing_and_the_next_rejection_decides_again()
    {
        await _sut.RecordRejectedAsync(TenantId, MissingApp);
        Advance(GraphAccessHealthRecorder.ConfirmAfter);

        _store.RejectWrites = true;
        await _sut.RecordRejectedAsync(TenantId, MissingApp);
        Assert.Null(_store.Row!.GraphAccessRejectionConfirmedAt);
        Assert.Empty(_events);

        _store.RejectWrites = false;
        await _sut.RecordRejectedAsync(TenantId, MissingApp);
        Assert.NotNull(_store.Row!.GraphAccessRejectionConfirmedAt);
        Assert.Single(_events);
    }

    [Fact]
    public async Task A_token_without_a_role_runs_through_the_same_two_stages()
    {
        await _sut.RecordRejectedAsync(TenantId, EntraTokenRejection.NoApplicationRole);
        Assert.Equal("NoApplicationRole", _store.Row!.GraphAccessRejectedCode);
        Assert.Empty(_events);

        Advance(GraphAccessHealthRecorder.ConfirmAfter);
        await _sut.RecordRejectedAsync(TenantId, EntraTokenRejection.NoApplicationRole);

        Assert.NotNull(_store.Row!.GraphAccessRejectionConfirmedAt);
        Assert.Equal(OpsEventTypes.GraphAccessRejected, Assert.Single(_events).EventType);
    }

    [Fact]
    public async Task A_tenant_without_device_validation_stays_untouched()
    {
        _store.Row = TenantConfiguration.CreateDefault(TenantId);

        await _sut.RecordRejectedAsync(TenantId, MissingApp);

        Assert.Empty(_store.Writes);
    }

    [Fact]
    public async Task A_tenant_without_a_row_stays_without_one()
    {
        _store.Row = null;

        await _sut.RecordRejectedAsync(TenantId, MissingApp);

        Assert.Empty(_store.Writes);
    }

    // ── Our own credentials ─────────────────────────────────────────────────

    [Fact]
    public async Task Our_expired_secret_never_touches_the_tenant_and_alerts_once_per_hour()
    {
        await _sut.RecordRejectedAsync(TenantId, ExpiredSecret);
        Advance(TimeSpan.FromMinutes(59));
        await _sut.RecordRejectedAsync(TenantId, ExpiredSecret);

        Assert.Empty(_store.Writes);
        var alert = Assert.Single(_events);
        Assert.Equal(OpsEventTypes.GraphAppCredentialRejected, alert.EventType);
        Assert.Equal(OpsEventSeverity.Critical, alert.Severity);

        Advance(TimeSpan.FromMinutes(2));
        await _sut.RecordRejectedAsync(TenantId, ExpiredSecret);

        Assert.Equal(2, _events.Count);
    }

    // ── Minted ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_minted_token_writes_nothing_without_a_state()
    {
        await _sut.RecordMintedAsync(TenantId, CoreRoles);

        Assert.Empty(_store.Writes);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task A_minted_token_clears_a_suspicion_silently()
    {
        await _sut.RecordRejectedAsync(TenantId, MissingApp);

        await _sut.RecordMintedAsync(TenantId, CoreRoles);

        Assert.Null(_store.Row!.GraphAccessRejectedSince);
        Assert.Null(_store.Row.GraphAccessRejectedCode);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task A_minted_token_ends_a_confirmed_episode_with_one_recovery_event()
    {
        await _sut.RecordRejectedAsync(TenantId, MissingApp);
        Advance(GraphAccessHealthRecorder.ConfirmAfter);
        await _sut.RecordRejectedAsync(TenantId, MissingApp);
        _events.Clear();

        await _sut.RecordMintedAsync(TenantId, CoreRoles);
        await _sut.RecordMintedAsync(TenantId, CoreRoles);

        Assert.Null(_store.Row!.GraphAccessRejectedSince);
        Assert.Null(_store.Row.GraphAccessRejectedCode);
        Assert.Null(_store.Row.GraphAccessRejectionConfirmedAt);
        var recovery = Assert.Single(_events);
        Assert.Equal(OpsEventTypes.GraphAccessRecovered, recovery.EventType);
        Assert.Equal(OpsEventSeverity.Info, recovery.Severity);
    }

    [Fact]
    public async Task After_a_recovery_a_new_episode_starts_at_the_first_stage()
    {
        await _sut.RecordRejectedAsync(TenantId, MissingApp);
        await _sut.RecordMintedAsync(TenantId, CoreRoles);
        Advance(GraphAccessHealthRecorder.ConfirmAfter);

        await _sut.RecordRejectedAsync(TenantId, MissingApp);

        Assert.Equal(_time.GetUtcNow().UtcDateTime, _store.Row!.GraphAccessRejectedSince);
        Assert.Null(_store.Row.GraphAccessRejectionConfirmedAt);
        Assert.Empty(_events);
    }

    // ── Add-on validations: the same two stages per validation ─────────────

    private void SwitchOn(bool binding = true, bool cloudPc = false, bool autopilot = true)
    {
        _store.Row!.ValidateAutopilotDevice = autopilot;
        _store.Row.ValidateIntuneDeviceBinding = binding;
        _store.Row.ValidateCloudPcDevice = cloudPc;
    }

    [Fact]
    public async Task First_device_kept_out_only_stamps_the_addon_suspicion()
    {
        SwitchOn();

        await _sut.RecordAddOnPermissionMissingAsync(TenantId, Binding);

        Assert.Equal(Start.UtcDateTime, _store.Row!.IntuneDeviceBindingPermissionMissingSince);
        Assert.Null(_store.Row.IntuneDeviceBindingPermissionMissingConfirmedAt);
        Assert.Null(_store.Row.CloudPcPermissionMissingSince);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task A_device_kept_out_after_the_window_confirms_and_alerts_once()
    {
        SwitchOn();
        await _sut.RecordAddOnPermissionMissingAsync(TenantId, Binding);
        Advance(TimeSpan.FromMinutes(14));
        await _sut.RecordAddOnPermissionMissingAsync(TenantId, Binding);
        Advance(TimeSpan.FromMinutes(1));
        await _sut.RecordAddOnPermissionMissingAsync(TenantId, Binding);
        Advance(TimeSpan.FromMinutes(5));
        await _sut.RecordAddOnPermissionMissingAsync(TenantId, Binding);

        Assert.Equal(2, _store.Writes.Count);
        Assert.Equal(Start.UtcDateTime + GraphAccessHealthRecorder.ConfirmAfter, _store.Row!.IntuneDeviceBindingPermissionMissingConfirmedAt);
        var alert = Assert.Single(_events);
        Assert.Equal(OpsEventTypes.AddOnPermissionMissing, alert.EventType);
        Assert.Equal(OpsEventSeverity.Warning, alert.Severity);
        Assert.Contains(GraphFeatureCatalog.FeatureIntuneDeviceBinding, alert.Details);
        Assert.Contains(GraphAppPermissions.DeviceManagementManagedDevicesReadAll, alert.Details);
        Assert.Contains("\"blocksAll\":false", alert.Details);
    }

    [Fact]
    public async Task Each_addon_runs_its_own_episode()
    {
        SwitchOn(binding: true, cloudPc: true);
        await _sut.RecordAddOnPermissionMissingAsync(TenantId, Binding);
        Advance(TimeSpan.FromMinutes(10));
        await _sut.RecordAddOnPermissionMissingAsync(TenantId, new[] { AddOnValidation.CloudPc });
        Advance(TimeSpan.FromMinutes(5));

        await _sut.RecordAddOnPermissionMissingAsync(TenantId, new[] { AddOnValidation.CloudPc, AddOnValidation.IntuneDeviceBinding });

        Assert.NotNull(_store.Row!.IntuneDeviceBindingPermissionMissingConfirmedAt);
        Assert.NotNull(_store.Row.CloudPcPermissionMissingSince);
        Assert.Null(_store.Row.CloudPcPermissionMissingConfirmedAt);
        Assert.Equal(OpsEventTypes.AddOnPermissionMissing, Assert.Single(_events).EventType);
    }

    [Fact]
    public async Task A_switched_off_validation_is_never_stamped()
    {
        SwitchOn(binding: false);

        await _sut.RecordAddOnPermissionMissingAsync(TenantId, Binding);

        Assert.Empty(_store.Writes);
    }

    [Fact]
    public async Task The_only_validation_reports_that_it_blocks_all()
    {
        SwitchOn(binding: true, autopilot: false);
        await _sut.RecordAddOnPermissionMissingAsync(TenantId, Binding);
        Advance(GraphAccessHealthRecorder.ConfirmAfter);

        await _sut.RecordAddOnPermissionMissingAsync(TenantId, Binding);

        Assert.Contains("\"blocksAll\":true", Assert.Single(_events).Details);
        Assert.True(AddOnValidation.BlocksAllValidation(_store.Row!));
    }

    [Fact]
    public async Task A_fresh_token_with_the_permission_ends_a_suspicion_silently()
    {
        SwitchOn();
        await _sut.RecordAddOnPermissionMissingAsync(TenantId, Binding);

        await _sut.RecordMintedAsync(TenantId, CoreAndBindingRoles);

        Assert.Null(_store.Row!.IntuneDeviceBindingPermissionMissingSince);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task A_fresh_token_with_the_permission_ends_a_confirmed_episode_with_one_event()
    {
        SwitchOn();
        await _sut.RecordAddOnPermissionMissingAsync(TenantId, Binding);
        Advance(GraphAccessHealthRecorder.ConfirmAfter);
        await _sut.RecordAddOnPermissionMissingAsync(TenantId, Binding);
        _events.Clear();

        await _sut.RecordMintedAsync(TenantId, CoreAndBindingRoles);
        await _sut.RecordMintedAsync(TenantId, CoreAndBindingRoles);

        Assert.Null(_store.Row!.IntuneDeviceBindingPermissionMissingSince);
        Assert.Null(_store.Row.IntuneDeviceBindingPermissionMissingConfirmedAt);
        var resolved = Assert.Single(_events);
        Assert.Equal(OpsEventTypes.AddOnPermissionResolved, resolved.EventType);
        Assert.Equal(OpsEventSeverity.Info, resolved.Severity);
        Assert.Contains(GraphAccessHealthRecorder.ResolvedByGrant, resolved.Details);
    }

    [Fact]
    public async Task A_fresh_token_without_the_permission_confirms_only_after_the_window()
    {
        SwitchOn();
        await _sut.RecordAddOnPermissionMissingAsync(TenantId, Binding);
        Advance(TimeSpan.FromMinutes(5));
        await _sut.RecordMintedAsync(TenantId, CoreRoles);
        Assert.Single(_store.Writes);

        Advance(TimeSpan.FromMinutes(10));
        await _sut.RecordMintedAsync(TenantId, CoreRoles);

        Assert.NotNull(_store.Row!.IntuneDeviceBindingPermissionMissingConfirmedAt);
        Assert.Equal(OpsEventTypes.AddOnPermissionMissing, Assert.Single(_events).EventType);
    }

    [Fact]
    public async Task A_fresh_token_without_an_addon_episode_writes_nothing()
    {
        SwitchOn();

        await _sut.RecordMintedAsync(TenantId, CoreRoles);

        Assert.Empty(_store.Writes);
    }

    [Fact]
    public async Task Switching_the_validation_off_ends_a_confirmed_episode_once()
    {
        SwitchOn();
        await _sut.RecordAddOnPermissionMissingAsync(TenantId, Binding);
        Advance(GraphAccessHealthRecorder.ConfirmAfter);
        await _sut.RecordAddOnPermissionMissingAsync(TenantId, Binding);
        _events.Clear();
        _store.Row!.ValidateIntuneDeviceBinding = false;

        // The maintenance run reads fresh, like another instance.
        var maintenance = NewInstance();
        await maintenance.ReconcileSwitchedOffAddOnsAsync(TenantId);
        await maintenance.ReconcileSwitchedOffAddOnsAsync(TenantId);

        Assert.Null(_store.Row.IntuneDeviceBindingPermissionMissingSince);
        Assert.Null(_store.Row.IntuneDeviceBindingPermissionMissingConfirmedAt);
        var resolved = Assert.Single(_events);
        Assert.Equal(OpsEventTypes.AddOnPermissionResolved, resolved.EventType);
        Assert.Contains(GraphAccessHealthRecorder.ResolvedBySwitchOff, resolved.Details);
    }

    [Fact]
    public async Task Reconciling_leaves_an_episode_alone_while_its_switch_is_on()
    {
        SwitchOn();
        await _sut.RecordAddOnPermissionMissingAsync(TenantId, Binding);

        await NewInstance().ReconcileSwitchedOffAddOnsAsync(TenantId);

        Assert.Single(_store.Writes);
        Assert.NotNull(_store.Row!.IntuneDeviceBindingPermissionMissingSince);
    }

    // ── Fail-soft: the agent route must not notice ──────────────────────────

    [Fact]
    public async Task A_storage_failure_never_reaches_the_caller()
    {
        _store.ThrowOnRead = new InvalidOperationException("storage down");

        await _sut.RecordRejectedAsync(TenantId, MissingApp);
        await _sut.RecordMintedAsync(TenantId, CoreRoles);
        await _sut.RecordAddOnPermissionMissingAsync(TenantId, Binding);
        await _sut.ReconcileSwitchedOffAddOnsAsync(TenantId);

        Assert.Empty(_store.Writes);
    }

    [Fact]
    public async Task A_failing_write_never_reaches_the_caller()
    {
        _store.ThrowOnWrite = new InvalidOperationException("storage down");

        await _sut.RecordRejectedAsync(TenantId, MissingApp);

        Assert.Empty(_store.Writes);
        Assert.Empty(_events);
    }
}
