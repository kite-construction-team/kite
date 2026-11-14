using System.Globalization;
using System.Text;
using Kite.Core.Memory;

namespace Kite.Server.HttpModules;

/// <summary>
/// Formats W3C Extended Log File Format lines with zero heap allocations into a caller-supplied <see cref="Span{T}"/>
/// or via <see cref="KiteObjectPools.StringBuilderPool"/>.
/// Fields: <c>date time c-ip cs-method cs-uri-stem cs-uri-query s-port cs-host sc-status sc-bytes time-taken</c>
/// </summary>
public static class W3CAccessLogFormatter
{
    public const string W3CHeaderBlock =
        "#Software: Kite Web Server 1.0 (.NET 10.0)\n" +
        "#Version: 1.0\n" +
        "#Fields: date time c-ip cs-method cs-uri-stem cs-uri-query s-port cs-host sc-status sc-bytes time-taken\n";

    /// <summary>
    /// Formats a W3C log entry directly into a target <see cref="Span{T}"/> with 0 bytes allocated on the managed heap.
    /// </summary>
    public static bool TryFormatLine(
        Span<char> destination,
        DateTimeOffset timestampUtc,
        ReadOnlySpan<char> clientIp,
        ReadOnlySpan<char> method,
        ReadOnlySpan<char> uriStem,
        ReadOnlySpan<char> uriQuery,
        int serverPort,
        ReadOnlySpan<char> host,
        int statusCode,
        long bytesSent,
        long elapsedMilliseconds,
        out int charsWritten)
    {
        ReadOnlySpan<char> cleanClientIp = clientIp.IsEmpty ? "-" : clientIp;
        ReadOnlySpan<char> cleanStem = uriStem.IsEmpty ? "/" : uriStem;
        ReadOnlySpan<char> cleanQuery = uriQuery.IsEmpty ? "-" : (uriQuery[0] == '?' ? uriQuery[1..] : uriQuery);
        if (cleanQuery.IsEmpty)
        {
            cleanQuery = "-";
        }
        ReadOnlySpan<char> cleanHost = host.IsEmpty ? "-" : host;

        charsWritten = 0;
        if (destination.Length < 32)
        {
            return false;
        }

        // Write yyyy-MM-dd HH:mm:ss directly with zero allocations
        DateTime dt = timestampUtc.UtcDateTime;
        Write4Digits(destination, dt.Year);
        destination[4] = '-';
        Write2Digits(destination[5..], dt.Month);
        destination[7] = '-';
        Write2Digits(destination[8..], dt.Day);
        destination[10] = ' ';
        Write2Digits(destination[11..], dt.Hour);
        destination[13] = ':';
        Write2Digits(destination[14..], dt.Minute);
        destination[16] = ':';
        Write2Digits(destination[17..], dt.Second);
        destination[19] = ' ';

        int pos = 20;
        if (!TryAppendSpanAndSpace(destination, ref pos, cleanClientIp) ||
            !TryAppendSpanAndSpace(destination, ref pos, method) ||
            !TryAppendSpanAndSpace(destination, ref pos, cleanStem) ||
            !TryAppendSpanAndSpace(destination, ref pos, cleanQuery) ||
            !TryAppendIntAndSpace(destination, ref pos, serverPort) ||
            !TryAppendSpanAndSpace(destination, ref pos, cleanHost) ||
            !TryAppendIntAndSpace(destination, ref pos, statusCode) ||
            !TryAppendLongAndSpace(destination, ref pos, bytesSent) ||
            !elapsedMilliseconds.TryFormat(destination[pos..], out int elapsedLen, default, CultureInfo.InvariantCulture))
        {
            return false;
        }

        pos += elapsedLen;
        if (pos >= destination.Length)
        {
            return false;
        }

        destination[pos++] = '\n';
        charsWritten = pos;
        return true;
    }

    private static void Write4Digits(Span<char> dest, int value)
    {
        dest[0] = (char)('0' + (value / 1000) % 10);
        dest[1] = (char)('0' + (value / 100) % 10);
        dest[2] = (char)('0' + (value / 10) % 10);
        dest[3] = (char)('0' + (value % 10));
    }

    private static void Write2Digits(Span<char> dest, int value)
    {
        dest[0] = (char)('0' + (value / 10) % 10);
        dest[1] = (char)('0' + (value % 10));
    }

    private static bool TryAppendSpanAndSpace(Span<char> dest, ref int pos, ReadOnlySpan<char> value)
    {
        if (pos + value.Length + 1 > dest.Length)
        {
            return false;
        }

        value.CopyTo(dest[pos..]);
        pos += value.Length;
        dest[pos++] = ' ';
        return true;
    }

    private static bool TryAppendIntAndSpace(Span<char> dest, ref int pos, int value)
    {
        if (!value.TryFormat(dest[pos..], out int written, default, CultureInfo.InvariantCulture) ||
            pos + written + 1 > dest.Length)
        {
            return false;
        }

        pos += written;
        dest[pos++] = ' ';
        return true;
    }

    private static bool TryAppendLongAndSpace(Span<char> dest, ref int pos, long value)
    {
        if (!value.TryFormat(dest[pos..], out int written, default, CultureInfo.InvariantCulture) ||
            pos + written + 1 > dest.Length)
        {
            return false;
        }

        pos += written;
        dest[pos++] = ' ';
        return true;
    }

    public static string FormatLinePooled(
        DateTimeOffset timestampUtc,
        ReadOnlySpan<char> clientIp,
        ReadOnlySpan<char> method,
        ReadOnlySpan<char> uriStem,
        ReadOnlySpan<char> uriQuery,
        int serverPort,
        ReadOnlySpan<char> host,
        int statusCode,
        long bytesSent,
        long elapsedMilliseconds)
    {
        Span<char> stackBuffer = stackalloc char[1024];
        if (TryFormatLine(
            stackBuffer,
            timestampUtc,
            clientIp,
            method,
            uriStem,
            uriQuery,
            serverPort,
            host,
            statusCode,
            bytesSent,
            elapsedMilliseconds,
            out int written))
        {
            return new string(stackBuffer[..written]);
        }

        StringBuilder sb = KiteObjectPools.StringBuilderPool.Get();
        try
        {
            sb.AppendFormat(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm:ss} ", timestampUtc);
            sb.Append(clientIp.IsEmpty ? "-" : clientIp).Append(' ');
            sb.Append(method).Append(' ');
            sb.Append(uriStem.IsEmpty ? "/" : uriStem).Append(' ');
            ReadOnlySpan<char> q = uriQuery.IsEmpty ? "-" : (uriQuery[0] == '?' ? uriQuery[1..] : uriQuery);
            sb.Append(q.IsEmpty ? "-" : q).Append(' ');
            sb.Append(serverPort).Append(' ');
            sb.Append(host.IsEmpty ? "-" : host).Append(' ');
            sb.Append(statusCode).Append(' ');
            sb.Append(bytesSent).Append(' ');
            sb.Append(elapsedMilliseconds).Append('\n');
            return sb.ToString();
        }
        finally
        {
            KiteObjectPools.StringBuilderPool.Return(sb);
        }
    }
}
