using System.Text;
using System.Text.Json;
using AutopilotMonitor.Shared;
using AutopilotMonitor.Shared.Models;
using Microsoft.AspNetCore.Http;
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
/// handlers. Empty, malformed, non-object or incomplete bodies (the client aborted mid-body) are
/// a 400 envelope here, never a 500 from an unhandled <see cref="JsonException"/> or
/// <see cref="BadHttpRequestException"/>. Every read is capped on the bytes it actually reads,
/// after UseRequestDecompression, so neither chunked transfer nor Content-Encoding gets past the
/// cap. The cap is per site (a session report carries base64 attachments), and
/// <see cref="DefaultMaxBytes"/> applies where a site names none.
/// </summary>
public static class RequestBody
{
    /// <summary>
    /// The cap of a site that names none. Every such body is a small admin or portal object
    /// (strings, short lists); the largest, an email template, stays far below it.
    /// </summary>
    public const long DefaultMaxBytes = 1_048_576;

    /// <summary>
    /// The worker's request body limit: Kestrel's MaxRequestBodySize, set in
    /// <c>AddWorkerHttpPipeline</c>, which UseRequestDecompression also applies to the inflated body.
    /// It equals the largest cap a route reads with; a route cap above it would never be reached.
    /// </summary>
    public const long WorkerMaxBytes = Constants.SubmissionLimits.ReportRequestMaxBytes;

    private const string RequiredMessage = "Request body is required";
    private const string InvalidMessage = "Invalid JSON body";
    private const string TooLargeMessage = "Request body too large";
    private const string IncompleteMessage = "Request body incomplete";

    /// <summary>System.Text.Json with <see cref="ApiJsonOptions.Read"/> — the default reader.</summary>
    public static async Task<BodyResult<T>> ReadAsync<T>(this HttpRequestData req, long maxBytes = DefaultMaxBytes)
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
    public static async Task<BodyResult<T>> ReadOptionalAsync<T>(this HttpRequestData req, long maxBytes = DefaultMaxBytes)
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
    public static async Task<BodyResult<T>> ReadNewtonsoftAsync<T>(this HttpRequestData req, long maxBytes = DefaultMaxBytes)
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
    public static async Task<BodyResult<JsonDocument>> ReadDocumentAsync(this HttpRequestData req, long maxBytes = DefaultMaxBytes)
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

    /// <summary>
    /// The body, buffered under <paramref name="maxBytes"/>, for the routes that parse with their own
    /// reader (the agent wire, a foreign schema). Null when the body is larger; an aborted body throws
    /// <see cref="BadHttpRequestException"/> as before. Same counting as every read above.
    /// </summary>
    public static Task<MemoryStream?> ReadCappedAsync(this HttpRequestData req, long maxBytes)
        => ReadWithinAsync(req.Body, maxBytes);

    /// <summary>
    /// Buffers <paramref name="body"/> while counting the bytes actually read and asks for at most one
    /// byte past the cap — that byte alone proves the body too large. The count runs on what the
    /// handler receives, after UseRequestDecompression, so a missing (chunked) or compressed
    /// Content-Length changes nothing. Null when the body is larger than <paramref name="maxBytes"/>
    /// (a body exactly at the cap is read) or when the worker's own limit refused it first: Kestrel and
    /// the decompression stream raise a 413 <see cref="BadHttpRequestException"/>. Every other
    /// exception propagates, so an aborted body stays the caller's "incomplete".
    /// </summary>
    internal static async Task<MemoryStream?> ReadWithinAsync(Stream body, long maxBytes)
    {
        var buffered = new MemoryStream();
        var chunk = new byte[8192];
        try
        {
            while (true)
            {
                var remaining = maxBytes - buffered.Length;
                var wanted = remaining < chunk.Length ? (int)remaining + 1 : chunk.Length;
                var read = await body.ReadAsync(chunk.AsMemory(0, wanted));
                if (read == 0) break;
                if (read > remaining) return null;
                buffered.Write(chunk, 0, read);
            }
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return null;
        }

        buffered.Position = 0;
        return buffered;
    }

    private static async Task<BodyResult<string>> ReadTextAsync(HttpRequestData req, long maxBytes, bool allowEmpty = false)
    {
        MemoryStream? buffered;
        try
        {
            buffered = await ReadWithinAsync(req.Body, maxBytes);
        }
        catch (BadHttpRequestException)
        {
            // The client stopped sending mid-body (tab closed, navigation, network drop) and Kestrel
            // reports "Unexpected end of request content". The server is fine, so this is a 4xx.
            return BodyResult<string>.Fail(await req.BadRequestAsync(IncompleteMessage));
        }

        if (buffered == null)
            return BodyResult<string>.Fail(await req.BadRequestAsync(TooLargeMessage));

        // Decoded as HttpRequestData.ReadAsStringAsync decodes: UTF-8 unless a byte-order mark says otherwise.
        string text;
        using (var reader = new StreamReader(buffered, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
            text = await reader.ReadToEndAsync();

        if (!string.IsNullOrWhiteSpace(text)) return BodyResult<string>.Ok(text);
        return allowEmpty
            ? BodyResult<string>.Empty()
            : BodyResult<string>.Fail(await req.BadRequestAsync(RequiredMessage));
    }
}
