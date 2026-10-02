using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace MW_GC.EventManager.API.Validation;

/// <summary>The outcome of reading a request body: either a value or the response to send instead.</summary>
internal sealed class BodyRead<T> where T : class
{
    private BodyRead(T? value, IActionResult? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }
    public IActionResult? Error { get; }

    [MemberNotNullWhen(true, nameof(Value))]
    [MemberNotNullWhen(false, nameof(Error))]
    public bool Ok => Error is null;

    public static BodyRead<T> Success(T value) => new(value, null);
    public static BodyRead<T> Failure(IActionResult error) => new(null, error);
}

/// <summary>
/// Reads and deserialises a JSON request body once, for every route that takes a body.
/// A Content-Type that is present and not JSON is a 415; an empty, malformed or <c>null</c>
/// body is a 400 with a short message. Callable from unit tests without a Functions host.
/// </summary>
internal static class RequestBody
{
    public const string InvalidJsonMessage = "Request body is empty or malformed.";
    public const string NullBodyMessage = "Request body must not be null.";
    public const string UnsupportedMediaTypeMessage = "Content-Type must be application/json.";

    // The same settings HttpRequest.ReadFromJsonAsync used before: camelCase, case-insensitive names.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<BodyRead<T>> ReadAsync<T>(HttpRequest request, CancellationToken ct) where T : class
    {
        MediaTypeHeaderValue? contentType = null;
        if (!string.IsNullOrWhiteSpace(request.ContentType)
            && (!MediaTypeHeaderValue.TryParse(request.ContentType, out contentType) || !IsJson(contentType)))
            return BodyRead<T>.Failure(new ObjectResult(UnsupportedMediaTypeMessage) { StatusCode = StatusCodes.Status415UnsupportedMediaType });

        // Honour a non-UTF-8 charset the way ReadFromJsonAsync did; JSON itself is read as UTF-8.
        var encoding = contentType?.Encoding;
        var body = encoding is null || encoding.CodePage == Encoding.UTF8.CodePage
            ? request.Body
            : Encoding.CreateTranscodingStream(request.Body, encoding, Encoding.UTF8, leaveOpen: true);

        T? value;
        try
        {
            value = await JsonSerializer.DeserializeAsync<T>(body, Json, ct);
        }
        // A FormatException or ArgumentException here comes from an entity setter (a bad or null
        // "rowKey" fails Guid.Parse); System.Text.Json does not wrap those in a JsonException.
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return BodyRead<T>.Failure(new BadRequestObjectResult(InvalidJsonMessage));
        }

        return value is null
            ? BodyRead<T>.Failure(new BadRequestObjectResult(NullBodyMessage))
            : BodyRead<T>.Success(value);
    }

    // application/json or any +json type (application/problem+json and the like), any case.
    private static bool IsJson(MediaTypeHeaderValue contentType)
    {
        var mediaType = contentType.MediaType.Value ?? string.Empty;
        return mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase);
    }
}
