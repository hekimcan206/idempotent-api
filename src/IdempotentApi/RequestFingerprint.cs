using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace IdempotentApi;

/// <summary>
/// Produces the "idempotency fingerprint" of the draft: a digest of the parts of a request that must be identical for
/// a retry to count as the same request. A key reused with a different fingerprint is answered with 422.
/// </summary>
public interface IRequestFingerprinter
{
    string Compute(HttpRequest request, ReadOnlySpan<byte> body);
}

/// <summary>
/// SHA-256 over the method, path, query string, normalised media type and the exact body bytes.
/// </summary>
/// <remarks>
/// The body is compared byte for byte, not as parsed JSON: a client that retries must resend what it sent. Semantic
/// comparison (key order, whitespace) would mean parsing every body and defining equality for every media type; a
/// custom <see cref="IRequestFingerprinter"/> can do that for an API that needs it.
/// </remarks>
public sealed class Sha256RequestFingerprinter : IRequestFingerprinter
{
    public string Compute(HttpRequest request, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendField(hash, request.Method);
        AppendField(hash, request.Path.Value ?? string.Empty);
        AppendField(hash, request.QueryString.Value ?? string.Empty);
        AppendField(hash, NormaliseMediaType(request.ContentType));
        hash.AppendData(body);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static void AppendField(IncrementalHash hash, string value)
    {
        // Length-prefixing each field makes the encoding unambiguous: ("ab","c") and ("a","bc") hash differently.
        var max = Encoding.UTF8.GetMaxByteCount(value.Length) + 4;
        var buffer = ArrayPool<byte>.Shared.Rent(max);
        try
        {
            var written = Encoding.UTF8.GetBytes(value, 0, value.Length, buffer, 4);
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(buffer, written);
            hash.AppendData(buffer, 0, written + 4);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // "application/json; charset=utf-8" and "Application/JSON" describe the same payload; parameters other than the
    // media type itself rarely change meaning, so they are ignored rather than turning a benign retry into a 422.
    private static string NormaliseMediaType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return string.Empty;
        }

        var semicolon = contentType.IndexOf(';', StringComparison.Ordinal);
        var mediaType = semicolon >= 0 ? contentType[..semicolon] : contentType;
        return mediaType.Trim().ToLowerInvariant();
    }
}
