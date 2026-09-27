using AutopilotMonitor.Functions.DataAccess.TableStorage;
using AutopilotMonitor.Functions.Functions.Admin;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

internal static class ConfigRepoMockExtensions
{
    /// <summary>
    /// Asserts that no tenant configuration row was created or replaced — the only two ways the
    /// repository can write one (D-290).
    /// </summary>
    public static void VerifyNoTenantConfigWrite(this Mock<IConfigRepository> repo)
    {
        repo.Verify(r => r.TryCreateTenantConfigurationAsync(It.IsAny<TenantConfiguration>()), Times.Never);
        repo.Verify(r => r.TryReplaceTenantConfigurationAsync(It.IsAny<TenantConfiguration>(), It.IsAny<string>()), Times.Never);
        repo.Verify(r => r.TryReplaceTenantConfigurationAsync(
            It.IsAny<TenantConfiguration>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
    }

    /// <summary>
    /// Backs <see cref="TenantConfigurationService.UpdateAsync"/> and
    /// <see cref="TenantConfigurationService.CreateOrUpdateAsync"/> of a service mock with one stored row
    /// and the real contract: the mutation sees a fresh copy (never the caller's object), the offboarding
    /// tombstone is refused, a declined mutation writes nothing, and every write is recorded.
    /// </summary>
    public static StoredTenantConfig StubWrites(this Mock<TenantConfigurationService> service, string tenantId, TenantConfiguration? row)
    {
        var store = new StoredTenantConfig { Row = row };

        Task<TenantConfigUpdate> Run(Func<TenantConfiguration, bool> mutate, string source, string reason, bool createIfMissing)
        {
            if (store.Force is { } forced)
                return Task.FromResult(new TenantConfigUpdate(forced, null));
            if (store.ThrowOnRead is { } ex)
                throw ex;

            TenantConfiguration fresh;
            if (store.Row == null)
            {
                if (!createIfMissing)
                    return Task.FromResult(new TenantConfigUpdate(TenantConfigUpdateStatus.NotFound, null));
                fresh = TenantConfiguration.CreateDefault(tenantId);
            }
            else
            {
                fresh = Clone(store.Row);
                if (TenantOffboardFunction.IsOffboardingTombstone(fresh))
                    return Task.FromResult(new TenantConfigUpdate(TenantConfigUpdateStatus.OffboardingInProgress, fresh));
            }

            if (!mutate(fresh))
                return Task.FromResult(new TenantConfigUpdate(TenantConfigUpdateStatus.Declined, fresh));

            fresh.LastUpdated = DateTime.UtcNow;
            store.Row = fresh;
            store.Writes.Add((fresh, source, reason));
            return Task.FromResult(new TenantConfigUpdate(TenantConfigUpdateStatus.Updated, fresh));
        }

        service.Setup(x => x.UpdateAsync(tenantId, It.IsAny<Func<TenantConfiguration, bool>>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string _, Func<TenantConfiguration, bool> mutate, string source, string reason) => Run(mutate, source, reason, false));
        service.Setup(x => x.CreateOrUpdateAsync(tenantId, It.IsAny<Func<TenantConfiguration, bool>>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string _, Func<TenantConfiguration, bool> mutate, string source, string reason) => Run(mutate, source, reason, true));
        return store;
    }

    /// <summary>
    /// Backs the tenant-configuration reads and writes of a repository mock with one stored row and
    /// the storage contract: every read hands back a fresh copy, the replace succeeds only with the
    /// current ETag, the insert only while no row exists, and every write is recorded.
    /// </summary>
    public static StoredTenantConfig BackWithRow(this Mock<IConfigRepository> repo, TenantConfiguration? row)
    {
        var store = new StoredTenantConfig { Row = row };
        var version = 0;
        string ETag() => $"etag-{version}";

        TenantConfiguration? Read()
        {
            if (store.ThrowOnRead is { } ex) throw ex;
            return store.Row == null ? null : Clone(store.Row);
        }

        bool Write(TenantConfiguration config, string source, string reason)
        {
            if (store.ThrowOnWrite is { } ex) throw ex;
            if (store.RejectWrites) return false;
            store.Row = Clone(config);
            version++;
            store.Writes.Add((store.Row, source, reason));
            return true;
        }

        repo.Setup(r => r.GetTenantConfigurationAsync(It.IsAny<string>())).ReturnsAsync(() => Read());
        repo.Setup(r => r.GetTenantConfigurationWithEtagAsync(It.IsAny<string>()))
            .ReturnsAsync(() => Read() is { } fresh ? (fresh, ETag()) : null);
        repo.Setup(r => r.TryReplaceTenantConfigurationAsync(
                It.IsAny<TenantConfiguration>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
            .ReturnsAsync((TenantConfiguration c, string etag, string? source, string? reason) =>
                store.Row != null && etag == ETag() && Write(c, source ?? "", reason ?? ""));
        repo.Setup(r => r.TryCreateTenantConfigurationAsync(It.IsAny<TenantConfiguration>()))
            .ReturnsAsync((TenantConfiguration c) => store.Row == null && Write(c, "(create)", ""));
        return store;
    }

    /// <summary>A storage round trip: what a fresh read of the stored row hands back.</summary>
    public static TenantConfiguration Clone(TenantConfiguration config)
        => TableConfigRepository.ConvertFromTenantTableEntity(TableConfigRepository.ConvertToTenantTableEntity(config));
}

/// <summary>The stored row behind <see cref="ConfigRepoMockExtensions.StubWrites"/> and what was written to it.</summary>
internal sealed class StoredTenantConfig
{
    public TenantConfiguration? Row { get; set; }
    public List<(TenantConfiguration Row, string Source, string Reason)> Writes { get; } = new();

    /// <summary>When set, every write returns this status without touching the row (e.g. Conflict).</summary>
    public TenantConfigUpdateStatus? Force { get; set; }

    /// <summary>When set, every read (and, for the service stub, every write) throws it — a storage outage.</summary>
    public Exception? ThrowOnRead { get; set; }

    /// <summary>Repository double only: when set, every write throws it.</summary>
    public Exception? ThrowOnWrite { get; set; }

    /// <summary>Repository double only: every conditional write loses (as if a concurrent writer always won).</summary>
    public bool RejectWrites { get; set; }
}
