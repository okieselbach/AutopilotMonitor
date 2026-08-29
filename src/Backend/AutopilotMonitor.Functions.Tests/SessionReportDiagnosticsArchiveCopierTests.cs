using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services.Diagnostics;
using AutopilotMonitor.Shared.Models;
using Azure;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The diagnostics-archive copier preserves a session's diag ZIP with a session report.
/// Its contract: NEVER throw (a failed copy must not block the report), re-verify the
/// hosted tenant prefix (a tampered Sessions row must not reach foreign blobs), and honour
/// the same size cap as the download proxy. Tests pin every status branch via a recording
/// subclass — no Azurite, no HTTP.
/// </summary>
public class SessionReportDiagnosticsArchiveCopierTests
{
    private const string TenantA = "11111111-1111-1111-1111-111111111111";
    private const string TenantB = "22222222-2222-2222-2222-222222222222";
    private const string SessionId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string HostedBlob = $"{TenantA}/AgentDiagnostics-{SessionId}-20260729T120000.zip";
    private const string CustomerBlob = $"AgentDiagnostics-{SessionId}-20260729T120000.zip";
    private const string DestBlob = $"{TenantA}_{SessionId}_diag_archive_20260729_120000.zip";

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task CopyAsync_NoSource_FailsSoft_NoStorageCalls(string? source)
    {
        var copier = new RecordingCopier();

        var result = await copier.CopyAsync(TenantA, SessionId, source, DestBlob);

        Assert.False(result.Success);
        Assert.Equal(SessionReportDiagnosticsArchiveCopier.Statuses.FailedNoDiagnostics, result.Status);
        Assert.Equal(0, copier.HostedOpenCalls + copier.CustomerOpenCalls + copier.UploadCalls);
    }

    [Fact]
    public async Task CopyAsync_HostedBlobOfForeignTenant_Rejected_WithoutRead()
    {
        // Spoof guard: even if the Sessions row was tampered to point at another tenant's
        // hosted blob, the classifier re-checks the prefix against the validated tenant.
        var copier = new RecordingCopier();
        var foreignBlob = $"{TenantB}/AgentDiagnostics-{SessionId}-x.zip";

        var result = await copier.CopyAsync(TenantA, SessionId, foreignBlob, DestBlob);

        Assert.False(result.Success);
        Assert.Equal(SessionReportDiagnosticsArchiveCopier.Statuses.FailedInvalidBlobName, result.Status);
        Assert.Equal(0, copier.HostedOpenCalls + copier.CustomerOpenCalls + copier.UploadCalls);
    }

    [Theory]
    [InlineData("x/../../hosted-diagnostics/22222222-2222-2222-2222-222222222222/payload_diag_archive_1.zip")]
    [InlineData("sub/dir_diag_archive_1.zip")]
    [InlineData("..\\escape_diag_archive_1.zip")]
    public async Task CopyAsync_NonFlatDestination_Rejected_WithoutReadOrWrite(string destination)
    {
        // A path-shaped destination would let the SDK + System.Uri resolve the write outside
        // the session-reports container. Reject before touching any storage.
        var copier = new RecordingCopier();

        var result = await copier.CopyAsync(TenantA, SessionId, HostedBlob, destination);

        Assert.False(result.Success);
        Assert.Equal(SessionReportDiagnosticsArchiveCopier.Statuses.FailedInvalidBlobName, result.Status);
        Assert.Equal(0, copier.HostedOpenCalls + copier.CustomerOpenCalls + copier.UploadCalls);
    }

    [Fact]
    public async Task CopyAsync_Hosted_HappyPath_UploadsWithFlatName()
    {
        var copier = new RecordingCopier { SourceSizeBytes = 1234 };

        var result = await copier.CopyAsync(TenantA, SessionId, HostedBlob, DestBlob);

        Assert.True(result.Success);
        Assert.Equal(SessionReportDiagnosticsArchiveCopier.Statuses.Copied, result.Status);
        Assert.Equal(1234, result.SizeBytes);
        Assert.Equal(1, copier.HostedOpenCalls);
        Assert.Equal(HostedBlob, copier.HostedOpenLastPath);
        Assert.Equal(1, copier.UploadCalls);
        Assert.Equal(DestBlob, copier.UploadLastName);
        Assert.DoesNotContain("/", copier.UploadLastName);
    }

    [Fact]
    public async Task CopyAsync_CustomerSas_HappyPath_BuildsSasBlobUrl()
    {
        var copier = new RecordingCopier
        {
            SourceSizeBytes = 42,
            CustomerSasUrl = "https://customer.blob/diag?sv=2024&sp=r&sig=x",
        };

        var result = await copier.CopyAsync(TenantA, SessionId, CustomerBlob, DestBlob);

        Assert.True(result.Success);
        Assert.Equal(1, copier.CustomerOpenCalls);
        Assert.Equal(
            $"https://customer.blob/diag/{CustomerBlob}?sv=2024&sp=r&sig=x",
            copier.CustomerOpenLastUri!.ToString());
        Assert.Equal(1, copier.UploadCalls);
    }

    [Fact]
    public async Task CopyAsync_CustomerSas_NoSasConfigured_FailsSoft()
    {
        var copier = new RecordingCopier { CustomerSasUrl = null };

        var result = await copier.CopyAsync(TenantA, SessionId, CustomerBlob, DestBlob);

        Assert.False(result.Success);
        Assert.Equal(SessionReportDiagnosticsArchiveCopier.Statuses.FailedSasNotConfigured, result.Status);
        Assert.Equal(0, copier.CustomerOpenCalls + copier.UploadCalls);
    }

