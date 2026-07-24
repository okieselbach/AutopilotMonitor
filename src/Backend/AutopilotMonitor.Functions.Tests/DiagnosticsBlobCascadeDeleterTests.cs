using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Diagnostics;
using AutopilotMonitor.Shared.Models.Deletion;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Plan §5b: cascade-delete must route the diagnostics blob to the right store and
/// honour the "customer SAS without delete = skip" rule. The deleter is the only
/// place this routing is decided; misclassification either leaks blobs or poisons
/// the cascade. Tests pin every branch via a recording subclass — no Azurite, no
/// HTTP — by replacing the storage primitives at the seam.
/// </summary>
public class DiagnosticsBlobCascadeDeleterTests
{
    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";
    private const string Filename = "AgentDiagnostics-aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa-20260519T120000.zip";

    [Fact]
    public async Task DeleteAsync_NoBlobName_SkipsImmediately()
    {
        var deleter = new RecordingDeleter();
        var manifest = ManifestWith(blobName: null, destination: "Hosted");

        var outcome = await deleter.DeleteAsync(manifest);

        Assert.Equal(DiagnosticsBlobDeleteOutcome.SkippedNoBlob, outcome);
        Assert.Equal(0, deleter.HostedDeleteCalls);
        Assert.Equal(0, deleter.CustomerSasResolveCalls);
    }

    [Fact]
    public async Task DeleteAsync_HostedDestination_AlwaysDeletes_RegardlessOfSas()
    {
        var deleter = new RecordingDeleter
        {
            // Even if a stale customer SAS happens to be configured, Hosted overrides.
            ConfiguredCustomerSasUrl = "https://customer.blob/diag?sp=rwc&sig=x",
        };
        var manifest = ManifestWith(blobName: $"{TenantA}/{Filename}", destination: "Hosted");

        var outcome = await deleter.DeleteAsync(manifest);

        Assert.Equal(DiagnosticsBlobDeleteOutcome.HostedDeleted, outcome);
        Assert.Equal(1, deleter.HostedDeleteCalls);
        Assert.Equal($"{TenantA}/{Filename}", deleter.HostedDeleteLastBlobPath);
        Assert.Equal(0, deleter.CustomerSasDeleteCalls);
    }

    [Theory]
    [InlineData("Hosted")]
    [InlineData("hosted")]
    [InlineData("HOSTED")]
    public async Task DeleteAsync_HostedDestination_CaseInsensitive(string destination)
    {
        var deleter = new RecordingDeleter();
        var manifest = ManifestWith(blobName: $"{TenantA}/x.zip", destination: destination);

        var outcome = await deleter.DeleteAsync(manifest);

        Assert.Equal(DiagnosticsBlobDeleteOutcome.HostedDeleted, outcome);
    }

    [Fact]
    public async Task DeleteAsync_CustomerSas_NoSasConfigured_Skips()
    {
        var deleter = new RecordingDeleter
        {
            ConfiguredCustomerSasUrl = null,
        };
        var manifest = ManifestWith(blobName: Filename, destination: "CustomerSas");

        var outcome = await deleter.DeleteAsync(manifest);

        Assert.Equal(DiagnosticsBlobDeleteOutcome.SkippedCustomerNoSas, outcome);
        Assert.Equal(0, deleter.HostedDeleteCalls);
        Assert.Equal(0, deleter.CustomerSasDeleteCalls);
    }

    [Fact]
    public async Task DeleteAsync_CustomerSas_SasLacksDelete_Skips()
    {
        var deleter = new RecordingDeleter
        {
            // sp=rwc has Read+Write+Create but no Delete. Customer's lifecycle rules
            // remain the source of truth.
            ConfiguredCustomerSasUrl = "https://customer.blob/diag?sv=2024&sp=rwc&sig=x",
        };
        var manifest = ManifestWith(blobName: Filename, destination: "CustomerSas");

        var outcome = await deleter.DeleteAsync(manifest);

        Assert.Equal(DiagnosticsBlobDeleteOutcome.SkippedCustomerSasLacksDelete, outcome);
        Assert.Equal(0, deleter.CustomerSasDeleteCalls);
    }

    [Fact]
    public async Task DeleteAsync_CustomerSas_SasHasDelete_Deletes()
    {
        var deleter = new RecordingDeleter
        {
            ConfiguredCustomerSasUrl = "https://customer.blob/diag?sv=2024&sp=rwcd&sig=x",
        };
        var manifest = ManifestWith(blobName: Filename, destination: "CustomerSas");

        var outcome = await deleter.DeleteAsync(manifest);

        Assert.Equal(DiagnosticsBlobDeleteOutcome.CustomerSasDeleted, outcome);
        Assert.Equal(1, deleter.CustomerSasDeleteCalls);
        Assert.Equal(
            $"https://customer.blob/diag/{Filename}?sv=2024&sp=rwcd&sig=x",
            deleter.CustomerSasDeleteLastUrl);
    }

