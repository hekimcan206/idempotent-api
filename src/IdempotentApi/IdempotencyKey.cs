using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace IdempotentApi;

/// <summary>
/// Parses the <c>Idempotency-Key</c> field value. The IETF draft defines it as an Item Structured Header whose value
/// MUST be a String (RFC 9651 / RFC 8941 sf-string): a double-quoted run of printable ASCII where only <c>"</c> and
/// <c>\</c> may be escaped.
/// </summary>
/// <remarks>
/// The parser is deliberately strict because the key becomes part of a storage lookup: anything it does not fully
/// understand is rejected with 400 instead of being normalised into a key that might collide with another request.
/// Parameters on the item (<c>"key";foo=1</c>) are rejected for the same reason: the draft does not define any.
/// </remarks>
public static class IdempotencyKey
{
    /// <summary>Why a field value was rejected.</summary>
    public enum ParseError
    {
        None,
        Empty,
        NotAString,
        InvalidCharacter,
        InvalidEscape,
        Unterminated,
        TrailingCharacters,
        TooShort,
        TooLong,
    }

    /// <summary>
    /// Parses a raw field value.
    /// </summary>
    /// <param name="fieldValue">The raw header value as received.</param>
    /// <param name="allowBareToken">
    /// Accept an unquoted value (<c>Idempotency-Key: 8e03...</c>) as many existing clients send it. Off by default:
    /// the draft requires an sf-string.
    /// </param>
    /// <param name="minLength">Minimum length of the decoded key.</param>
    /// <param name="maxLength">Maximum length of the decoded key.</param>
    /// <param name="key">The decoded key.</param>
    /// <param name="error">Why the value was rejected.</param>
    public static bool TryParse(
        string? fieldValue,
        bool allowBareToken,
        int minLength,
        int maxLength,
        [NotNullWhen(true)] out string? key,
        out ParseError error)
    {
        key = null;
        var span = (fieldValue ?? string.Empty).AsSpan().Trim(' ');
        if (span.IsEmpty)
        {
            error = ParseError.Empty;
            return false;
        }

        string decoded;
        if (span[0] == '"')
        {
            if (!TryParseSfString(span, out decoded, out error))
            {
                return false;
            }
        }
        else if (allowBareToken)
        {
            foreach (var c in span)
            {
                // A bare key is restricted to visible ASCII without quotes, commas, semicolons or backslashes so it
                // cannot be confused with structured-field syntax.
                if (c is <= ' ' or >= (char)0x7F or '"' or ',' or ';' or '\\')
                {
                    error = ParseError.InvalidCharacter;
                    return false;
                }
            }

            decoded = span.ToString();
        }
        else
        {
            error = ParseError.NotAString;
            return false;
        }

        if (decoded.Length < minLength)
        {
            error = ParseError.TooShort;
            return false;
        }

        if (decoded.Length > maxLength)
        {
            error = ParseError.TooLong;
            return false;
        }

        key = decoded;
        error = ParseError.None;
        return true;
    }

    /// <summary>Serialises a key as an sf-string, the inverse of <see cref="TryParse"/>.</summary>
    public static string Format(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var sb = new StringBuilder(key.Length + 2);
        sb.Append('"');
        foreach (var c in key)
        {
            if (c is < ' ' or > '~')
            {
                throw new ArgumentException("An sf-string can only hold printable ASCII (0x20-0x7E).", nameof(key));
            }

            if (c is '"' or '\\')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.Append('"').ToString();
    }

    private static bool TryParseSfString(ReadOnlySpan<char> span, out string decoded, out ParseError error)
    {
        decoded = string.Empty;
        var sb = new StringBuilder(span.Length);
        var i = 1;
        while (true)
        {
            if (i >= span.Length)
            {
                error = ParseError.Unterminated;
                return false;
            }

            var c = span[i++];
            if (c == '\\')
            {
                if (i >= span.Length)
                {
                    error = ParseError.Unterminated;
                    return false;
                }

                var next = span[i++];
                if (next is not ('"' or '\\'))
                {
                    error = ParseError.InvalidEscape;
                    return false;
                }

                sb.Append(next);
            }
            else if (c == '"')
            {
                break;
            }
            else if (c is < ' ' or > '~')
            {
                error = ParseError.InvalidCharacter;
                return false;
            }
            else
            {
                sb.Append(c);
            }
        }

        if (i != span.Length)
        {
            // Parameters (";a=1") or a second list member (", ...") are not part of the draft's grammar for this field.
            error = ParseError.TrailingCharacters;
            return false;
        }

        decoded = sb.ToString();
        error = ParseError.None;
        return true;
    }
}