    [Fact]
    public async Task CopyAsync_SourceAboveSizeCap_Rejected_NoUpload()
    {
        var copier = new RecordingCopier
        {
            MaxDownloadSizeMB = 1,
            SourceSizeBytes = 2 * 1024 * 1024,
        };

        var result = await copier.CopyAsync(TenantA, SessionId, HostedBlob, DestBlob);

        Assert.False(result.Success);
        Assert.Equal(SessionReportDiagnosticsArchiveCopier.Statuses.FailedTooLarge, result.Status);
        Assert.Equal(2 * 1024 * 1024, result.SizeBytes);
        Assert.Equal(0, copier.UploadCalls);
    }

    [Fact]
    public async Task CopyAsync_SizeCapZero_MeansUnlimited()
    {
        var copier = new RecordingCopier
        {
            MaxDownloadSizeMB = 0,
            SourceSizeBytes = 5L * 1024 * 1024 * 1024,
        };

        var result = await copier.CopyAsync(TenantA, SessionId, HostedBlob, DestBlob);

        Assert.True(result.Success);
        Assert.Equal(1, copier.UploadCalls);
    }

    [Theory]
    [InlineData(404, SessionReportDiagnosticsArchiveCopier.Statuses.FailedSourceNotFound)]
    [InlineData(403, SessionReportDiagnosticsArchiveCopier.Statuses.FailedSasReadDenied)]
    [InlineData(500, SessionReportDiagnosticsArchiveCopier.Statuses.FailedError)]
    public async Task CopyAsync_RequestFailed_MapsStatus_WithoutThrowing(int httpStatus, string expected)
    {
        var copier = new RecordingCopier
        {
            HostedOpenException = new RequestFailedException(httpStatus, $"storage error {httpStatus}"),
        };

        var result = await copier.CopyAsync(TenantA, SessionId, HostedBlob, DestBlob);

        Assert.False(result.Success);
        Assert.Equal(expected, result.Status);
        Assert.Equal(0, copier.UploadCalls);
    }

    [Fact]
    public async Task CopyAsync_Timeout_MapsToTimeoutStatus()
    {
        var copier = new RecordingCopier
        {
            HostedOpenException = new OperationCanceledException(),
        };

        var result = await copier.CopyAsync(TenantA, SessionId, HostedBlob, DestBlob);

        Assert.False(result.Success);
        Assert.Equal(SessionReportDiagnosticsArchiveCopier.Statuses.FailedTimeout, result.Status);
    }

    [Fact]
    public async Task CopyAsync_UploadFailure_FailsSoft_AsError()
    {
        // Read succeeded but the write into session-reports blew up — still fail-soft.
        var copier = new RecordingCopier
        {
            UploadException = new InvalidOperationException("container gone"),
        };

        var result = await copier.CopyAsync(TenantA, SessionId, HostedBlob, DestBlob);

        Assert.False(result.Success);
        Assert.Equal(SessionReportDiagnosticsArchiveCopier.Statuses.FailedError, result.Status);
    }

    /// <summary>
    /// Overrides every storage/config seam with an in-memory recording version, so the
    /// tests exercise the REAL CopyAsync routing (classify → config → open → cap → upload)
    /// without touching Azure.
    /// </summary>
    private sealed class RecordingCopier : SessionReportDiagnosticsArchiveCopier
    {
        public int MaxDownloadSizeMB { get; set; } = 500;
        public long SourceSizeBytes { get; set; } = 100;
        public string? CustomerSasUrl { get; set; }
        public Exception? HostedOpenException { get; set; }
        public Exception? UploadException { get; set; }

        public int HostedOpenCalls { get; private set; }
        public string? HostedOpenLastPath { get; private set; }
        public int CustomerOpenCalls { get; private set; }
        public Uri? CustomerOpenLastUri { get; private set; }
        public int UploadCalls { get; private set; }
        public string? UploadLastName { get; private set; }

        protected override Task<AdminConfiguration> GetAdminConfigurationAsync()
            => Task.FromResult(new AdminConfiguration
            {
                MaxDiagnosticsDownloadSizeMB = MaxDownloadSizeMB,
                DiagnosticsDownloadTimeoutSeconds = 120,
            });

        protected override Task<TenantConfiguration> GetTenantConfigurationAsync(string tenantId)
            => Task.FromResult(new TenantConfiguration
            {
                DiagnosticsBlobSasUrl = CustomerSasUrl!,
            });

        protected override Task<(Stream Content, long ContentLength)> OpenHostedSourceAsync(
            string blobPath, CancellationToken cancellationToken)
        {
            HostedOpenCalls++;
            HostedOpenLastPath = blobPath;
            if (HostedOpenException != null) throw HostedOpenException;
            return Task.FromResult<(Stream, long)>((new MemoryStream(new byte[8]), SourceSizeBytes));
        }

        protected override Task<(Stream Content, long ContentLength)> OpenCustomerSourceAsync(
            Uri blobUri, CancellationToken cancellationToken)
        {
            CustomerOpenCalls++;
            CustomerOpenLastUri = blobUri;
            return Task.FromResult<(Stream, long)>((new MemoryStream(new byte[8]), SourceSizeBytes));
        }

        protected override Task UploadToReportsContainerAsync(
            string destinationBlobName, Stream content, CancellationToken cancellationToken)
        {
            UploadCalls++;
            UploadLastName = destinationBlobName;
            if (UploadException != null) throw UploadException;
            return Task.CompletedTask;
        }
    }
}