    [Fact]
    public async Task DeleteAsync_NullDestination_TreatedAsCustomerSas_LegacyCompat()
    {
        // Sessions written before the §5b column always had blobs in the customer's
        // container. Treating null as CustomerSas preserves that contract — without
        // the legacy default we'd attempt a Hosted DELETE against a path that
        // doesn't exist there, then 404 + record HostedDeleted (wrong telemetry).
        var deleter = new RecordingDeleter
        {
            ConfiguredCustomerSasUrl = "https://customer.blob/diag?sp=rwcd&sig=x",
        };
        var manifest = ManifestWith(blobName: Filename, destination: null);

        var outcome = await deleter.DeleteAsync(manifest);

        Assert.Equal(DiagnosticsBlobDeleteOutcome.CustomerSasDeleted, outcome);
    }

    [Fact]
    public async Task DeleteAsync_UnknownDestination_TreatedAsCustomerSas_SafetyDefault()
    {
        // Defence-in-depth: a typo like "Vendor" or "Self" must NOT route to the
        // hosted DELETE path (which would silently 404 against the wrong storage
        // account, producing misleading "HostedDeleted" telemetry without actually
        // touching the customer's blob).
        var deleter = new RecordingDeleter
        {
            ConfiguredCustomerSasUrl = "https://customer.blob/diag?sp=rwc&sig=x",
        };
        var manifest = ManifestWith(blobName: Filename, destination: "Vendor");

        var outcome = await deleter.DeleteAsync(manifest);

        Assert.Equal(DiagnosticsBlobDeleteOutcome.SkippedCustomerSasLacksDelete, outcome);
        Assert.Equal(0, deleter.HostedDeleteCalls);
    }

    // ── REAL DeleteAsync, Hosted branch — prefix sweep + belt-and-braces name delete ──
    // The RecordingDeleter above replaces DeleteAsync wholesale, so these run the REAL
    // base implementation against a fake storage service to pin the sweep behaviour.

    private const string SessionGuid = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    [Fact]
    public async Task RealDeleteAsync_Hosted_SweepsSessionPrefix_AndDeletesManifestBlob()
    {
        var storage = new RecordingHostedStorage();
        var deleter = RealDeleter(storage);
        var manifest = ManifestWith(blobName: $"{TenantA}/{Filename}", destination: "Hosted");
        manifest.SessionId = SessionGuid;

        var outcome = await deleter.DeleteAsync(manifest);

        Assert.Equal(DiagnosticsBlobDeleteOutcome.HostedDeleted, outcome);
        Assert.Equal((TenantA, SessionGuid), storage.SweptSession);
        Assert.Equal($"{TenantA}/{Filename}", storage.DeletedBlobPath);
    }

    [Fact]
    public async Task RealDeleteAsync_Hosted_NonGuidSessionId_SkipsSweep_StillDeletesManifestBlob()
    {
        // Legacy manifests may carry a malformed/empty SessionId — the sweep must degrade
        // to the single-blob delete, never throw into the cascade poison path.
        var storage = new RecordingHostedStorage();
        var deleter = RealDeleter(storage);
        var manifest = ManifestWith(blobName: $"{TenantA}/{Filename}", destination: "Hosted");
        manifest.SessionId = "not-a-guid";

        var outcome = await deleter.DeleteAsync(manifest);

        Assert.Equal(DiagnosticsBlobDeleteOutcome.HostedDeleted, outcome);
        Assert.Null(storage.SweptSession);
        Assert.Equal($"{TenantA}/{Filename}", storage.DeletedBlobPath);
    }

    private static DiagnosticsBlobCascadeDeleter RealDeleter(RecordingHostedStorage storage)
        // TenantConfigurationService is only consulted on the CustomerSas branch; the
        // Hosted-branch tests never reach it, so null keeps the harness Azure-free.
        => new(
            storage,
            tenantConfig: null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DiagnosticsBlobCascadeDeleter>.Instance);

    private sealed class RecordingHostedStorage : HostedDiagnosticsBlobService
    {
        public (string TenantId, string SessionId)? SweptSession { get; private set; }
        public string? DeletedBlobPath { get; private set; }

        public RecordingHostedStorage()
            : base(
                new Azure.Storage.Blobs.BlobServiceClient("UseDevelopmentStorage=true"),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<HostedDiagnosticsBlobService>.Instance,
                usesManagedIdentity: false)
        {
        }

