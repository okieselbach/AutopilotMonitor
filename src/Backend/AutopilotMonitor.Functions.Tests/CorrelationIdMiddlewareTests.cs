using System.Net;
using System.Text;
using System.Text.Json;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AutopilotMonitor.Functions.Tests;

/// <summary>
/// The agent treats a 401/403 as this backend's verdict only when the response echoes the
/// <c>X-Correlation-ID</c> it sent; the platform's own pages and a proxy's pages carry none. The
/// middleware stamps the header onto the shared <see cref="HttpContext"/> response before the rest
/// of the pipeline runs, so the stamp does not depend on the status a later step writes: a
/// middleware envelope (<see cref="ApiErrorWriter"/>) and a function response alike — with the
/// ASP.NET Core integration a worker <c>HttpResponseData</c> is a view over that same response,
/// and its header set starts from the headers already on it.
/// </summary>
public class CorrelationIdMiddlewareTests
{
    private const string HeaderName = "X-Correlation-ID";

    /// <summary>The agent's format: a fresh <c>Guid.ToString("N")</c> per request.</summary>
    private const string AgentCorrelationId = "7c9e6679a1b24f3e8d5c0b1a2f3e4d5c";

    private sealed class Run
    {
        public required HttpContext Http { get; init; }
        public required IDictionary<object, object> Items { get; init; }

        public string EchoedId => Http.Response.Headers[HeaderName].ToString();

        public string Body()
        {
            Http.Response.Body.Position = 0;
            return new StreamReader(Http.Response.Body, Encoding.UTF8).ReadToEnd();
        }
    }

    /// <summary>Runs the middleware; <paramref name="answer"/> is whatever answers the request after it.</summary>
    private static async Task<Run> InvokeAsync(string? inboundId, Func<FunctionContext, HttpContext, Task> answer)
    {
        var http = new DefaultHttpContext();
        http.Response.Body = new MemoryStream();
        if (inboundId != null)
            http.Request.Headers[HeaderName] = inboundId;
        var items = new Dictionary<object, object> { ["HttpRequestContext"] = http };
        var context = new Mock<FunctionContext>();
        context.SetupGet(c => c.Items).Returns(items);

        var sut = new CorrelationIdMiddleware(NullLogger<CorrelationIdMiddleware>.Instance);
        await sut.Invoke(context.Object, ctx => answer(ctx, http));
        return new Run { Http = http, Items = items };
    }

    /// <summary>A function's answer: status and JSON body on the shared response.</summary>
    private static Func<FunctionContext, HttpContext, Task> FunctionAnswers(HttpStatusCode status) => (_, http) =>
    {
        http.Response.StatusCode = (int)status;
        http.Response.ContentType = "application/json";
        return http.Response.WriteAsync("{\"success\":false}");
    };

    // ── A valid inbound id is echoed on every status ─────────────────────────────

    [Theory]
    [InlineData(HttpStatusCode.OK)]           // ingest 2xx
    [InlineData(HttpStatusCode.Unauthorized)] // ValidateSecurityAsync → ResponseHelper.ErrorAsync
    [InlineData(HttpStatusCode.Forbidden)]    // the same, plus RegisterSession's and GetDiagnosticsUploadUrl's own 403 bodies
    public async Task A_valid_inbound_id_is_echoed_whatever_status_the_function_answers_with(HttpStatusCode status)
    {
        var run = await InvokeAsync(AgentCorrelationId, FunctionAnswers(status));

        Assert.Equal((int)status, run.Http.Response.StatusCode);
        Assert.Equal(AgentCorrelationId, run.EchoedId);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_valid_inbound_id_is_echoed_on_a_middleware_error_envelope_and_in_its_body(HttpStatusCode status)
    {
        var run = await InvokeAsync(AgentCorrelationId, (ctx, http) => ApiErrorWriter.WriteAsync(
            http, ctx.GetCorrelationId(), status, ApiErrorWriter.DefaultCode(status), "Request rejected."));

        Assert.Equal((int)status, run.Http.Response.StatusCode);
        Assert.Equal(AgentCorrelationId, run.EchoedId);
        using var body = JsonDocument.Parse(run.Body());
        Assert.Equal(AgentCorrelationId, body.RootElement.GetProperty("correlationId").GetString());
    }

    public static TheoryData<string> InsideTheAllowList => new()
    {
        AgentCorrelationId,
        "Az_09-mixed",
        new string('a', 128),
    };

    [Theory]
    [MemberData(nameof(InsideTheAllowList))]
    public async Task Every_id_inside_the_allow_list_is_echoed_verbatim(string inboundId)
    {
        var run = await InvokeAsync(inboundId, FunctionAnswers(HttpStatusCode.Forbidden));

        Assert.Equal(inboundId, run.EchoedId);
        Assert.Equal(inboundId, run.Items["CorrelationId"]);
    }

    // ── Anything else is replaced, never echoed ──────────────────────────────────

    public static TheoryData<string> OutsideTheAllowList => new()
    {
        "",
        "7c9e6679 a1b2",
        "7c9e6679\r\nX-Forged: 1",
        "7c9e6679\n",                 // .NET's `$` also matches before a final newline; the gate anchors with `\z`
        "7c9e6679.a1b2",
        "7c9e6679-ä",
        new string('a', 129),
    };

    [Theory]
    [MemberData(nameof(OutsideTheAllowList))]
    public async Task An_inbound_id_outside_the_allow_list_is_replaced_not_echoed(string inboundId)
    {
        var run = await InvokeAsync(inboundId, FunctionAnswers(HttpStatusCode.Forbidden));

        Assert.NotEqual(inboundId, run.EchoedId);
        AssertFreshId(run);
    }

    [Fact]
    public async Task A_request_without_an_id_gets_a_fresh_one()
    {
        var run = await InvokeAsync(null, FunctionAnswers(HttpStatusCode.OK));

        AssertFreshId(run);
    }

    /// <summary>A minted id is a compact GUID, and the request's logs and error bodies carry the same value.</summary>
    private static void AssertFreshId(Run run)
    {
        Assert.Matches("^[0-9a-f]{32}$", run.EchoedId);
        Assert.Equal(run.EchoedId, run.Items["CorrelationId"]);
    }
}
