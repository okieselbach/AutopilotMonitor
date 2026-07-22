using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Functions.Config;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// ContactEmail is the address enforcement actions and service notices are sent to.
/// The seed must never overwrite a value the tenant owns — that is the whole contract.
/// </summary>
public class TenantContactEmailTests
{
    private const string TenantId = "77777777-7777-7777-7777-777777777777";

    [Fact]
    public void ContactEmail_defaults_to_null_so_absence_is_distinguishable_from_empty()
    {
        var config = new TenantConfiguration();
        Assert.Null(config.ContactEmail);
    }

    [Fact]
    public void ContactEmail_survives_a_round_trip_through_the_model()
    {
        var config = new TenantConfiguration { ContactEmail = "it-operations@contoso.com" };
        Assert.Equal("it-operations@contoso.com", config.ContactEmail);
    }

    // ------------------------------------------------------------------
    // Server-side validation (UpdateTenantConfigurationFunction)
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("it-operations@contoso.com")]
    [InlineData("first.last+autopilot@sub.fabrikam.co.uk")]
    [InlineData("UPPER@CONTOSO.COM")]
    public void ValidateContactEmail_accepts_real_addresses(string email)
        => Assert.Null(UpdateTenantConfigurationFunction.ValidateContactEmail(email));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateContactEmail_treats_absence_as_valid(string? email)
    {
        // No contact address is a legitimate state — it means we cannot reach this tenant.
        Assert.Null(UpdateTenantConfigurationFunction.ValidateContactEmail(email));
    }

    [Theory]
    [InlineData("not-an-address", "single")]              // no @ at all
    [InlineData("@contoso.com", "single")]                // empty local part
    [InlineData("ops@", "single")]                        // empty domain
    [InlineData("ops@@contoso.com", "single")]            // two @
    [InlineData("ops@localhost", "dotted")]               // unreachable bare host
    [InlineData("ops@.contoso.com", "dotted")]            // leading dot
    [InlineData("ops@contoso.", "dotted")]                // trailing dot
    [InlineData("ops@contoso.com, attacker@evil.test", "single address")] // recipient list
    [InlineData("ops@contoso.com; attacker@evil.test", "single address")]
    [InlineData("Ops Team <ops@contoso.com>", "single address")]          // display-name form
    public void ValidateContactEmail_rejects_values_that_are_not_a_single_address(string email, string expectedHint)
    {
        var error = UpdateTenantConfigurationFunction.ValidateContactEmail(email);
        Assert.NotNull(error);
        Assert.Contains(expectedHint, error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateContactEmail_rejects_header_injection()
    {
        // Once this address is actually mailed, an embedded CR/LF would forge mail headers.
        var error = UpdateTenantConfigurationFunction.ValidateContactEmail(
            $"ops@contoso.com{(char)13}{(char)10}Bcc: attacker@evil.test");

        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateContactEmail_rejects_an_over_long_value()
    {
        var local = new string('a', UpdateTenantConfigurationFunction.MaxContactEmailLength);
        var error = UpdateTenantConfigurationFunction.ValidateContactEmail($"{local}@contoso.com");

        Assert.NotNull(error);
        Assert.Contains("at most", error!);
    }

    // ------------------------------------------------------------------
    // Seed contract (TenantConfigurationService — the single owner)
    // ------------------------------------------------------------------

    private static (TenantConfigurationService service, Mock<IConfigRepository> repo, MemoryCache cache) BuildService()
    {
        var repo = new Mock<IConfigRepository>();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new TenantConfigurationService(
            repo.Object, NullLogger<TenantConfigurationService>.Instance, cache);
        return (service, repo, cache);
    }

    [Fact]
    public async Task TrySeedContactEmail_when_the_write_lands_reports_success_and_drops_the_cached_row()
    {
        var (service, repo, cache) = BuildService();
        repo.Setup(r => r.TrySeedTenantContactEmailAsync(TenantId, "ops@contoso.com")).ReturnsAsync(true);
        cache.Set($"tenant-config:{TenantId}", new TenantConfiguration { TenantId = TenantId });

        Assert.True(await service.TrySeedContactEmailAsync(TenantId, "ops@contoso.com"));

        // The repository writes behind this cache; without the eviction the freshly seeded
        // address would stay invisible for the full 5-minute TTL.
        Assert.False(cache.TryGetValue($"tenant-config:{TenantId}", out _));
    }

    [Fact]
    public async Task TrySeedContactEmail_when_the_tenant_already_owns_an_address_leaves_the_cache_alone()
    {
        var (service, repo, cache) = BuildService();
        repo.Setup(r => r.TrySeedTenantContactEmailAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(false);
        var cached = new TenantConfiguration { TenantId = TenantId, ContactEmail = "owner@contoso.com" };
        cache.Set($"tenant-config:{TenantId}", cached);

        Assert.False(await service.TrySeedContactEmailAsync(TenantId, "ops@contoso.com"));

        Assert.True(cache.TryGetValue($"tenant-config:{TenantId}", out TenantConfiguration? still));
        Assert.Equal("owner@contoso.com", still!.ContactEmail);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task TrySeedContactEmail_with_no_address_never_reaches_storage(string? email)
    {
        var (service, repo, _) = BuildService();

        Assert.False(await service.TrySeedContactEmailAsync(TenantId, email));

        repo.Verify(r => r.TrySeedTenantContactEmailAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task TrySeedContactEmail_with_no_tenant_never_reaches_storage()
    {
        var (service, repo, _) = BuildService();

        Assert.False(await service.TrySeedContactEmailAsync("  ", "ops@contoso.com"));

        repo.Verify(r => r.TrySeedTenantContactEmailAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task TrySeedContactEmail_trims_before_it_reaches_storage()
    {
        var (service, repo, _) = BuildService();
        repo.Setup(r => r.TrySeedTenantContactEmailAsync(TenantId, "ops@contoso.com")).ReturnsAsync(true);

        Assert.True(await service.TrySeedContactEmailAsync(TenantId, "  ops@contoso.com  "));

        repo.Verify(r => r.TrySeedTenantContactEmailAsync(TenantId, "ops@contoso.com"), Times.Once);
    }

    [Fact]
    public async Task TrySeedContactEmail_never_writes_the_whole_configuration()
    {
        // Regression guard: the seed used to be a read-modify-write of the full model through
        // SaveTenantConfigurationAsync, which replaces the row unconditionally — a concurrent
        // portal save was clobbered by the seeder's stale snapshot of EVERY field.
        var (service, repo, _) = BuildService();
        repo.Setup(r => r.TrySeedTenantContactEmailAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync(true);

        await service.TrySeedContactEmailAsync(TenantId, "ops@contoso.com");

        repo.Verify(r => r.SaveTenantConfigurationAsync(It.IsAny<TenantConfiguration>()), Times.Never);
    }

    // ------------------------------------------------------------------
    // Seed trigger (PreviewWhitelistService)
    // ------------------------------------------------------------------

    private static (PreviewWhitelistService preview, Mock<IConfigRepository> repo) BuildPreview()
    {
        var repo = new Mock<IConfigRepository>();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var tenantConfig = new TenantConfigurationService(
            repo.Object, NullLogger<TenantConfigurationService>.Instance, cache);
        var preview = new PreviewWhitelistService(
            repo.Object, cache, NullLogger<PreviewWhitelistService>.Instance, tenantConfig);
        return (preview, repo);
    }

    [Fact]
    public async Task SaveNotificationEmail_persists_the_address_and_seeds_the_contact_field()
    {
        var (preview, repo) = BuildPreview();
        repo.Setup(r => r.TrySeedTenantContactEmailAsync(TenantId, "ops@contoso.com")).ReturnsAsync(true);

        await preview.SaveNotificationEmailAsync(TenantId, "ops@contoso.com");

        repo.Verify(r => r.SaveNotificationEmailAsync(TenantId, "ops@contoso.com"), Times.Once);
        repo.Verify(r => r.TrySeedTenantContactEmailAsync(TenantId, "ops@contoso.com"), Times.Once);
    }

    [Fact]
    public async Task SaveNotificationEmail_clearing_the_address_does_not_seed()
    {
        var (preview, repo) = BuildPreview();

        await preview.SaveNotificationEmailAsync(TenantId, null);

        repo.Verify(r => r.SaveNotificationEmailAsync(TenantId, null), Times.Once);
        repo.Verify(r => r.TrySeedTenantContactEmailAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task SaveNotificationEmail_still_succeeds_when_the_seed_blows_up()
    {
        // The notification email has already been persisted at that point; a failing side
        // effect must not turn a completed write into a failed request.
        var (preview, repo) = BuildPreview();
        repo.Setup(r => r.TrySeedTenantContactEmailAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new RequestFailedException(503, "throttled"));

        await preview.SaveNotificationEmailAsync(TenantId, "ops@contoso.com");

        repo.Verify(r => r.SaveNotificationEmailAsync(TenantId, "ops@contoso.com"), Times.Once);
    }

    // ------------------------------------------------------------------
    // Conditional write (TableConfigRepository) — the actual invariant
    // ------------------------------------------------------------------

    private sealed class RepoHarness
    {
        public TableConfigRepository Sut { get; }
        public Mock<TableClient> Table { get; } = new();
        public List<(TableEntity Entity, ETag IfMatch, TableUpdateMode Mode)> Updates { get; } = new();

        public RepoHarness(TableEntity? stored, RequestFailedException? throwOnUpdate = null)
        {
            if (stored == null)
            {
                Table.Setup(c => c.GetEntityAsync<TableEntity>(
                        It.IsAny<string>(), It.IsAny<string>(),
                        It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new RequestFailedException(404, "Not Found", "ResourceNotFound", null));
            }
            else
            {
                Table.Setup(c => c.GetEntityAsync<TableEntity>(
                        It.IsAny<string>(), It.IsAny<string>(),
                        It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Response.FromValue(stored, Mock.Of<Response>()));
            }

            Table.Setup(c => c.UpdateEntityAsync(
                    It.IsAny<TableEntity>(), It.IsAny<ETag>(),
                    It.IsAny<TableUpdateMode>(), It.IsAny<CancellationToken>()))
                .Returns<TableEntity, ETag, TableUpdateMode, CancellationToken>((e, tag, mode, _) =>
                {
                    if (throwOnUpdate != null)
                        throw throwOnUpdate;
                    Updates.Add((e, tag, mode));
                    return Task.FromResult(Mock.Of<Response>());
                });

            var serviceClient = new Mock<TableServiceClient>();
            serviceClient.Setup(c => c.GetTableClient(It.IsAny<string>())).Returns(Table.Object);

            Sut = new TableConfigRepository(
                new TableStorageService(serviceClient.Object, NullLogger<TableStorageService>.Instance),
                NullLogger<TableConfigRepository>.Instance);
        }
    }

    private static TableEntity StoredConfig(string? contactEmail, string etag = "W/\"stored\"")
    {
        var entity = new TableEntity(TenantId, "config") { ["DomainName"] = "contoso.com" };
        if (contactEmail != null)
            entity["ContactEmail"] = contactEmail;
        entity.ETag = new ETag(etag);
        return entity;
    }

    [Fact]
    public async Task Repo_seeds_only_the_contact_field_and_only_conditionally()
    {
        var harness = new RepoHarness(StoredConfig(contactEmail: null));

        Assert.True(await harness.Sut.TrySeedTenantContactEmailAsync(TenantId, "  ops@contoso.com "));

        var (entity, ifMatch, mode) = Assert.Single(harness.Updates);
        // Merge, not Replace: every other stored property must survive untouched.
        Assert.Equal(TableUpdateMode.Merge, mode);
        Assert.Equal(new ETag("W/\"stored\""), ifMatch);
        Assert.Equal("ops@contoso.com", entity["ContactEmail"]);
        Assert.False(entity.ContainsKey("DomainName"));
    }

    [Fact]
    public async Task Repo_never_overwrites_an_address_the_tenant_already_owns()
    {
        var harness = new RepoHarness(StoredConfig("owner@contoso.com"));

        Assert.False(await harness.Sut.TrySeedTenantContactEmailAsync(TenantId, "ops@contoso.com"));

        Assert.Empty(harness.Updates);
    }

    [Fact]
    public async Task Repo_treats_a_whitespace_only_stored_value_as_absent()
    {
        var harness = new RepoHarness(StoredConfig("   "));

        Assert.True(await harness.Sut.TrySeedTenantContactEmailAsync(TenantId, "ops@contoso.com"));

        Assert.Single(harness.Updates);
    }

    [Fact]
    public async Task Repo_loses_the_race_rather_than_clobbering_a_concurrent_write()
    {
        // Someone wrote the row between our read and our write. The ETag precondition turns
        // that into a 412; the seed abandons rather than overwriting the winner.
        var harness = new RepoHarness(
            StoredConfig(contactEmail: null),
            throwOnUpdate: new RequestFailedException(412, "Precondition Failed", "UpdateConditionNotSatisfied", null));

        Assert.False(await harness.Sut.TrySeedTenantContactEmailAsync(TenantId, "ops@contoso.com"));
    }

    [Fact]
    public async Task Repo_with_no_configuration_row_reports_no_seed()
    {
        var harness = new RepoHarness(stored: null);

        Assert.False(await harness.Sut.TrySeedTenantContactEmailAsync(TenantId, "ops@contoso.com"));

        Assert.Empty(harness.Updates);
    }

    [Fact]
    public async Task Repo_is_fail_soft_on_a_storage_fault()
    {
        // Callers are side effects of an already-committed write — a lost seed is recoverable
        // on the next maintenance run, a thrown exception is not.
        var harness = new RepoHarness(
            StoredConfig(contactEmail: null),
            throwOnUpdate: new RequestFailedException(503, "Server Busy"));

        Assert.False(await harness.Sut.TrySeedTenantContactEmailAsync(TenantId, "ops@contoso.com"));
    }

    [Theory]
    [InlineData(null, "ops@contoso.com")]
    [InlineData("", "ops@contoso.com")]
    [InlineData(TenantId, null)]
    [InlineData(TenantId, "   ")]
    public async Task Repo_rejects_incomplete_input_without_touching_storage(string? tenantId, string? email)
    {
        var harness = new RepoHarness(StoredConfig(contactEmail: null));

        Assert.False(await harness.Sut.TrySeedTenantContactEmailAsync(tenantId!, email!));

        Assert.Empty(harness.Updates);
        harness.Table.Verify(c => c.GetEntityAsync<TableEntity>(
            It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
