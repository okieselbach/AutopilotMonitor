using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AutopilotMonitor.Functions.Helpers;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Functions.Raw
{
    public class AppInsightsQueryFunction
    {
        private readonly ILogger<AppInsightsQueryFunction> _logger;
        private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };
        private static readonly DefaultAzureCredential _credential = new();

        // Application Insights REST API scope for Azure AD auth
        private const string AppInsightsScope = "https://api.applicationinsights.io/.default";

        public AppInsightsQueryFunction(ILogger<AppInsightsQueryFunction> logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// POST /api/global/raw/logs — Proxy KQL queries to Application Insights
        /// Body: { "query": "traces | where ...", "timespan": "PT1H" }
        /// Uses Managed Identity (DefaultAzureCredential) for authentication.
        /// Requires APPINSIGHTS_APP_ID app setting and "Monitoring Reader" RBAC role.
        /// </summary>
        [Function("QueryBackendLogs")]
        public async Task<HttpResponseData> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "global/raw/logs")] HttpRequestData req,
            FunctionContext context)
        {
            // Authorization is the catalog (GlobalAdminOnly) + PolicyEnforcementMiddleware; this is the
            // in-body re-check so a middleware regression can never expose the KQL proxy.
            if (await RawGlobalAdminGate.DenyUnlessGlobalAdminAsync(req, context) is { } denied)
                return denied;

            try
            {
                var appId = Environment.GetEnvironmentVariable("APPINSIGHTS_APP_ID");

                if (string.IsNullOrEmpty(appId))
                {
                    var unavailable = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
                    await unavailable.WriteAsJsonAsync(new { error = "Application Insights diagnostics is not configured. Set the APPINSIGHTS_APP_ID app setting and assign 'Monitoring Reader' role to the Function App's Managed Identity." });
                    return unavailable;
                }

                var body = await req.ReadFromJsonAsync<LogQueryRequest>();
                if (body == null || string.IsNullOrWhiteSpace(body.Query))
                {
                    var bad = req.CreateResponse(HttpStatusCode.BadRequest);
                    await bad.WriteAsJsonAsync(new { error = "query is required" });
                    return bad;
                }

                var timespan = body.Timespan ?? "PT1H";

                // Acquire Bearer token via Managed Identity / DefaultAzureCredential
                var tokenResult = await _credential.GetTokenAsync(
                    new Azure.Core.TokenRequestContext(new[] { AppInsightsScope }));

                // Call Application Insights REST API
                var aiUrl = $"https://api.applicationinsights.io/v1/apps/{appId}/query";
                var requestBody = JsonSerializer.Serialize(new
                {
                    query = body.Query,
                    timespan
                });

                using var aiRequest = new HttpRequestMessage(HttpMethod.Post, aiUrl);
                aiRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenResult.Token);
                aiRequest.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");

                using var aiResponse = await _httpClient.SendAsync(aiRequest);
                var responseText = await aiResponse.Content.ReadAsStringAsync();

                if (!aiResponse.IsSuccessStatusCode)
                {
                    _logger.LogWarning("App Insights query failed: {StatusCode} {Response}",
                        aiResponse.StatusCode, responseText);

                    // Extract the meaningful error message from AppInsights error JSON
                    // Format: { "error": { "message": "...", "code": "...", "innererror": { "code": "SyntaxError", "message": "..." } } }
                    var errorMessage = "Application Insights query failed";
                    var errorCode = (string?)null;
                    try
                    {
                        using var doc = JsonDocument.Parse(responseText);
                        if (doc.RootElement.TryGetProperty("error", out var errorObj))
                        {
                            if (errorObj.TryGetProperty("innererror", out var inner) &&
                                inner.TryGetProperty("message", out var innerMsg))
                            {
                                errorMessage = innerMsg.GetString() ?? errorMessage;
                            }
                            else if (errorObj.TryGetProperty("message", out var msg))
                            {
                                errorMessage = msg.GetString() ?? errorMessage;
                            }

                            if (errorObj.TryGetProperty("innererror", out var innerErr) &&
                                innerErr.TryGetProperty("code", out var innerCode))
                            {
                                errorCode = innerCode.GetString();
                            }
                            else if (errorObj.TryGetProperty("code", out var code))
                            {
                                errorCode = code.GetString();
                            }
                        }
                    }
                    catch
                    {
                        // responseText is not JSON — keep generic message
                    }

                    var errorResp = req.CreateResponse(HttpStatusCode.BadGateway);
                    await errorResp.WriteAsJsonAsync(new
                    {
                        error = errorMessage,
                        errorCode,
                        statusCode = (int)aiResponse.StatusCode,
                        hint = errorCode == "SyntaxError"
                            ? "The KQL query has a syntax error. Check operators, pipe stages, and string quoting. Example: traces | where message contains 'error' | take 50"
                            : errorCode == "SemanticError"
                                ? "The KQL query references an unknown table or column. Valid tables: traces, requests, exceptions, customEvents, dependencies."
                                : (string?)null,
                    });
                    return errorResp;
                }

                var response = req.CreateResponse(HttpStatusCode.OK);
                response.Headers.Add("Content-Type", "application/json");
                await response.WriteStringAsync(responseText);
                return response;
            }
            catch (TaskCanceledException)
            {
                var timeout = req.CreateResponse(HttpStatusCode.GatewayTimeout);
                await timeout.WriteAsJsonAsync(new { error = "Application Insights query timed out (30s limit)" });
                return timeout;
            }
            catch (Azure.Identity.CredentialUnavailableException ex)
            {
                _logger.LogError(ex, "Managed Identity not available for App Insights query");
                var err = req.CreateResponse(HttpStatusCode.ServiceUnavailable);
                await err.WriteAsJsonAsync(new { error = "Managed Identity is not configured. Enable System-assigned Managed Identity on the Function App and assign 'Monitoring Reader' role on the Application Insights resource." });
                return err;
            }
            catch (Exception ex)
            {
                return await req.InternalServerErrorAsync(_logger, ex, "Query Application Insights");
            }
        }

        private class LogQueryRequest
        {
            public string Query { get; set; } = string.Empty;
            public string? Timespan { get; set; }
        }
    }
}
