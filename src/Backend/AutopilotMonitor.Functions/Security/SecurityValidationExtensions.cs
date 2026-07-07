using System;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace AutopilotMonitor.Functions.Security
{
    /// <summary>
    /// Extension methods for security validation on HTTP requests
    /// </summary>
    public static class SecurityValidationExtensions
    {
        /// <summary>
        /// Validates request security and creates error response if validation fails
        /// </summary>
        /// <param name="req">HTTP request</param>
        /// <param name="tenantId">Tenant ID</param>
        /// <param name="configService">Tenant configuration service</param>
        /// <param name="rateLimitService">Rate limit service</param>
        /// <param name="logger">Logger</param>
        /// <returns>Validation result with optional error response</returns>
        public static async Task<(SecurityValidationResult validation, HttpResponseData? errorResponse)> ValidateSecurityAsync(
            this HttpRequestData req,
            string tenantId,
            TenantConfigurationService configService,
            AdminConfigurationService adminConfigService,
            RateLimitService rateLimitService,
            AutopilotDeviceValidator autopilotDeviceValidator,
            CorporateIdentifierValidator corporateIdentifierValidator,
            ILogger logger,
            string? sessionId = null,
            BootstrapSessionService? bootstrapSessionService = null,
            DeviceAssociationValidator? deviceAssociationValidator = null)
        {
            var validator = new SecurityValidator(configService, adminConfigService, rateLimitService, autopilotDeviceValidator, corporateIdentifierValidator, logger, bootstrapSessionService, deviceAssociationValidator);
            var validation = await validator.ValidateRequestAsync(req, tenantId, sessionId);

            if (!validation.IsValid)
            {
                // Create appropriate error response
                var response = req.CreateResponse(validation.StatusCode);

                if (validation.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    // Rate limit error - include Retry-After header
                    if (validation.RateLimitResult?.RetryAfter.HasValue == true)
                    {
                        response.Headers.Add("Retry-After", ((int)validation.RateLimitResult.RetryAfter.Value.TotalSeconds).ToString());
                    }

                    await response.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = validation.ErrorMessage,
                        rateLimitExceeded = true,
                        rateLimitInfo = new
                        {
                            requestsInWindow = validation.RateLimitResult?.RequestsInWindow,
                            maxRequests = validation.RateLimitResult?.MaxRequests,
                            windowDurationSeconds = validation.RateLimitResult?.WindowDuration.TotalSeconds,
                            retryAfterSeconds = validation.RateLimitResult?.RetryAfter?.TotalSeconds
                        }
                    });
                }
                else if (validation.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    // Transient device validation failure — tell agent to retry
                    if (validation.RetryAfterSeconds.HasValue)
                    {
                        response.Headers.Add("Retry-After", validation.RetryAfterSeconds.Value.ToString());
                    }

                    await response.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = validation.ErrorMessage,
                        details = validation.Details,
                        retryAfterSeconds = validation.RetryAfterSeconds
                    });
                }
                else
                {
                    // Other security errors
                    await response.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = validation.ErrorMessage,
                        details = validation.Details
                    });
                }

                return (validation, response);
            }

            // Validation successful - no error response
            return (validation, null);
        }
    }
}
