using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace IdempotentApi;

/// <summary>
/// RFC 9457 problem documents. The draft asks for error responses that link to the API's idempotency documentation,
/// which the <c>type</c> URI does.
/// </summary>
internal static class Problems
{
    public const string ContentType = "application/problem+json";

    public static Task WriteAsync(
        HttpContext context,
        Uri typeBase,
        string fragment,
        int status,
        string title,
        string detail,
        Action<Utf8JsonWriter>? extensions = null)
    {
        var response = context.Response;
        response.StatusCode = status;
        response.ContentType = ContentType;
        response.Headers.CacheControl = "no-store";

        var buffer = new System.Buffers.ArrayBufferWriter<byte>(256);
        // Relaxed escaping keeps quotes readable in "detail"; the body is JSON for API clients, never embedded in HTML.
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteString("type", new UriBuilder(typeBase) { Fragment = fragment }.Uri.AbsoluteUri);
            writer.WriteString("title", title);
            writer.WriteNumber("status", status);
            writer.WriteString("detail", detail);
            extensions?.Invoke(writer);
            writer.WriteEndObject();
        }

        response.ContentLength = buffer.WrittenCount;
        return response.Body.WriteAsync(buffer.WrittenMemory, context.RequestAborted).AsTask();
    }
}
