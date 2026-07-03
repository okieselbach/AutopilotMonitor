using System.Collections.Generic;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// In-memory ISignalRNotificationService for unit tests. Records every call so tests can
/// assert that producer code (TenantNotificationService, GlobalNotificationService, …) emits
/// the right SignalR push when it should — without instantiating the real Azure SignalR
/// ServiceManager (whose constructor validates a connection string and is not test-friendly).
/// </summary>
public class FakeSignalRNotificationService : ISignalRNotificationService
{
    public List<(string TenantId, NotificationAudience Audience, object Dto)> TenantSends { get; } = new();
    public List<(string TenantId, string NotificationId)> TenantDismisses { get; } = new();
    public List<string> TenantDismissAlls { get; } = new();
    public List<object> GlobalSends { get; } = new();
    public List<string> GlobalDismisses { get; } = new();
    public int GlobalDismissAllCount { get; private set; }
    public List<(string TenantId, string SessionId, int Count)> RuleResultsCalls { get; } = new();
    public List<(string TenantId, string SessionId, string Risk)> VulnReportCalls { get; } = new();
    public List<(string TenantId, string SessionId)> SessionDeletedCalls { get; } = new();

    public Task NotifyRuleResultsAvailableAsync(string tenantId, string sessionId, int resultCount)
    {
        RuleResultsCalls.Add((tenantId, sessionId, resultCount));
        return Task.CompletedTask;
    }

    public Task NotifyVulnerabilityReportAvailableAsync(string tenantId, string sessionId, string overallRisk)
    {
        VulnReportCalls.Add((tenantId, sessionId, overallRisk));
        return Task.CompletedTask;
    }

    public Task NotifySessionDeletedAsync(string tenantId, string sessionId)
    {
        SessionDeletedCalls.Add((tenantId, sessionId));
        return Task.CompletedTask;
    }

    public Task SendTenantNotificationAsync(string tenantId, NotificationAudience audience, object dto)
    {
        TenantSends.Add((tenantId, audience, dto));
        return Task.CompletedTask;
    }

    public Task SendTenantNotificationDismissedAsync(string tenantId, string notificationId)
    {
        TenantDismisses.Add((tenantId, notificationId));
        return Task.CompletedTask;
    }

    public Task SendTenantNotificationDismissedAllAsync(string tenantId)
    {
        TenantDismissAlls.Add(tenantId);
        return Task.CompletedTask;
    }

    public Task SendGlobalNotificationAsync(object dto)
    {
        GlobalSends.Add(dto);
        return Task.CompletedTask;
    }

    public Task SendGlobalNotificationDismissedAsync(string notificationId)
    {
        GlobalDismisses.Add(notificationId);
        return Task.CompletedTask;
    }

    public Task SendGlobalNotificationDismissedAllAsync()
    {
        GlobalDismissAllCount++;
        return Task.CompletedTask;
    }

    public List<string> NegotiatedUserIds { get; } = new();
    public List<string> DisconnectedUsers { get; } = new();

    public Task<(string Url, string AccessToken)?> NegotiateClientAsync(string userId)
    {
        NegotiatedUserIds.Add(userId);
        return Task.FromResult<(string Url, string AccessToken)?>(("https://fake.signalr.test/client", "fake-token"));
    }

    public Task DisconnectUserAsync(string userId)
    {
        DisconnectedUsers.Add(userId);
        return Task.CompletedTask;
    }
}
