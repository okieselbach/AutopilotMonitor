using System.Text.Json;
using AutopilotMonitor.Shared.Models;
using Microsoft.Azure.Functions.Worker.Http;
using JsonConvert = Newtonsoft.Json.JsonConvert;

namespace AutopilotMonitor.Functions.Helpers;

/// <summary>
/// Outcome of reading a request body: <see cref="Value"/> or <see cref="Error"/> (both null only
/// for an optional body that was absent).
/// The error is a finished 400 envelope (<see cref="ResponseHelper.BadRequestAsync"/>) — the
/// handler returns it as-is: <c>if (body.Error != null) return body.Error;</c>.
/// </summary>
public sealed class BodyResult<T> where T : class
{
    private BodyResult(T? value, HttpResponseData? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }
    public HttpResponseData? Error { get; }

    public static BodyResult<T> Ok(T value) => new(value, null);
    public static BodyResult<T> Fail(HttpResponseData error) => new(null, error);
    /// <summary>No body and no error — only <c>ReadOptionalAsync</c> produces this.</summary>
    public static BodyResult<T> Empty() => new(null, null);
}

/// <summary>
/// The one way a function reads an HTTP request body. The generic overloads constrain on
/// <see cref="IApiRequest"/>, so every body type is a Shared wire type (exported to TypeScript
/// by SharedManifestParityTests); TypedRequestGuardTests refuses the raw deserializers in
/// handlers. Empty, malformed or non-object bodies are a 400 envelope here, never a 500 from an
/// unhandled <see cref="JsonException"/>. The optional byte cap is per site (a session report
/// carries base64 attachments; most admin bodies fit in a kilobyte).
/// </summary>
public static class RequestBody
{
    private const string RequiredMessage = "Request body is required";
    private const string InvalidMessage = "Invalid JSON body";
    private const string TooLargeMessage = "Request body too large";

    /// <summary>System.Text.Json with <see cref="ApiJsonOptions.Read"/> — the default reader.</summary>
    public static async Task<BodyResult<T>> ReadAsync<T>(this HttpRequestData req, long? maxBytes = null)
        where T : class, IApiRequest
    {
        var text = await ReadTextAsync(req, maxBytes);
        if (text.Error != null) return BodyResult<T>.Fail(text.Error);

        T? value;
        try
        {
            value = JsonSerializer.Deserialize<T>(text.Value!, ApiJsonOptions.Read);
        }
        catch (JsonException)
        {
            return BodyResult<T>.Fail(await req.BadRequestAsync(InvalidMessage));
        }

        return value == null
            ? BodyResult<T>.Fail(await req.BadRequestAsync(RequiredMessage))
            : BodyResult<T>.Ok(value);
    }

    /// <summary>
    /// Like <see cref="ReadAsync{T}"/>, but an empty body is a legitimate "nothing" (Value = null,
    /// no error): endpoints whose body only carries optional overrides (a download ticket that may
    /// name the blob by query, a revert defaulting to the latest backup, a usage-plan reset).
    /// A malformed body is still a 400.
    /// </summary>
    public static async Task<BodyResult<T>> ReadOptionalAsync<T>(this HttpRequestData req, long? maxBytes = null)
        where T : class, IApiRequest
    {
        var text = await ReadTextAsync(req, maxBytes, allowEmpty: true);
        if (text.Error != null) return BodyResult<T>.Fail(text.Error);
        if (text.Value == null) return BodyResult<T>.Empty();

        try
        {
            var value = JsonSerializer.Deserialize<T>(text.Value, ApiJsonOptions.Read);
            return value == null ? BodyResult<T>.Empty() : BodyResult<T>.Ok(value);
        }
        catch (JsonException)
        {
            return BodyResult<T>.Fail(await req.BadRequestAsync(InvalidMessage));
        }
    }

    /// <summary>
    /// Newtonsoft reader for the bodies whose downstream is Newtonsoft-bound (tenant/admin
    /// configuration through <c>TenantConfigPatchService</c>, the rule documents). Same contract
    /// and errors as <see cref="ReadAsync{T}"/>; TypedRequestGuardTests carries the shrinking
    /// baseline of its call sites — a new site is a regression, port the downstream instead.
    /// </summary>
    public static async Task<BodyResult<T>> ReadNewtonsoftAsync<T>(this HttpRequestData req, long? maxBytes = null)
        where T : class, IApiRequest
    {
        var text = await ReadTextAsync(req, maxBytes);
        if (text.Error != null) return BodyResult<T>.Fail(text.Error);

        T? value;
        try
        {
            value = JsonConvert.DeserializeObject<T>(text.Value!);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return BodyResult<T>.Fail(await req.BadRequestAsync(InvalidMessage));
        }

        return value == null
            ? BodyResult<T>.Fail(await req.BadRequestAsync(RequiredMessage))
            : BodyResult<T>.Ok(value);
    }

    /// <summary>
    /// The body as a <see cref="JsonDocument"/> whose root is an object — only for the one
    /// PATCH whose semantics distinguish an absent key from an explicit null (tenant plan);
    /// its key names come from the request DTO by <c>nameof</c>, see PlanManagementFunction.
    /// The caller disposes the document.
    /// </summary>
    public static async Task<BodyResult<JsonDocument>> ReadDocumentAsync(this HttpRequestData req, long? maxBytes = null)
    {
        var text = await ReadTextAsync(req, maxBytes);
        if (text.Error != null) return BodyResult<JsonDocument>.Fail(text.Error);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text.Value!);
        }
        catch (JsonException)
        {
            return BodyResult<JsonDocument>.Fail(await req.BadRequestAsync(InvalidMessage));
        }

        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            doc.Dispose();
            return BodyResult<JsonDocument>.Fail(await req.BadRequestAsync("Body must be a JSON object"));
        }

        return BodyResult<JsonDocument>.Ok(doc);
    }

    private static async Task<BodyResult<string>> ReadTextAsync(HttpRequestData req, long? maxBytes, bool allowEmpty = false)
    {
        if (maxBytes is long cap
            && req.Headers.TryGetValues("Content-Length", out var values)
            && long.TryParse(values.FirstOrDefault(), out var contentLength)
            && contentLength > cap)
        {
            return BodyResult<string>.Fail(await req.BadRequestAsync(TooLargeMessage));
        }

        var text = await req.ReadAsStringAsync();
        if (!string.IsNullOrWhiteSpace(text)) return BodyResult<string>.Ok(text!);
        return allowEmpty
            ? BodyResult<string>.Empty()
            : BodyResult<string>.Fail(await req.BadRequestAsync(RequiredMessage));
    }
}
