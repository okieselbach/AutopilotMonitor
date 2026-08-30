using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Wire-identity proof for the Functions/Reports folder (anonymous-object → typed-DTO
/// migration). Each fact serializes the OLD anonymous literal exactly as it appeared at the
/// call site (independent fixture, copied from the pre-migration code) and the NEW typed DTO
/// with the same values, asserting ordinally identical JSON via
/// <see cref="ApiResponseWireParityTests.AssertWireIdentical"/>.
/// </summary>
public class ReportsWireParityTests
{
    private static readonly DateTime T0 = new(2026, 8, 15, 10, 30, 0, DateTimeKind.Utc);

    // =========================================================================
    // GetDeviceNotRegisteredFunction — { success, aggregated, totalRawReports, dataQualityNotice }
    // =========================================================================

    [Fact]
    public void DeviceNotRegistered_envelope_and_item_match_old_anonymous_shape()
    {
        const string notice = "This data is from pre-authentication distress reports and is UNVERIFIED. Devices reported here were rejected with HTTP 403 because they were not found in the tenant's Autopilot or Corporate Identifier registry. Serial number, manufacturer, model, and the Cloud PC marker are self-reported by devices.";

        // Old anonymous item shape from BuildAggregatedResult (pre-migration), in a List<object>
        // exactly as the old code produced it.
        var oldAggregated = new List<object>
        {
            new
            {
                serialNumber = "GM18NHV3",
                manufacturer = "Lenovo",
                model = "ThinkPad T14",
                isCloudPc = true,
                attemptCount = 3,
                firstSeen = T0,
                lastSeen = T0.AddHours(2)
            }
        };

        ApiResponseWireParityTests.AssertWireIdentical(
            new
            {
                success = true,
                aggregated = oldAggregated,
                totalRawReports = 3,
                dataQualityNotice = notice
            },
            new DeviceNotRegisteredResponse
            {
                Success = true,
                Aggregated = new List<DeviceNotRegisteredItem>
                {
                    new()
                    {
                        SerialNumber = "GM18NHV3",
                        Manufacturer = "Lenovo",
                        Model = "ThinkPad T14",
                        IsCloudPc = true,
                        AttemptCount = 3,
                        FirstSeen = T0,
                        LastSeen = T0.AddHours(2)
                    }
                },
                TotalRawReports = 3,
                DataQualityNotice = notice
            });
    }

    // =========================================================================
    // GetHardwareRejectedFunction — { success, aggregated, totalRawReports, dataQualityNotice }
    // =========================================================================

    [Fact]
    public void HardwareRejected_envelope_and_item_match_old_anonymous_shape()
    {
        const string notice = "This data is from pre-authentication distress reports and is UNVERIFIED. Manufacturer, model, and serial number values are self-reported by devices.";

        var oldAggregated = new List<object>
        {
            new
            {
                manufacturer = "Dell Inc.",
                model = "Latitude 5440",
                attemptCount = 4,
                uniqueSerials = 2,
                firstSeen = T0,
                lastSeen = T0.AddDays(1),
                sampleSerialNumbers = new List<string> { "SN1", "SN2" }
            }
        };

        ApiResponseWireParityTests.AssertWireIdentical(
            new
            {
                success = true,
                aggregated = oldAggregated,
                totalRawReports = 4,
                dataQualityNotice = notice
            },
            new HardwareRejectedResponse
            {
                Success = true,
                Aggregated = new List<HardwareRejectedItem>
                {
                    new()
                    {
                        Manufacturer = "Dell Inc.",
                        Model = "Latitude 5440",
                        AttemptCount = 4,
                        UniqueSerials = 2,
                        FirstSeen = T0,
                        LastSeen = T0.AddDays(1),
                        SampleSerialNumbers = new List<string> { "SN1", "SN2" }
                    }
                },
                TotalRawReports = 4,
                DataQualityNotice = notice
            });
    }

    // =========================================================================
    // GetTpmPssUnsupportedFunction — { success, aggregated, totalRawReports, dataQualityNotice }
    // =========================================================================

    [Fact]
    public void TpmPssUnsupported_envelope_and_item_match_old_anonymous_shape()
    {
        const string notice = "This data is from pre-authentication distress reports and is UNVERIFIED. Devices reported here have a TPM that cannot perform RSA-PSS signing, so their agent cannot authenticate to the backend. Serial number, manufacturer, and model values are self-reported by devices.";

        var oldAggregated = new List<object>
        {
            new
            {
                serialNumber = "SN1",
                manufacturer = "Lenovo",
                model = "ThinkCentre M720q",
                attemptCount = 3,
                firstSeen = T0,
                lastSeen = T0.AddHours(2)
            }
        };

        ApiResponseWireParityTests.AssertWireIdentical(
            new
            {
                success = true,
                aggregated = oldAggregated,
                totalRawReports = 3,
                dataQualityNotice = notice
            },
            new TpmPssUnsupportedResponse
            {
                Success = true,
                Aggregated = new List<TpmPssUnsupportedItem>
                {
                    new()
                    {
                        SerialNumber = "SN1",
                        Manufacturer = "Lenovo",
                        Model = "ThinkCentre M720q",
                        AttemptCount = 3,
                        FirstSeen = T0,
                        LastSeen = T0.AddHours(2)
                    }
                },
                TotalRawReports = 3,
                DataQualityNotice = notice
            });
    }

    // =========================================================================
    // GetSessionReportsFunction — non-paged { success, count, reports } and
    // paged { success, count, reports, nextLink } — one DTO (SessionReportListResponse)
    // =========================================================================

    private static List<SessionReportMetadata> MakeReports() =>
        new()
        {
            new SessionReportMetadata
            {
                ReportId = "5f0d2c9a-1b3e-4f6a-8c7d-9e0f1a2b3c4d",
                TenantId = "11111111-2222-3333-4444-555555555555",
                SessionId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
                Comment = "ESP stuck at device setup",
                Email = "admin@contoso.com",
                BlobName = "report_5f0d2c9a.zip",
                SubmittedBy = "admin@contoso.com",
                SubmittedAt = T0,
                AdminNote = "",
                ReportType = ReportTypes.Session,
                DiagnosticsBlobName = null,
                DiagnosticsCopyStatus = null
            }
        };

    [Fact]
    public void SessionReports_nonPaged_matches_old_anonymous_shape_without_nextLink_key()
    {
        var reports = MakeReports();

        // Old non-paged literal had NO nextLink property; the typed DTO leaves NextLink null
        // and WhenWritingNull must drop the key so both sides stay identical.
        ApiResponseWireParityTests.AssertWireIdentical(
            new { success = true, count = reports.Count, reports },
            new SessionReportListResponse { Success = true, Count = reports.Count, Reports = reports });
    }

    [Fact]
    public void SessionReports_paged_with_nextLink_matches_old_anonymous_shape()
    {
        var reports = MakeReports();
        var nextLink = "/api/global/session-reports?pageSize=50&continuation=abc123";

        ApiResponseWireParityTests.AssertWireIdentical(
            new
            {
                success = true,
                count = reports.Count,
                reports,
                nextLink,
            },
            new SessionReportListResponse
            {
                Success = true,
                Count = reports.Count,
                Reports = reports,
                NextLink = nextLink,
            });
    }

    [Fact]
    public void SessionReports_paged_lastPage_nextLink_null_drops_key_on_both_sides()
    {
        var reports = MakeReports();
        string? nextLink = null;

        ApiResponseWireParityTests.AssertWireIdentical(
            new
            {
                success = true,
                count = reports.Count,
                reports,
                nextLink,
            },
            new SessionReportListResponse
            {
                Success = true,
                Count = reports.Count,
                Reports = reports,
                NextLink = nextLink,
            });
    }

    // =========================================================================
    // GetDistressReportsFunction — { success, count, reports }
    // =========================================================================

    [Fact]
    public void DistressReports_matches_old_anonymous_shape()
    {
        var reports = new List<DistressReportEntry>
        {
            new()
            {
                TenantId = "11111111-2222-3333-4444-555555555555",
                ErrorType = "DeviceNotRegistered",
                Manufacturer = "Lenovo",
                Model = "ThinkPad T14",
                SerialNumber = "GM18NHV3",
                AgentVersion = "2.4.1.0",
                HttpStatusCode = 403,
                Message = "Device not found in registry",
                AgentTimestamp = T0,
                IngestedAt = T0.AddSeconds(5),
                SourceIp = "203.0.113.10",
                IsCloudPc = false
                // Cert-context fields left null — WhenWritingNull drops them identically
                // on both sides (same DistressReportEntry class serializes the items).
            }
        };

        ApiResponseWireParityTests.AssertWireIdentical(
            new
            {
                success = true,
                count = reports.Count,
                reports
            },
            new DistressReportListResponse
            {
                Success = true,
                Count = reports.Count,
                Reports = reports
            });
    }

    // =========================================================================
    // GetSessionReportDownloadUrlFunction — { success, downloadUrl }
    // =========================================================================

    [Fact]
    public void SessionReportDownloadUrl_matches_old_anonymous_shape()
    {
        var downloadUrl = "https://storage.example.invalid/session-reports/report_5f0d2c9a.zip?sv=2024-11-04&sig=abc";

        ApiResponseWireParityTests.AssertWireIdentical(
            new { success = true, downloadUrl },
            new SessionReportDownloadUrlResponse { Success = true, DownloadUrl = downloadUrl });
    }

    // =========================================================================
    // UpdateSessionReportNoteFunction — { success } (reuses SuccessOnlyResponse)
    // =========================================================================

    [Fact]
    public void UpdateSessionReportNote_success_matches_old_anonymous_shape()
    {
        ApiResponseWireParityTests.AssertWireIdentical(
            new { success = true },
            new SuccessOnlyResponse { Success = true });
    }
}
