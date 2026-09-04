using System.Net;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Functions.Helpers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Global;

/// <summary>
/// API endpoints for managing persistent Global Admin notifications.
/// All endpoints require GlobalAdminOnly authorization (enforced by PolicyEnforcementMiddleware).
/// </summary>
public class GlobalNotificationsFunction
{
    private readonly ILogger<GlobalNotificationsFunction> _logger;
    private readonly GlobalNotificationService _notificationService;

    public GlobalNotificationsFunction(
        ILogger<GlobalNotificationsFunction> logger,
        GlobalNotificationService notificationService)
    {
        _logger = logger;
        _notificationService = notificationService;
    }

    /// <summary>
    /// GET /api/global/notifications
    /// Returns all active (non-dismissed) notifications, newest first.
    /// </summary>
    [Function("GetGlobalNotifications")]
    public async Task<HttpResponseData> GetNotifications(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "global/notifications")] HttpRequestData req)
    {
        var notifications = await _notificationService.GetActiveNotificationsAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new NotificationListResponse { Success = true, Notifications = notifications });
        return response;
    }

    /// <summary>
    /// POST /api/global/notifications/{notificationId}/dismiss
    /// Dismisses a single notification.
    /// </summary>
    [Function("DismissGlobalNotification")]
    public async Task<HttpResponseData> DismissNotification(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "global/notifications/{notificationId}/dismiss")] HttpRequestData req,
        string notificationId)
    {
        var found = await _notificationService.DismissNotificationAsync(notificationId);

        if (!found)
        {
            return await req.NotFoundAsync("Notification not found");
        }

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new SuccessOnlyResponse { Success = true });
        return response;
    }

    /// <summary>
    /// POST /api/global/notifications/dismiss-all
    /// Dismisses all active notifications.
    /// </summary>
    [Function("DismissAllGlobalNotifications")]
    public async Task<HttpResponseData> DismissAll(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "global/notifications/dismiss-all")] HttpRequestData req)
    {
        var dismissedCount = await _notificationService.DismissAllNotificationsAsync();

        var response = req.CreateResponse(HttpStatusCode.OK);
        await response.WriteAsJsonAsync(new DismissAllNotificationsResponse { Success = true, DismissedCount = dismissedCount });
        return response;
    }
}
