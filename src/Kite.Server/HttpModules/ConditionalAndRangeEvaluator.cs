using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Kite.Server.HttpModules;

public enum PreconditionOutcome
{
    Proceed = 0,
    NotModified304 = 304,
    PreconditionFailed412 = 412
}

public readonly record struct ByteRangeSpec(long Start, long End)
{
    public long Length => End - Start + 1;
}

public enum RangeParseOutcome
{
    NoRangeHeader,
    Satisfiable,
    NotSatisfiable416,
    IgnoredByIfRangeMismatch
}

/// <summary>
/// Implements normative RFC 9110 Section 13 (Conditional Requests) and Section 14 (Range Requests)
/// with zero-allocation span parsing where possible.
/// </summary>
public static class ConditionalAndRangeEvaluator
{
    public static string GenerateStrongETag(DateTimeOffset lastModifiedUtc, long length)
    {
        long unixSeconds = lastModifiedUtc.ToUnixTimeSeconds();
        return $"\"{unixSeconds:x}-{length:x}\"";
    }

    /// <summary>
    /// Evaluates conditional headers according to exact RFC 9110 Section 13.2.2 precedence:
    /// 1. If-Match
    /// 2. If-Unmodified-Since (only if If-Match is not present)
    /// 3. If-None-Match
    /// 4. If-Modified-Since (only if If-None-Match is not present and method is GET/HEAD)
    /// </summary>
    public static PreconditionOutcome EvaluatePreconditions(
        IHeaderDictionary requestHeaders,
        string httpMethod,
        string currentETag,
        DateTimeOffset lastModifiedUtc)
    {
        // Truncate sub-second precision for HTTP-date comparisons (RFC 9110 Section 5.6.7)
        DateTimeOffset resourceDate = DateTimeOffset.FromUnixTimeSeconds(lastModifiedUtc.ToUnixTimeSeconds());

        // Step 1: If-Match (RFC 9110 Section 13.1.1 — uses strong comparison)
        if (requestHeaders.TryGetValue("If-Match", out StringValues ifMatchValues) &&
            !StringValues.IsNullOrEmpty(ifMatchValues))
        {
            if (!MatchesIfMatch(ifMatchValues.ToString().AsSpan(), currentETag.AsSpan()))
            {
                return PreconditionOutcome.PreconditionFailed412;
            }
        }
        // Step 2: If-Unmodified-Since (RFC 9110 Section 13.1.4 — only when If-Match is absent)
        else if (requestHeaders.TryGetValue("If-Unmodified-Since", out StringValues iusValues) &&
                 TryParseHttpDate(iusValues.ToString(), out DateTimeOffset iusDate))
        {
            if (resourceDate > iusDate)
            {
                return PreconditionOutcome.PreconditionFailed412;
            }
        }

        bool isGetOrHead = HttpMethods.IsGet(httpMethod) || HttpMethods.IsHead(httpMethod);

        // Step 3: If-None-Match (RFC 9110 Section 13.1.2 — uses weak comparison)
        if (requestHeaders.TryGetValue("If-None-Match", out StringValues inmValues) &&
            !StringValues.IsNullOrEmpty(inmValues))
        {
            if (MatchesIfNoneMatch(inmValues.ToString().AsSpan(), currentETag.AsSpan()))
            {
                return isGetOrHead ? PreconditionOutcome.NotModified304 : PreconditionOutcome.PreconditionFailed412;
            }

            // Per RFC 9110 Section 13.1.3: When If-None-Match is present, If-Modified-Since MUST be ignored!
            return PreconditionOutcome.Proceed;
        }

        // Step 4: If-Modified-Since (RFC 9110 Section 13.1.3 — only for GET/HEAD when If-None-Match is absent)
        if (isGetOrHead &&
            requestHeaders.TryGetValue("If-Modified-Since", out StringValues imsValues) &&
            TryParseHttpDate(imsValues.ToString(), out DateTimeOffset imsDate))
        {
            if (resourceDate <= imsDate)
            {
                return PreconditionOutcome.NotModified304;
            }
        }

        return PreconditionOutcome.Proceed;
    }

