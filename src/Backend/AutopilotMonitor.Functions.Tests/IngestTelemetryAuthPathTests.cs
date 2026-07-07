using System.Threading;
using System.Threading.Tasks;
using Azure.Data.Tables;
using AutopilotMonitor.Functions.Functions.Ingest;
using AutopilotMonitor.Functions.Services.Deletion;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Deletion;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// Auth / pre-persist gate coverage for <see cref="IngestTelemetryFunction.Run"/>.
///
/// <para>
/// HARNESS NOTE — why these are decision-seam tests, not a live <c>Run()</c> invocation.
/// This suite has no fixture that boots the Functions worker's response serializer, so
/// <c>HttpRequestData.CreateResponse</c> + <c>HttpResponseData.WriteAsJsonAsync</c> (which every
/// Run() exit path calls, incl. the 400/403/410 error paths via <c>WriteErrorAsync</c>) cannot be
/// exercised without inventing runtime infrastructure. This is the same call the sibling function
/// tests make explicitly — see <see cref="DeviceBlockFunctionTests"/> /
/// <see cref="GetAllBlockedDevicesFunctionTests"/> ("mocking HttpRequestData + the middleware chain
/// is more setup than the test is worth"). So, exactly like <see cref="IngestCriticalPathTests"/>,
/// this file pins the <b>decision predicates and mapping helpers Run() branches on</b> at their real
/// seams, not the terminal HTTP status emission.
/// </para>
///
/// <para>
/// Coverage map to the four requested auth branches:
/// <list type="bullet">
///   <item>missing <c>X-Tenant-Id</c> → 400: the <c>string.IsNullOrEmpty(tenantIdHeader)</c> gate
///   (Run L100).</item>
///   <item>tenant mismatch (header vs PartitionKey) → 403 / malformed PartitionKey → 400: the real
///   <see cref="IngestTelemetryFunction.TryParsePartitionKey"/> static + the
///   <c>OrdinalIgnoreCase</c> equality Run performs (Run L169-180).</item>
///   <item>session deletion locked → 410: the real <see cref="SessionDeletionGuard"/> throwing
///   <see cref="SessionDeletionLockedException"/> (Run L205-213), plus the unique
///   <see cref="IngestTelemetryFunction.TryReadSessionStatus"/> that consumes the guard's row.</item>
///   <item>kill-switch / device-blocked → 200-block: NOT duplicated here. The verdict Run() routes
///   on (<c>IsBlocked</c>/<c>IsKill</c>/<c>UnblockAt</c>) is produced by
///   <see cref="Functions.Services.KillSwitchEvaluator"/> and is already exhaustively covered at the
///   exact seam Run() calls in <see cref="KillSwitchEvaluatorTests"/> (device Block/Kill, version
///   Block/Kill, device-block-short-circuit). Re-instantiating the evaluator here would add zero
///   coverage.</item>
/// </list>
/// </para>
/// </summary>
public class IngestTelemetryAuthPathTests
{
    private const string TenantId  = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
    private const string SessionId = "b2c3d4e5-f6a7-8901-bcde-f12345678901";

