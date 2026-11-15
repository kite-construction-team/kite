using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Kite.Core.Plugins;
using Microsoft.AspNetCore.Http;

namespace Kite.TechEmpower.Plugin;

/// <summary>
/// TechEmpower FrameworkBenchmarks (plaintext, json, db, query, update, fortunes, cached-query)
/// edge plugin hosted directly inside Kite's HTTP pipeline.
/// </summary>
public sealed class TechEmpowerEdgeMiddleware : IKiteEdgeMiddleware
{
    private static ReadOnlySpan<byte> PlaintextPayload => "Hello, World!"u8;
    private static readonly Microsoft.Extensions.Primitives.StringValues PlaintextContentType = new("text/plain");
    private static readonly Microsoft.Extensions.Primitives.StringValues JsonContentType = new("application/json");
    private static readonly Microsoft.Extensions.Primitives.StringValues HtmlContentType = new("text/html; charset=utf-8");

    private static readonly HtmlEncoder FortuneHtmlEncoder = CreateFortuneHtmlEncoder();

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? t_jsonBuffer;

    [ThreadStatic]
    private static Utf8JsonWriter? t_jsonWriter;

    [ThreadStatic]
    private static StringBuilder? t_htmlBuilder;

    [ThreadStatic]
    private static ArrayBufferWriter<byte>? t_htmlBytes;

    private TechEmpowerDb? _db;
    private readonly Lock _dbInitLock = new();

    public ValueTask<bool> InvokeAsync(
        HttpContext context,
        IReadOnlyDictionary<string, string> settings,
        CancellationToken cancellationToken)
    {
        PathString path = context.Request.Path;

        if (path.Equals("/plaintext", StringComparison.Ordinal))
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.Headers.ContentType = PlaintextContentType;
            context.Response.ContentLength = PlaintextPayload.Length;

            Span<byte> target = context.Response.BodyWriter.GetSpan(PlaintextPayload.Length);
            PlaintextPayload.CopyTo(target);
            context.Response.BodyWriter.Advance(PlaintextPayload.Length);
            return ValueTask.FromResult(false);
        }

        if (path.Equals("/json", StringComparison.Ordinal))
        {
            WriteJsonSync(context, new JsonMessage("Hello, World!"), TechEmpowerJsonContext.Default.JsonMessage);
            return ValueTask.FromResult(false);
        }

        if (path.Equals("/db", StringComparison.Ordinal))
        {
            return HandleDbAsync(context, GetOrCreateDb(settings), cancellationToken);
        }

        if (path.StartsWithSegments("/queries", StringComparison.Ordinal, out PathString queriesRemaining))
        {
            int count = ParseClampedCount(context, queriesRemaining);
            return HandleMultipleQueriesAsync(context, GetOrCreateDb(settings), count, cancellationToken);
        }

        if (path.StartsWithSegments("/updates", StringComparison.Ordinal, out PathString updatesRemaining))
        {
            int count = ParseClampedCount(context, updatesRemaining);
            return HandleUpdatesAsync(context, GetOrCreateDb(settings), count, cancellationToken);
        }

        if (path.Equals("/fortunes", StringComparison.Ordinal))
        {
            return HandleFortunesAsync(context, GetOrCreateDb(settings), cancellationToken);
        }

        if (path.StartsWithSegments("/cached-worlds", StringComparison.Ordinal, out PathString cachedRemaining))
        {
            int count = ParseClampedCount(context, cachedRemaining);
            TechEmpowerDb db = GetOrCreateDb(settings);
            ValueTask<WorldRow[]> task = db.LoadCachedWorldsAsync(count, cancellationToken);
            if (task.IsCompletedSuccessfully)
            {
                WriteJsonSync(context, task.Result, TechEmpowerJsonContext.Default.WorldRowArray);
                return ValueTask.FromResult(false);
            }
            return FinishCachedWorldsAsync(context, task, cancellationToken);
        }