        public override Task<int> DeleteBySessionPrefixAsync(
            string tenantId, string sessionId, CancellationToken cancellationToken = default)
        {
            SweptSession = (tenantId, sessionId);
            return Task.FromResult(1);
        }

        public override Task<bool> DeleteIfExistsAsync(
            string blobPath, CancellationToken cancellationToken = default)
        {
            DeletedBlobPath = blobPath;
            return Task.FromResult(true);
        }
    }

    // ── BuildBlobUrl helper (CustomerSas URL construction) ─────────────────────────

    [Fact]
    public void BuildBlobUrl_AppendsBlobNameBeforeQueryString()
    {
        var url = DiagnosticsBlobCascadeDeleter.BuildBlobUrl(
            "https://customer.blob/diag?sv=2024&sp=rwcd&sig=x", "AgentDiagnostics-x.zip");
        Assert.Equal(
            "https://customer.blob/diag/AgentDiagnostics-x.zip?sv=2024&sp=rwcd&sig=x",
            url);
    }

    [Fact]
    public void BuildBlobUrl_NoQueryString_AppendsBlobName()
    {
        var url = DiagnosticsBlobCascadeDeleter.BuildBlobUrl(
            "https://customer.blob/diag", "x.zip");
        Assert.Equal("https://customer.blob/diag/x.zip", url);
    }

    // ── NormalizeDestination ───────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("CustomerSas")]
    [InlineData("Vendor")]              // unknown → CustomerSas (NOT Hosted)
    [InlineData("self")]                // unknown → CustomerSas
    public void NormalizeDestination_DefaultsToCustomerSas(string? raw)
    {
        Assert.Equal("CustomerSas", DiagnosticsBlobCascadeDeleter.NormalizeDestination(raw));
    }

    [Theory]
    [InlineData("Hosted")]
    [InlineData("hosted")]
    [InlineData("HOSTED")]
    public void NormalizeDestination_RecognisesHostedCaseInsensitive(string raw)
    {
        Assert.Equal("Hosted", DiagnosticsBlobCascadeDeleter.NormalizeDestination(raw));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    private static DeletionManifest ManifestWith(string? blobName, string? destination)
        => new()
        {
            ManifestId = "M1",
            TenantId = TenantA,
            SessionId = "S1",
            DiagnosticsBlobName = blobName,
            DiagnosticsBlobDestination = destination,
        };

    /// <summary>
    /// Subclasses <see cref="DiagnosticsBlobCascadeDeleter"/> via the protected
    /// test-seam ctor + overrides the public <see cref="DeleteAsync"/> with an
    /// in-memory recording version that mirrors the production routing logic.
    /// Bypasses Azure entirely, so the suite runs without Azurite.
    /// </summary>
    private sealed class RecordingDeleter : DiagnosticsBlobCascadeDeleter
    {
        public string? ConfiguredCustomerSasUrl { get; set; }

        public int HostedDeleteCalls { get; private set; }
        public string? HostedDeleteLastBlobPath { get; private set; }

        public int CustomerSasResolveCalls { get; private set; }
        public int CustomerSasDeleteCalls { get; private set; }
        public string? CustomerSasDeleteLastUrl { get; private set; }

        public RecordingDeleter() : base() { }

        public override Task<DiagnosticsBlobDeleteOutcome> DeleteAsync(
            DeletionManifest manifest, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(manifest.DiagnosticsBlobName))
                return Task.FromResult(DiagnosticsBlobDeleteOutcome.SkippedNoBlob);

            var destination = NormalizeDestination(manifest.DiagnosticsBlobDestination);
            if (destination == DestinationHosted)
            {
                HostedDeleteCalls++;
                HostedDeleteLastBlobPath = manifest.DiagnosticsBlobName;
                return Task.FromResult(DiagnosticsBlobDeleteOutcome.HostedDeleted);
            }

            CustomerSasResolveCalls++;
            var sas = ConfiguredCustomerSasUrl;
            if (string.IsNullOrEmpty(sas))
                return Task.FromResult(DiagnosticsBlobDeleteOutcome.SkippedCustomerNoSas);
            if (!SasPermissionParser.HasDelete(sas))
                return Task.FromResult(DiagnosticsBlobDeleteOutcome.SkippedCustomerSasLacksDelete);

            CustomerSasDeleteCalls++;
            CustomerSasDeleteLastUrl = BuildBlobUrl(sas!, manifest.DiagnosticsBlobName!);
            return Task.FromResult(DiagnosticsBlobDeleteOutcome.CustomerSasDeleted);
        }
    }
}