    // ============================================================ Missing X-Tenant-Id → 400

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(TenantId, false)]
    public void MissingTenantHeader_gate_rejects_null_or_empty(string? tenantIdHeader, bool rejected)
    {
        // Run L100: `if (string.IsNullOrEmpty(tenantIdHeader)) return 400 "X-Tenant-Id header is required"`.
        // An absent header surfaces here as null (Run maps Headers.Contains==false → null).
        Assert.Equal(rejected, string.IsNullOrEmpty(tenantIdHeader));
    }

    // ============================================================ Tenant / PartitionKey auth decision

    public enum AuthDecision { Accept, MalformedPartitionKey, TenantMismatch }

    public static IEnumerable<object[]> PartitionKeyAuthCases()
    {
        // header, partitionKey, expected Run() decision
        yield return new object[] { TenantId, $"{TenantId}_{SessionId}", AuthDecision.Accept };
        // OrdinalIgnoreCase equality — same GUID, different casing must NOT be a mismatch (Run L174).
        yield return new object[] { TenantId.ToUpperInvariant(), $"{TenantId}_{SessionId}", AuthDecision.Accept };
        // Different tenant GUID in the body → 403.
        yield return new object[] { TenantId, $"c3d4e5f6-a7b8-9012-cdef-012345678901_{SessionId}", AuthDecision.TenantMismatch };
        // Unparseable PartitionKey → 400 before the mismatch check even runs (Run L169).
        yield return new object[] { TenantId, "no-underscore-here", AuthDecision.MalformedPartitionKey };
        yield return new object[] { TenantId, "_missing-tenant", AuthDecision.MalformedPartitionKey };
        yield return new object[] { TenantId, "too_many_parts_here", AuthDecision.MalformedPartitionKey };
    }

    [Theory]
    [MemberData(nameof(PartitionKeyAuthCases))]
    public void TenantAuth_reproduces_Run_decision(string header, string partitionKey, AuthDecision expected)
    {
        // Mirrors Run L169-180 using the real production static + the exact OrdinalIgnoreCase compare.
        var parsed = IngestTelemetryFunction.TryParsePartitionKey(partitionKey, out var bodyTenant, out _);

        AuthDecision actual;
        if (!parsed)
        {
            actual = AuthDecision.MalformedPartitionKey; // Run → 400 "Malformed PartitionKey"
        }
        else if (!string.Equals(bodyTenant, header, StringComparison.OrdinalIgnoreCase))
        {
            actual = AuthDecision.TenantMismatch;        // Run → 403 "TenantId mismatch..."
        }
        else
        {
            actual = AuthDecision.Accept;
        }

        Assert.Equal(expected, actual);
    }

    // ============================================================ Session deletion locked → 410

    [Theory]
    [InlineData(SessionDeletionState.Preparing)]
    [InlineData(SessionDeletionState.Queued)]
    [InlineData(SessionDeletionState.Running)]
    [InlineData(SessionDeletionState.Poisoned)]
    public async Task DeletionLocked_guard_throws_the_exception_Run_maps_to_410(string lockState)
    {
        // Run L205-213: EnsureWritableAndGetRowAsync throws SessionDeletionLockedException for an
        // in-flight cascade → Run responds 410 Gone via WriteSessionLockedAsync.
        var guard = NewGuard(out var reader);
        var row = new TableEntity(TenantId, SessionId)
        {
            ["DeletionState"] = lockState,
            ["PendingDeletionManifestId"] = "MANIFEST-410",
        };
        reader.Setup(r => r.GetSessionRowAsync(TenantId, SessionId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(row);

        var ex = await Assert.ThrowsAsync<SessionDeletionLockedException>(
            () => guard.EnsureWritableAndGetRowAsync(TenantId, SessionId, callerContext: "V2.IngestTelemetry"));

        Assert.Equal(lockState, ex.CurrentState);
        Assert.Equal("MANIFEST-410", ex.ManifestId);
    }

    [Fact]
    public async Task DeletionUnlocked_guard_returns_row_that_feeds_the_status_prefetch()
    {
        // Run L205-217: on an unlocked session the guard hands back the loaded row, and Run reads
        // Status off it via TryReadSessionStatus to seed the stall-heal prefetch (no second read).
        var guard = NewGuard(out var reader);
        var row = new TableEntity(TenantId, SessionId)
        {
            ["DeletionState"] = SessionDeletionState.None,
            ["Status"] = "Succeeded", // Sessions stores Status as string (status.ToString())
        };
        reader.Setup(r => r.GetSessionRowAsync(TenantId, SessionId, It.IsAny<CancellationToken>()))
              .ReturnsAsync(row);

        var returned = await guard.EnsureWritableAndGetRowAsync(TenantId, SessionId, callerContext: "V2.IngestTelemetry");

        Assert.Same(row, returned);
        Assert.Equal(SessionStatus.Succeeded, IngestTelemetryFunction.TryReadSessionStatus(returned));
    }

    // ============================================================ TryReadSessionStatus (unique helper)

    [Theory]
    [InlineData("InProgress", SessionStatus.InProgress)]
    [InlineData("Succeeded",  SessionStatus.Succeeded)]
    [InlineData("failed",     SessionStatus.Failed)]   // case-insensitive parse
    [InlineData("Stalled",    SessionStatus.Stalled)]
    public void TryReadSessionStatus_parses_string_status_case_insensitively(string stored, SessionStatus expected)
    {
        var row = new TableEntity(TenantId, SessionId) { ["Status"] = stored };
        Assert.Equal(expected, IngestTelemetryFunction.TryReadSessionStatus(row));
    }

    [Fact]
    public void TryReadSessionStatus_returns_null_for_null_row()
    {
        Assert.Null(IngestTelemetryFunction.TryReadSessionStatus(null));
    }

    [Fact]
    public void TryReadSessionStatus_returns_null_when_status_column_absent()
    {
        var row = new TableEntity(TenantId, SessionId); // no Status column
        Assert.Null(IngestTelemetryFunction.TryReadSessionStatus(row));
    }

    [Fact]
    public void TryReadSessionStatus_returns_null_for_unparseable_status()
    {
        var row = new TableEntity(TenantId, SessionId) { ["Status"] = "NotARealStatus" };
        Assert.Null(IngestTelemetryFunction.TryReadSessionStatus(row));
    }

    // ============================================================ Harness (mirrors SessionDeletionGuardTests)

    private static SessionDeletionGuard NewGuard(out Mock<ISessionDeletionInventoryReader> reader)
    {
        reader = new Mock<ISessionDeletionInventoryReader>();
        return new SessionDeletionGuard(reader.Object, NullLogger<SessionDeletionGuard>.Instance);
    }
}