        return ValueTask.FromResult(true);
    }

    private TechEmpowerDb GetOrCreateDb(IReadOnlyDictionary<string, string> settings)
    {
        TechEmpowerDb? existing = Volatile.Read(ref _db);
        if (existing is not null)
        {
            return existing;
        }

        lock (_dbInitLock)
        {
            if (_db is not null)
            {
                return _db;
            }

            string connStr =
                (settings.TryGetValue("connectionString", out string? cfgConn) && !string.IsNullOrWhiteSpace(cfgConn)
                    ? cfgConn
                    : null)
                ?? Environment.GetEnvironmentVariable("KITE_TFB_CONNECTION_STRING")
                ?? TechEmpowerDb.DefaultConnectionString;

            _db = new TechEmpowerDb(connStr);
            return _db;
        }
    }

    private static async ValueTask<bool> HandleDbAsync(HttpContext context, TechEmpowerDb db, CancellationToken ct)
    {
        WorldRow row = await db.LoadSingleWorldAsync(ct).ConfigureAwait(false);
        await WriteJsonAsync(context, row, TechEmpowerJsonContext.Default.WorldRow, ct).ConfigureAwait(false);
        return false;
    }

    private static async ValueTask<bool> HandleMultipleQueriesAsync(HttpContext context, TechEmpowerDb db, int count, CancellationToken ct)
    {
        WorldRow[] rows = await db.LoadMultipleWorldsAsync(count, ct).ConfigureAwait(false);
        await WriteJsonAsync(context, rows, TechEmpowerJsonContext.Default.WorldRowArray, ct).ConfigureAwait(false);
        return false;
    }

    private static async ValueTask<bool> HandleUpdatesAsync(HttpContext context, TechEmpowerDb db, int count, CancellationToken ct)
    {
        WorldRow[] rows = await db.UpdateMultipleWorldsAsync(count, ct).ConfigureAwait(false);
        await WriteJsonAsync(context, rows, TechEmpowerJsonContext.Default.WorldRowArray, ct).ConfigureAwait(false);
        return false;
    }

    private static async ValueTask<bool> FinishCachedWorldsAsync(HttpContext context, ValueTask<WorldRow[]> task, CancellationToken ct)
    {
        WorldRow[] rows = await task.ConfigureAwait(false);
        await WriteJsonAsync(context, rows, TechEmpowerJsonContext.Default.WorldRowArray, ct).ConfigureAwait(false);
        return false;
    }

    private static async ValueTask<bool> HandleFortunesAsync(HttpContext context, TechEmpowerDb db, CancellationToken ct)
    {
        List<FortuneRow> fortunes = await db.LoadFortunesAsync(ct).ConfigureAwait(false);

        StringBuilder sb = t_htmlBuilder ??= new StringBuilder(2048);
        sb.Clear();
        sb.Append("<!DOCTYPE html><html><head><title>Fortunes</title></head><body><table><tr><th>id</th><th>message</th></tr>");
        for (int i = 0; i < fortunes.Count; i++)
        {
            FortuneRow f = fortunes[i];
            sb.Append("<tr><td>");
            sb.Append(f.Id.ToString(CultureInfo.InvariantCulture));
            sb.Append("</td><td>");
            sb.Append(FortuneHtmlEncoder.Encode(f.Message));
            sb.Append("</td></tr>");
        }
        sb.Append("</table></body></html>");

        ArrayBufferWriter<byte> htmlBytes = t_htmlBytes ??= new ArrayBufferWriter<byte>(4096);
        htmlBytes.Clear();
        foreach (ReadOnlyMemory<char> chunk in sb.GetChunks())
        {
            Span<byte> dest = htmlBytes.GetSpan(Encoding.UTF8.GetMaxByteCount(chunk.Length));
            int written = Encoding.UTF8.GetBytes(chunk.Span, dest);
            htmlBytes.Advance(written);
        }

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers.ContentType = HtmlContentType;
        context.Response.ContentLength = htmlBytes.WrittenCount;
        await context.Response.BodyWriter.WriteAsync(htmlBytes.WrittenMemory, ct).ConfigureAwait(false);
        return false;
    }

    private static int ParseClampedCount(HttpContext context, PathString remaining)
    {
        string? raw = remaining.HasValue ? remaining.Value!.Trim('/') : null;
        if (string.IsNullOrEmpty(raw) && context.Request.Query.TryGetValue("queries", out var qVal))
        {
            raw = qVal.ToString();
        }

        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            return 1;
        }

        return Math.Clamp(parsed, 1, 500);
    }

    private static void WriteJsonSync<T>(HttpContext context, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        ArrayBufferWriter<byte> buffer = SerializeToThreadBuffer(value, typeInfo);
        ReadOnlySpan<byte> payload = buffer.WrittenSpan;

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers.ContentType = JsonContentType;
        context.Response.ContentLength = payload.Length;

        Span<byte> target = context.Response.BodyWriter.GetSpan(payload.Length);
        payload.CopyTo(target);
        context.Response.BodyWriter.Advance(payload.Length);
    }

    private static async ValueTask WriteJsonAsync<T>(
        HttpContext context,
        T value,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken ct)
    {
        ArrayBufferWriter<byte> buffer = SerializeToThreadBuffer(value, typeInfo);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.Headers.ContentType = JsonContentType;
        context.Response.ContentLength = buffer.WrittenCount;
        await context.Response.BodyWriter.WriteAsync(buffer.WrittenMemory, ct).ConfigureAwait(false);
    }

    private static ArrayBufferWriter<byte> SerializeToThreadBuffer<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        ArrayBufferWriter<byte> buffer = t_jsonBuffer ??= new ArrayBufferWriter<byte>(256);
        buffer.Clear();
        Utf8JsonWriter writer = t_jsonWriter ??= new Utf8JsonWriter(buffer);
        writer.Reset(buffer);
        JsonSerializer.Serialize(writer, value, typeInfo);
        writer.Flush();
        return buffer;
    }

    private static HtmlEncoder CreateFortuneHtmlEncoder()
    {
        var settings = new TextEncoderSettings(UnicodeRanges.BasicLatin, UnicodeRanges.Katakana, UnicodeRanges.Hiragana);
        settings.AllowCharacter('\u2014');
        return HtmlEncoder.Create(settings);
    }
}

public readonly record struct JsonMessage(string Message);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(JsonMessage))]
[JsonSerializable(typeof(WorldRow))]
[JsonSerializable(typeof(WorldRow[]))]
internal sealed partial class TechEmpowerJsonContext : JsonSerializerContext;
