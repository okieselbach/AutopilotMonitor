using System.Net;
using System.Text.Json;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The middleware-side error-envelope writer: status, content type, Retry-After and the exact body
/// (production wire options — camelCase, absent-when-null) for the generic envelope and for a
/// specialised <see cref="IApiErrorResponse"/>. Whatever the authentication, policy, rate-limit and
/// quota middlewares write flows through here, so this is the one place their body shape is pinned.
/// </summary>
public class ApiErrorWriterTests
{
    [Fact]
    public async Task Generic_envelope_writes_status_content_type_and_the_three_prefix_keys()
    {
        var ctx = NewContext();

        await ApiErrorWriter.WriteAsync(ctx, "cid-42", HttpStatusCode.Unauthorized,
            Constants.ApiErrorCodes.AuthenticationRequired, "Authentication required.");

        Assert.Equal(401, ctx.Response.StatusCode);
        Assert.StartsWith("application/json", ctx.Response.ContentType);
        Assert.False(ctx.Response.Headers.ContainsKey("Retry-After"));
        Assert.Equal(
            "{\"error\":\"Authentication required.\",\"code\":\"AuthenticationRequired\",\"correlationId\":\"cid-42\"}",
            Body(ctx));
    }

    [Fact]
    public async Task Generic_envelope_with_retry_window_sets_the_header_and_mirrors_it_in_the_body()
    {
        var ctx = NewContext();

        await ApiErrorWriter.WriteAsync(ctx, "cid-7", HttpStatusCode.TooManyRequests,
            Constants.ApiErrorCodes.RateLimited, "Rate limit exceeded: 60 requests per minute",
            retryAfterSeconds: 23);

        Assert.Equal(429, ctx.Response.StatusCode);
        Assert.Equal("23", ctx.Response.Headers["Retry-After"]);
        Assert.Equal(
            "{\"error\":\"Rate limit exceeded: 60 requests per minute\",\"code\":\"RateLimited\",\"correlationId\":\"cid-7\",\"retryAfterSeconds\":23}",
            Body(ctx));
    }

    [Fact]
    public async Task Generic_envelope_carries_hint_and_operation_only_when_given()
    {
        var ctx = NewContext();

        await ApiErrorWriter.WriteAsync(ctx, "cid-9", HttpStatusCode.InternalServerError,
            Constants.ApiErrorCodes.InternalError, "QueryTable failed.",
            hint: "Retry with a narrower filter.", operation: "QueryTable");

        Assert.Equal(
            "{\"error\":\"QueryTable failed.\",\"code\":\"InternalError\",\"correlationId\":\"cid-9\"," +
            "\"hint\":\"Retry with a narrower filter.\",\"operation\":\"QueryTable\"}",
            Body(ctx));
    }

    [Fact]
    public async Task Specialised_body_is_stamped_with_the_correlation_id_and_serialized_as_its_runtime_type()
    {
        var ctx = NewContext();
        var body = new DelegatedSlotLimitReachedResponse
        {
            Error = "Slot limit reached.",
            HomeTenantId = "7aa20c11-0002-4b7c-a1d2-52f3aaaa0001",
            Used = 2,
            Limit = 2,
            Required = 1,
        };

        await ApiErrorWriter.WriteAsync(ctx, "cid-slot", HttpStatusCode.Conflict, body);

        Assert.Equal(409, ctx.Response.StatusCode);
        Assert.Equal("cid-slot", body.CorrelationId);
        var json = JsonDocument.Parse(Body(ctx)).RootElement;
        // Envelope prefix first, then the specialised fields — and nothing of the marker interface.
        var keys = json.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(new[] { "error", "code", "correlationId", "homeTenantId", "used", "limit", "required" }, keys);
        Assert.Equal("DelegatedSlotLimitReached", json.GetProperty("code").GetString());
    }

    private static DefaultHttpContext NewContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string Body(HttpContext ctx)
    {
        ctx.Response.Body.Position = 0;
        using var reader = new StreamReader(ctx.Response.Body);
        return reader.ReadToEnd();
    }
}
