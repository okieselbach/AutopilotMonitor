using System.Net;
using System.Net.Http;
using AutopilotMonitor.Functions.Security;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Tests.GraphResolution;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Graph;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The maintenance run asks Entra for every tenant with a suspicion, so the state does not depend on
/// device traffic. Runs the real token service and recorder; only Entra's answer is scripted.
/// </summary>
public class GraphAccessRecheckMaintenanceTests
{
    private const string TenantId = "11111111-1111-1111-1111-111111111111";
    private const string OtherTenantId = "22222222-2222-2222-2222-222222222222";
    private const string MissingAppBody = "{\"error\":\"invalid_client\",\"error_description\":\"AADSTS7000229: missing service principal\",\"error_codes\":[7000229]}";
    private const string ConditionalAccessBody = "{\"error\":\"invalid_request\",\"error_description\":\"AADSTS53003: blocked\",\"error_codes\":[53003]}";
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 8, 0, 0, TimeSpan.Zero);
    private static readonly string TokenBody = TestGraphToken.Body(TestGraphToken.ValidationRole);

    private readonly ScriptedEntra _entra = new();
    private readonly Mock<IConfigRepository> _repo = new();
    private readonly StoredTenantConfig _store;
    private readonly List<OpsEventEntry> _events = new();
    private readonly GraphTokenService _tokens;
    private readonly GraphAccessRecheckMaintenance _sut;

    public GraphAccessRecheckMaintenanceTests()
    {
        _store = _repo.BackWithRow(ValidatedRow());
        _repo.Setup(r => r.GetAllTenantConfigurationsAsync())
            .ReturnsAsync(() => _store.Row == null
                ? new List<TenantConfiguration>()
                : new List<TenantConfiguration> { ConfigRepoMockExtensions.Clone(_store.Row) });

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
        var recorder = new GraphAccessHealthRecorder(
            tenantConfig, new Lazy<OpsEventService>(() => opsEvents), NullLogger<GraphAccessHealthRecorder>.Instance, new TestTimeProvider(Now));

        _tokens = NewTokenService(new SingleHandlerFactory(_entra), recorder, tenantConfig);
        _sut = new GraphAccessRecheckMaintenance(_repo.Object, _tokens, recorder, NullLogger<GraphAccessRecheckMaintenance>.Instance);
    }

    private static GraphTokenService NewTokenService(
        IHttpClientFactory factory, IGraphAccessHealthRecorder recorder, TenantConfigurationService tenantConfig)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EntraId:ClientId"] = "aaaaaaaa-0000-0000-0000-000000000001",
            ["EntraId:ClientSecret"] = "secret",
        }).Build();
        return new GraphTokenService(
            NullLogger<GraphTokenService>.Instance,
            factory,
            new MemoryCache(new MemoryCacheOptions()),
            new EntraAppRegistry(configuration, NullLogger<EntraAppRegistry>.Instance),
            tenantConfig,
            recorder);
    }

    private static TenantConfiguration ValidatedRow()
    {
        var row = TenantConfiguration.CreateDefault(TenantId);
        row.ValidateAutopilotDevice = true;
        return row;
    }

    private void Suspect(TimeSpan age, string code = EntraTokenRejection.ServicePrincipalMissingCode)
    {
        _store.Row!.GraphAccessRejectedSince = Now.UtcDateTime - age;
        _store.Row.GraphAccessRejectedCode = code;
    }

    [Fact]
    public async Task Confirms_an_old_suspicion_without_device_traffic()
    {
        // A real 401 goes into the retry delay, which the budget cuts: the refusal must survive it.
        Suspect(TimeSpan.FromMinutes(20));
        _entra.Answer(HttpStatusCode.Unauthorized, MissingAppBody);

        Assert.Equal(1, await _sut.RunAsync());

        Assert.Equal(Now.UtcDateTime, _store.Row!.GraphAccessRejectionConfirmedAt);
        Assert.Equal(OpsEventTypes.GraphAccessRejected, Assert.Single(_events).EventType);
    }

    [Fact]
    public async Task A_young_suspicion_stays_a_suspicion()
    {
        Suspect(TimeSpan.FromMinutes(10));
        _entra.Answer(HttpStatusCode.BadRequest, ConditionalAccessBody);

        await _sut.RunAsync();

        Assert.Null(_store.Row!.GraphAccessRejectionConfirmedAt);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task Clears_the_notice_after_a_repair_made_directly_in_Entra()
    {
        Suspect(TimeSpan.FromHours(5));
        _store.Row!.GraphAccessRejectionConfirmedAt = Now.UtcDateTime - TimeSpan.FromHours(4);
        _entra.Answer(HttpStatusCode.OK, TokenBody);

        await _sut.RunAsync();

        Assert.Null(_store.Row!.GraphAccessRejectedSince);
        Assert.Null(_store.Row.GraphAccessRejectionConfirmedAt);
        Assert.Equal(OpsEventTypes.GraphAccessRecovered, Assert.Single(_events).EventType);
    }

    [Fact]
    public async Task A_token_without_a_role_keeps_the_notice_up()
    {
        // A user's sign-in re-created the removed enterprise application without any permission.
        Suspect(TimeSpan.FromHours(5));
        _store.Row!.GraphAccessRejectionConfirmedAt = Now.UtcDateTime - TimeSpan.FromHours(4);
        _entra.Answer(HttpStatusCode.OK, TestGraphToken.Body());

        await _sut.RunAsync();

        Assert.NotNull(_store.Row!.GraphAccessRejectedSince);
        Assert.NotNull(_store.Row.GraphAccessRejectionConfirmedAt);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task A_token_without_a_role_confirms_an_old_suspicion()
    {
        Suspect(TimeSpan.FromMinutes(20));
        _entra.Answer(HttpStatusCode.OK, TestGraphToken.Body());

        await _sut.RunAsync();

        Assert.NotNull(_store.Row!.GraphAccessRejectionConfirmedAt);
        Assert.Equal("NoApplicationRole", _store.Row.GraphAccessRejectedCode);
        Assert.Equal(OpsEventTypes.GraphAccessRejected, Assert.Single(_events).EventType);
    }

    [Fact]
    public async Task A_cached_token_cannot_answer_the_question()
    {
        // The token this instance still holds stays valid after the enterprise app is gone.
        _entra.Answer(HttpStatusCode.OK, TokenBody);
        await _tokens.GetAccessTokenAsync(TenantId);
        Assert.Equal(1, _entra.Posts);

        Suspect(TimeSpan.FromMinutes(20));
        _entra.Answer(HttpStatusCode.BadRequest, ConditionalAccessBody);

        await _sut.RunAsync();

        Assert.Equal(2, _entra.Posts);
        Assert.NotNull(_store.Row!.GraphAccessRejectionConfirmedAt);
    }

    [Fact]
    public async Task No_answer_from_Entra_decides_nothing()
    {
        Suspect(TimeSpan.FromMinutes(20));
        _entra.Answer(HttpStatusCode.ServiceUnavailable, "{}");

        await _sut.RunAsync();

        Assert.NotNull(_store.Row!.GraphAccessRejectedSince);
        Assert.Null(_store.Row.GraphAccessRejectionConfirmedAt);
        Assert.Empty(_events);
    }

    [Fact]
    public async Task A_tenant_without_a_suspicion_is_not_asked()
    {
        Assert.Equal(0, await _sut.RunAsync());

        Assert.Equal(0, _entra.Posts);
    }

    [Fact]
    public async Task A_tenant_that_switched_device_validation_off_is_not_asked()
    {
        Suspect(TimeSpan.FromMinutes(20));
        _store.Row!.ValidateAutopilotDevice = false;

        Assert.Equal(0, await _sut.RunAsync());

        Assert.Equal(0, _entra.Posts);
    }

    [Fact]
    public async Task A_failing_read_ends_the_run_quietly()
    {
        _repo.Setup(r => r.GetAllTenantConfigurationsAsync()).ThrowsAsync(new InvalidOperationException("storage down"));

        Assert.Equal(0, await _sut.RunAsync());
    }

    [Fact]
    public async Task One_failing_tenant_does_not_stop_the_next()
    {
        // The stub throws on the unscripted first tenant, which the token service does not catch.
        var repo = new Mock<IConfigRepository>();
        var first = ValidatedRow();
        first.GraphAccessRejectedSince = Now.UtcDateTime;
        var second = ValidatedRow();
        second.TenantId = OtherTenantId;
        second.GraphAccessRejectedSince = Now.UtcDateTime;
        repo.Setup(r => r.GetAllTenantConfigurationsAsync()).ReturnsAsync(new List<TenantConfiguration> { first, second });
        var recorder = new Mock<IGraphAccessHealthRecorder>();
        var handler = new StubHttpMessageHandler().When($"/{OtherTenantId}/oauth2", HttpStatusCode.OK, TokenBody);
        var tenantConfig = new TenantConfigurationService(
            repo.Object, NullLogger<TenantConfigurationService>.Instance, new MemoryCache(new MemoryCacheOptions()));
        var sut = new GraphAccessRecheckMaintenance(
            repo.Object, NewTokenService(new StubHttpClientFactory(handler), recorder.Object, tenantConfig), recorder.Object,
            NullLogger<GraphAccessRecheckMaintenance>.Instance);

        Assert.Equal(2, await sut.RunAsync());

        recorder.Verify(r => r.RecordMintedAsync(OtherTenantId, It.IsAny<IReadOnlyCollection<string>>()), Times.Once);
    }

    // ── Add-on episodes ─────────────────────────────────────────────────────

    private void AddOnSuspect(TimeSpan age, TimeSpan? confirmedAgo = null, bool switchedOn = true)
    {
        _store.Row!.ValidateIntuneDeviceBinding = switchedOn;
        _store.Row.IntuneDeviceBindingPermissionMissingSince = Now.UtcDateTime - age;
        if (confirmedAgo is { } ago)
            _store.Row.IntuneDeviceBindingPermissionMissingConfirmedAt = Now.UtcDateTime - ago;
    }

    [Fact]
    public async Task Confirms_an_old_addon_suspicion_with_a_token_that_lacks_the_permission()
    {
        AddOnSuspect(TimeSpan.FromMinutes(20));
        _entra.Answer(HttpStatusCode.OK, TokenBody);

        Assert.Equal(1, await _sut.RunAsync());

        Assert.Equal(Now.UtcDateTime, _store.Row!.IntuneDeviceBindingPermissionMissingConfirmedAt);
        Assert.Equal(OpsEventTypes.AddOnPermissionMissing, Assert.Single(_events).EventType);
    }

    [Fact]
    public async Task Ends_an_addon_episode_once_the_grant_arrives()
    {
        AddOnSuspect(TimeSpan.FromHours(5), confirmedAgo: TimeSpan.FromHours(4));
        _entra.Answer(HttpStatusCode.OK,
            TestGraphToken.Body(TestGraphToken.ValidationRole, GraphAppPermissions.DeviceManagementManagedDevicesReadAll));

        await _sut.RunAsync();

        Assert.Null(_store.Row!.IntuneDeviceBindingPermissionMissingSince);
        Assert.Null(_store.Row.IntuneDeviceBindingPermissionMissingConfirmedAt);
        var resolved = Assert.Single(_events);
        Assert.Equal(OpsEventTypes.AddOnPermissionResolved, resolved.EventType);
        Assert.Contains(GraphAccessHealthRecorder.ResolvedByGrant, resolved.Details);
    }

    [Fact]
    public async Task A_switched_off_addon_ends_without_asking_Entra()
    {
        AddOnSuspect(TimeSpan.FromHours(5), confirmedAgo: TimeSpan.FromHours(4), switchedOn: false);

        Assert.Equal(1, await _sut.RunAsync());

        Assert.Equal(0, _entra.Posts);
        Assert.Null(_store.Row!.IntuneDeviceBindingPermissionMissingSince);
        var resolved = Assert.Single(_events);
        Assert.Contains(GraphAccessHealthRecorder.ResolvedBySwitchOff, resolved.Details);
    }

    /// <summary>Entra's token endpoint with an answer that can change between calls.</summary>
    private sealed class ScriptedEntra : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.OK;
        private string _body = "{}";

        public int Posts { get; private set; }

        public void Answer(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Posts++;
            return Task.FromResult(new HttpResponseMessage(_status) { Content = new StringContent(_body) });
        }
    }
}
