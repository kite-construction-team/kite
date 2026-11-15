using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kite.Core.Ipc;

var builder = WebApplication.CreateSlimBuilder(args);
builder.Logging.ClearProviders();
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

var app = builder.Build();

app.MapGet("/", () => Results.Ok("ok"));

app.MapGet("/plaintext", (HttpContext ctx) =>
{
    ReadOnlySpan<byte> payload = "Hello, World!"u8;
    ctx.Response.StatusCode = StatusCodes.Status200OK;
    ctx.Response.ContentType = "text/plain";
    ctx.Response.ContentLength = payload.Length;
    Span<byte> target = ctx.Response.BodyWriter.GetSpan(payload.Length);
    payload.CopyTo(target);
    ctx.Response.BodyWriter.Advance(payload.Length);
    return Task.CompletedTask;
});

app.MapGet("/json", (HttpContext ctx) =>
{
    ReadOnlySpan<byte> payload = KiteHostedApp.EntryPoint.SerializeHelloJson();
    ctx.Response.StatusCode = StatusCodes.Status200OK;
    ctx.Response.ContentType = "application/json";
    ctx.Response.ContentLength = payload.Length;
    Span<byte> target = ctx.Response.BodyWriter.GetSpan(payload.Length);
    payload.CopyTo(target);
    ctx.Response.BodyWriter.Advance(payload.Length);
    return Task.CompletedTask;
});

await app.RunAsync().ConfigureAwait(false);

namespace KiteHostedApp
{
    public readonly record struct AppJsonMessage(string Message);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(AppJsonMessage))]
    internal sealed partial class TechEmpowerAppJsonContext : JsonSerializerContext;

    public static class EntryPoint
    {
        private static ReadOnlySpan<byte> PlaintextPayload => "Hello, World!"u8;

        [ThreadStatic]
        private static ArrayBufferWriter<byte>? t_jsonBuffer;

        [ThreadStatic]
        private static Utf8JsonWriter? t_jsonWriter;

        internal static ReadOnlySpan<byte> SerializeHelloJson()
        {
            ArrayBufferWriter<byte> buffer = t_jsonBuffer ??= new ArrayBufferWriter<byte>(64);
            buffer.Clear();
            Utf8JsonWriter writer = t_jsonWriter ??= new Utf8JsonWriter(buffer);
            writer.Reset(buffer);
            JsonSerializer.Serialize(
                writer,
                new AppJsonMessage("Hello, World!"),
                TechEmpowerAppJsonContext.Default.AppJsonMessage);
            writer.Flush();
            return buffer.WrittenSpan;
        }

        public static ValueTask HandleHttpAsync(
            KiteIpcRequestHeader req,
            Stream reqBody,
            KiteIpcResponseHeader resp,
            Stream respBody,
            CancellationToken ct)
        {
            if (string.Equals(req.Path, "/plaintext", StringComparison.Ordinal))
            {
                resp.StatusCode = 200;
                resp.Headers.Add(new KeyValuePair<string, string>("Content-Type", "text/plain"));
                resp.Headers.Add(new KeyValuePair<string, string>("Content-Length", "13"));
                respBody.Write(PlaintextPayload);
                return ValueTask.CompletedTask;
            }

            if (string.Equals(req.Path, "/json", StringComparison.Ordinal))
            {
                ReadOnlySpan<byte> payload = SerializeHelloJson();
                resp.StatusCode = 200;
                resp.Headers.Add(new KeyValuePair<string, string>("Content-Type", "application/json"));
                resp.Headers.Add(new KeyValuePair<string, string>("Content-Length", payload.Length.ToString(CultureInfo.InvariantCulture)));
                respBody.Write(payload);
                return ValueTask.CompletedTask;
            }

            resp.StatusCode = 404;
            resp.Headers.Add(new KeyValuePair<string, string>("Content-Type", "text/plain"));
            resp.Headers.Add(new KeyValuePair<string, string>("Content-Length", "9"));
            respBody.Write("Not Found"u8);
            return ValueTask.CompletedTask;
        }

        public static string HandleRequest(string path, string method)
        {
            return $$"""{"message":"Hello, World!","path":"{{path}}","method":"{{method}}"}""";
        }
    }
}