    /// <summary>
    /// Evaluates <c>Range</c> and <c>If-Range</c> headers per RFC 9110 Section 14.
    /// </summary>
    public static RangeParseOutcome EvaluateRange(
        IHeaderDictionary requestHeaders,
        string httpMethod,
        long totalLength,
        string currentETag,
        DateTimeOffset lastModifiedUtc,
        out ByteRangeSpec range)
    {
        range = default;

        if (!HttpMethods.IsGet(httpMethod))
        {
            return RangeParseOutcome.NoRangeHeader;
        }

        if (!requestHeaders.TryGetValue("Range", out StringValues rangeHeaderVal) ||
            StringValues.IsNullOrEmpty(rangeHeaderVal))
        {
            return RangeParseOutcome.NoRangeHeader;
        }

        // Check If-Range (RFC 9110 Section 13.1.5)
        if (requestHeaders.TryGetValue("If-Range", out StringValues ifRangeVal) &&
            !StringValues.IsNullOrEmpty(ifRangeVal))
        {
            ReadOnlySpan<char> ifRangeSpan = ifRangeVal.ToString().AsSpan().Trim();
            if (ifRangeSpan.StartsWith('"') || ifRangeSpan.StartsWith("W/\"", StringComparison.Ordinal))
            {
                if (!StrongETagEquals(ifRangeSpan, currentETag.AsSpan()))
                {
                    return RangeParseOutcome.IgnoredByIfRangeMismatch;
                }
            }
            else if (TryParseHttpDate(ifRangeSpan.ToString(), out DateTimeOffset ifRangeDate))
            {
                DateTimeOffset resourceDate = DateTimeOffset.FromUnixTimeSeconds(lastModifiedUtc.ToUnixTimeSeconds());
                if (resourceDate != ifRangeDate)
                {
                    return RangeParseOutcome.IgnoredByIfRangeMismatch;
                }
            }
            else
            {
                return RangeParseOutcome.IgnoredByIfRangeMismatch;
            }
        }

        ReadOnlySpan<char> rawRange = rangeHeaderVal.ToString().AsSpan().Trim();
        if (!rawRange.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
        {
            return RangeParseOutcome.NoRangeHeader;
        }

        if (totalLength <= 0)
        {
            return RangeParseOutcome.NotSatisfiable416;
        }

        ReadOnlySpan<char> spec = rawRange["bytes=".Length..].Trim();
        // If multi-range, take first range spec or validate first segment
        int commaIndex = spec.IndexOf(',');
        if (commaIndex >= 0)
        {
            spec = spec[..commaIndex].Trim();
        }

        int dashIndex = spec.IndexOf('-');
        if (dashIndex < 0)
        {
            return RangeParseOutcome.NotSatisfiable416;
        }

        ReadOnlySpan<char> startSpan = spec[..dashIndex].Trim();
        ReadOnlySpan<char> endSpan = spec[(dashIndex + 1)..].Trim();

        if (startSpan.IsEmpty)
        {
            // Suffix byte-range-spec: bytes=-500 (last 500 bytes)
            if (!long.TryParse(endSpan, NumberStyles.None, CultureInfo.InvariantCulture, out long suffixLength) ||
                suffixLength <= 0)
            {
                return RangeParseOutcome.NotSatisfiable416;
            }

            long start = Math.Max(0, totalLength - suffixLength);
            range = new ByteRangeSpec(start, totalLength - 1);
            return RangeParseOutcome.Satisfiable;
        }
        else
        {
            if (!long.TryParse(startSpan, NumberStyles.None, CultureInfo.InvariantCulture, out long start) ||
                start < 0 || start >= totalLength)
            {
                return RangeParseOutcome.NotSatisfiable416;
            }

            long end = totalLength - 1;
            if (!endSpan.IsEmpty)
            {
                if (!long.TryParse(endSpan, NumberStyles.None, CultureInfo.InvariantCulture, out long parsedEnd) ||
                    parsedEnd < start)
                {
                    return RangeParseOutcome.NotSatisfiable416;
                }
                end = Math.Min(parsedEnd, totalLength - 1);
            }

            range = new ByteRangeSpec(start, end);
            return RangeParseOutcome.Satisfiable;
        }
    }

    public static bool StrongETagEquals(ReadOnlySpan<char> tag1, ReadOnlySpan<char> tag2)
    {
        tag1 = tag1.Trim();
        tag2 = tag2.Trim();
        if (tag1.StartsWith("W/", StringComparison.OrdinalIgnoreCase) ||
            tag2.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return tag1.Length >= 2 && tag1.SequenceEqual(tag2);
    }

    public static bool WeakETagEquals(ReadOnlySpan<char> tag1, ReadOnlySpan<char> tag2)
    {
        tag1 = StripWeakPrefix(tag1.Trim());
        tag2 = StripWeakPrefix(tag2.Trim());
        return tag1.Length >= 2 && tag1.SequenceEqual(tag2);
    }

    private static ReadOnlySpan<char> StripWeakPrefix(ReadOnlySpan<char> tag) =>
        tag.StartsWith("W/", StringComparison.OrdinalIgnoreCase) ? tag[2..].Trim() : tag;

    private static bool MatchesIfMatch(ReadOnlySpan<char> headerValue, ReadOnlySpan<char> currentETag)
    {
        headerValue = headerValue.Trim();
        if (headerValue.SequenceEqual("*"))
        {
            return !currentETag.IsEmpty;
        }

        foreach (Range segment in headerValue.Split(','))
        {
            ReadOnlySpan<char> candidate = headerValue[segment].Trim();
            if (StrongETagEquals(candidate, currentETag))
            {
                return true;
            }
        }
        return false;
    }

    private static bool MatchesIfNoneMatch(ReadOnlySpan<char> headerValue, ReadOnlySpan<char> currentETag)
    {
        headerValue = headerValue.Trim();
        if (headerValue.SequenceEqual("*"))
        {
            return !currentETag.IsEmpty;
        }

        foreach (Range segment in headerValue.Split(','))
        {
            ReadOnlySpan<char> candidate = headerValue[segment].Trim();
            if (WeakETagEquals(candidate, currentETag))
            {
                return true;
            }
        }
        return false;
    }

    public static bool TryParseHttpDate(string? rawDate, out DateTimeOffset parsed)
    {
        if (string.IsNullOrWhiteSpace(rawDate))
        {
            parsed = default;
            return false;
        }

        return DateTimeOffset.TryParseExact(
            rawDate.Trim(),
            ["r", "R", "ddd, dd MMM yyyy HH:mm:ss 'GMT'", "dddd, dd-MMM-yy HH:mm:ss 'GMT'", "ddd MMM  d HH:mm:ss yyyy", "ddd MMM dd HH:mm:ss yyyy"],
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out parsed);
    }
}
