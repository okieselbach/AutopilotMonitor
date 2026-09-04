using System;
using System.Net;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Services;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Functions.Helpers;
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
            DeviceAssociationValidator? deviceAssociationValidator = null,
            CloudPcDeviceValidator? cloudPcDeviceValidator = null,
            IntuneDeviceBindingValidator? intuneDeviceBindingValidator = null)
        {
            var validator = new SecurityValidator(configService, adminConfigService, rateLimitService, autopilotDeviceValidator, corporateIdentifierValidator, logger, bootstrapSessionService, deviceAssociationValidator, cloudPcDeviceValidator, intuneDeviceBindingValidator);
            var validation = await validator.ValidateRequestAsync(req, tenantId, sessionId);

            if (!validation.IsValid)
            {
                // Agent-facing error envelope. The agent acts on the status and the Retry-After header
                // (503 retry loop, 429 backoff) and never parses these bodies beyond `errorCode` on the
                // RegisterSession response — so the envelope carries the human message (details folded
                // in) plus the retry window, nothing agent-specific.
                var message = string.IsNullOrEmpty(validation.Details)
                    ? validation.ErrorMessage ?? "Request rejected."
                    : $"{validation.ErrorMessage} ({validation.Details})";
                int? retryAfterSeconds = validation.StatusCode switch
                {
                    HttpStatusCode.TooManyRequests when validation.RateLimitResult?.RetryAfter is { } retryAfter
                        => (int)retryAfter.TotalSeconds,
                    HttpStatusCode.ServiceUnavailable => validation.RetryAfterSeconds,
                    _ => null,
                };

                var response = await req.ErrorAsync(
                    validation.StatusCode, ApiErrorWriter.DefaultCode(validation.StatusCode), message,
                    retryAfterSeconds: retryAfterSeconds);
                return (validation, response);
            }

            // Validation successful - no error response
            return (validation, null);
        }
    }
}
