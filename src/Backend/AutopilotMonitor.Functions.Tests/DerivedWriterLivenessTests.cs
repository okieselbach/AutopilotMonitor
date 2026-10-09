using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Functions.Services.Analyze;
using AutopilotMonitor.Functions.Services.Deletion;
using AutopilotMonitor.Functions.Services.Vulnerability;
using AutopilotMonitor.Shared.DataAccess;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Deletion;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The two queue handlers derive rows for a session (rule results, reports, inventory counters,
/// CveIndex). For a session the backend does not know, or one a cascade is tearing down, they
/// must stop before the first read of session data — nothing derived may ever outlive the
/// session's cascade. Both handlers are driven up to that gate with every later dependency
/// unavailable, so a regression that moves the gate shows up as a null dereference or an
/// unexpected mock call.
/// </summary>
public class DerivedWriterLivenessTests
{
    private const string TenantId  = "11111111-1111-1111-1111-111111111111";
    private const string SessionId = "22222222-2222-2222-2222-222222222222";

    private static SessionDeletionGuard GuardFor(TableEntity? row)
    {
        var reader = new Mock<ISessionDeletionInventoryReader>();
        reader.Setup(r => r.GetSessionRowAsync(TenantId, SessionId, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
              .ReturnsAsync(row);
        return new SessionDeletionGuard(reader.Object, NullLogger<SessionDeletionGuard>.Instance);
    }

    private static Mock<AdminConfigurationService> AdminConfigWithCorrelationEnabled()
    {
        var adminConfig = new Mock<AdminConfigurationService>(
            Mock.Of<IConfigRepository>(), NullLogger<AdminConfigurationService>.Instance,
            new MemoryCache(new MemoryCacheOptions()));
        adminConfig.Setup(a => a.GetConfigurationAsync())
            .ReturnsAsync(new AdminConfiguration { VulnerabilityCorrelationEnabled = true });
        return adminConfig;
    }

    public static IEnumerable<object?[]> NotLiveRows()
    {
        yield return new object?[] { null };
        yield return new object?[] { new TableEntity(TenantId, SessionId) { ["DeletionState"] = SessionDeletionState.Running } };
    }

    [Theory]
    [MemberData(nameof(NotLiveRows))]
    public async Task VulnerabilityCorrelate_skips_a_missing_or_locked_session_before_loading_inventory(TableEntity? row)
    {
        var inventoryLoader = new Mock<IVulnerabilityInventoryLoader>(MockBehavior.Strict);
        var vulnRepo = new Mock<IVulnerabilityRepository>(MockBehavior.Strict);
        var handler = new VulnerabilityCorrelateHandler(
            AdminConfigWithCorrelationEnabled().Object,
            vulnerabilityCorrelation: null!,
            vulnRepo.Object,
            Mock.Of<ISessionRepository>(),
            inventoryLoader.Object,
            Mock.Of<IAnalyzeOnEnrollmentEndProducer>(),
            signalRNotification: null!,
            GuardFor(row),
            NullLogger<VulnerabilityCorrelateHandler>.Instance);

        await handler.HandleAsync(new VulnerabilityCorrelateEnvelope
        {
            TenantId = TenantId, SessionId = SessionId, Reason = VulnerabilityCorrelateHandler.ReasonShutdownInventory, EnqueuedAt = DateTime.UtcNow,
        });

        inventoryLoader.VerifyNoOtherCalls();
        vulnRepo.VerifyNoOtherCalls();
    }

    [Theory]
    [MemberData(nameof(NotLiveRows))]
    public async Task Analyze_skips_a_missing_or_locked_session_before_the_rule_engine_runs(TableEntity? row)
    {
        var ruleRepo = new Mock<IRuleRepository>(MockBehavior.Strict);
        var metricsRepo = new Mock<IMetricsRepository>(MockBehavior.Strict);
        var handler = new AnalyzeOnEnrollmentEndHandler(
            ruleService: null!,
            ruleRepo.Object,
            Mock.Of<ISessionRepository>(),
            metricsRepo.Object,
            signalRNotification: null!,
            configService: null!,
            channelDispatcher: null!,
            GuardFor(row),
            NullLogger<AnalyzeOnEnrollmentEndHandler>.Instance);

        await handler.HandleAsync(new AnalyzeOnEnrollmentEndEnvelope
        {
            TenantId = TenantId, SessionId = SessionId, Reason = AnalyzeOnEnrollmentEndHandler.ReasonEnrollmentComplete, EnqueuedAt = DateTime.UtcNow,
        });

        ruleRepo.VerifyNoOtherCalls();
        metricsRepo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task A_guard_read_error_propagates_so_the_queue_retries_instead_of_skipping()
    {
        var reader = new Mock<ISessionDeletionInventoryReader>();
        reader.Setup(r => r.GetSessionRowAsync(TenantId, SessionId, It.IsAny<IEnumerable<string>>(), It.IsAny<CancellationToken>()))
              .ThrowsAsync(new Azure.RequestFailedException(503, "busy"));
        var handler = new AnalyzeOnEnrollmentEndHandler(
            ruleService: null!, Mock.Of<IRuleRepository>(), Mock.Of<ISessionRepository>(), Mock.Of<IMetricsRepository>(),
            signalRNotification: null!, configService: null!, channelDispatcher: null!,
            new SessionDeletionGuard(reader.Object, NullLogger<SessionDeletionGuard>.Instance),
            NullLogger<AnalyzeOnEnrollmentEndHandler>.Instance);

        await Assert.ThrowsAsync<Azure.RequestFailedException>(() => handler.HandleAsync(new AnalyzeOnEnrollmentEndEnvelope
        {
            TenantId = TenantId, SessionId = SessionId, Reason = AnalyzeOnEnrollmentEndHandler.ReasonEnrollmentComplete, EnqueuedAt = DateTime.UtcNow,
        }));
    }
}
