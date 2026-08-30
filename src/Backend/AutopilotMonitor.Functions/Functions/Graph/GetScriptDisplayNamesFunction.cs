using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AutopilotMonitor.Functions.Helpers;
using AutopilotMonitor.Functions.Services.GraphResolution;
using AutopilotMonitor.Shared.Models;
using AutopilotMonitor.Shared.Models.Graph;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace AutopilotMonitor.Functions.Functions.Graph;

/// <summary>
/// <c>POST /api/tenants/{tenantId}/scripts/display-names</c> with body
/// <c>{ "refs": ["Platform:{id}", "Remediation:{id}", ...] }</c> — resolves Intune script
/// display names for the supplied refs. POST (not GET) so even sessions with many
/// remediation scripts can fit comfortably in the request body without bumping into
/// URL-length limits at the browser or Azure Functions front door.
/// <para>
/// Output is a flat dictionary keyed by the canonical ref string; unresolved entries
/// (permission missing, NotFound, transient Graph failure) are returned as <c>null</c>.
/// The endpoint always returns 200 with a partial result rather than 4xx/5xx so the
/// timeline UI can keep rendering IDs and just patch in names where available.
/// </para>
/// </summary>
public class GetScriptDisplayNamesFunction
{
    /// <summary>Hard cap so a single request can't trigger arbitrarily many Graph fallback fetches.</summary>
    internal const int MaxRefsPerRequest = 200;

    private readonly ILogger<GetScriptDisplayNamesFunction> _logger;
    private readonly IScriptDisplayNameResolver _resolver;

    public GetScriptDisplayNamesFunction(
        ILogger<GetScriptDisplayNamesFunction> logger,
        IScriptDisplayNameResolver resolver)
    {
        _logger = logger;
        _resolver = resolver;
    }

    [Function("GetScriptDisplayNames")]
    public async Task<HttpResponseData> Run(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post",
            Route = "tenants/{tenantId}/scripts/display-names")] HttpRequestData req,
        string tenantId)
    {
        try
        {
            var requestCtx = req.GetRequestContext();

            string rawBody;
            using (var sr = new System.IO.StreamReader(req.Body))
            {
                rawBody = await sr.ReadToEndAsync();
            }
            if (string.IsNullOrWhiteSpace(rawBody))
            {
                return await req.OkAsync(new GetScriptDisplayNamesResponse { Refs = new Dictionary<string, string?>() });
            }

            RequestBody? body;
            try
            {
                body = JsonConvert.DeserializeObject<RequestBody>(rawBody);
            }
            catch (JsonException ex)
            {
                return await req.BadRequestAsync($"Request body is not valid JSON: {ex.Message}");
            }

            if (body?.Refs == null || body.Refs.Count == 0)
            {
                return await req.OkAsync(new GetScriptDisplayNamesResponse { Refs = new Dictionary<string, string?>() });
            }

            var parsedRefs = new List<ScriptRef>();
            var malformed = new List<string>();
            foreach (var token in body.Refs)
            {
                if (string.IsNullOrWhiteSpace(token)) continue;
                if (ScriptRef.TryParse(token, out var r)) parsedRefs.Add(r);
                else malformed.Add(token);
            }

            if (parsedRefs.Count > MaxRefsPerRequest)
            {
                return await req.BadRequestAsync(
                    $"Too many refs in a single request (got {parsedRefs.Count}, max {MaxRefsPerRequest}).");
            }

            var distinctRefs = parsedRefs.Distinct().ToList();

            var resolved = await _resolver.ResolveAsync(requestCtx.TargetTenantId, distinctRefs, default);

            var payload = new Dictionary<string, string?>(distinctRefs.Count);
            foreach (var r in distinctRefs)
            {
                payload[r.ToString()] = resolved.TryGetValue(r, out var name) ? name : null;
            }

            return await req.OkAsync(new GetScriptDisplayNamesResponse
            {
                Refs = payload,
                Malformed = malformed.Count > 0 ? malformed : null,
            });
        }
        catch (Exception ex)
        {
            return await req.InternalServerErrorAsync(_logger, ex,
                $"Resolve script display names for tenant '{tenantId}'");
        }
    }

    /// <summary>POST body shape.</summary>
    private sealed class RequestBody
    {
        [JsonProperty("refs")]
        public List<string>? Refs { get; set; }
    }
}
