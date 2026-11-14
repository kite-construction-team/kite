using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Kite.Core.Models;

namespace Kite.Server.Observability;

/// <summary>
/// Dependency-free OTLP/HTTP JSON (<c>/v1/traces</c>) exporter subscribing via <see cref="ActivityListener"/>
/// to <c>ActivitySource("Kite.Server")</c> spans and flushing batches asynchronously.
/// </summary>
public sealed class OtlpHttpJsonExporter : IAsyncDisposable
{
    private readonly ConcurrentQueue<Activity> _pendingSpans = new();
    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly CancellationTokenSource _cts = new();

    private OtlpExportConfig _config = new();
    private string _activitySourceName = "Kite.Server";
    private ActivityListener? _listener;
    private Task? _flushLoopTask;

    public OtlpHttpJsonExporter(HttpClient? httpClient = null)
    {
        _ownsHttpClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public long ExportedSpanCount { get; private set; }
    public long ExportFailureCount { get; private set; }

    public void Reconcile(ObservabilityConfig obs)
    {
        ArgumentNullException.ThrowIfNull(obs);
        _config = obs.Otlp;
        _activitySourceName = string.IsNullOrWhiteSpace(obs.ActivitySourceName) ? "Kite.Server" : obs.ActivitySourceName;

        bool active = obs.EnableOpenTelemetryTracing && obs.Otlp.Enabled && !string.IsNullOrWhiteSpace(obs.Otlp.EndpointUrl);
        if (active && _listener is null)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = source => string.Equals(source.Name, _activitySourceName, StringComparison.Ordinal),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if (_pendingSpans.Count < 4096)
                    {
                        _pendingSpans.Enqueue(activity);
                    }
                }
            };
            ActivitySource.AddActivityListener(_listener);
            _flushLoopTask = Task.Run(() => FlushLoopAsync(_cts.Token));
        }
        else if (!active && _listener is not null)
        {
            _listener.Dispose();
            _listener = null;
        }
    }

    public async ValueTask FlushNowAsync(CancellationToken ct = default)
    {
        if (_pendingSpans.IsEmpty || !_config.Enabled || string.IsNullOrWhiteSpace(_config.EndpointUrl))
        {
            return;
        }

        int maxBatch = Math.Clamp(_config.MaxBatchSize, 1, 1024);
        var batch = new List<Activity>(Math.Min(maxBatch, _pendingSpans.Count));
        while (batch.Count < maxBatch && _pendingSpans.TryDequeue(out Activity? act))
        {
            batch.Add(act);
        }

        if (batch.Count == 0)
        {
            return;
        }

        byte[] payload = SerializeOtlpTracesJson(_config.ServiceName, _activitySourceName, batch);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _config.EndpointUrl)
            {
                Content = new ByteArrayContent(payload)
            };
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            foreach (KeyValuePair<string, string> h in _config.Headers)
            {
                req.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }

            using HttpResponseMessage resp = await _httpClient.SendAsync(req, ct).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode)
            {
                ExportedSpanCount += batch.Count;
            }
            else
            {
                ExportFailureCount++;
            }
        }
        catch
        {
            ExportFailureCount++;
        }
    }

    public static byte[] SerializeOtlpTracesJson(
        string serviceName,
        string scopeName,
        IReadOnlyList<Activity> spans)
    {
        var buffer = new ArrayBufferWriter<byte>(1024);
        using var writer = new Utf8JsonWriter(buffer);

        writer.WriteStartObject();
        writer.WriteStartArray("resourceSpans");
        writer.WriteStartObject();

        writer.WriteStartObject("resource");
        writer.WriteStartArray("attributes");
        WriteStringAttribute(writer, "service.name", string.IsNullOrWhiteSpace(serviceName) ? "Kite.Server" : serviceName);
        writer.WriteEndArray();
        writer.WriteEndObject();

        writer.WriteStartArray("scopeSpans");
        writer.WriteStartObject();

        writer.WriteStartObject("scope");
        writer.WriteString("name", scopeName);
        writer.WriteEndObject();

        writer.WriteStartArray("spans");
        for (int i = 0; i < spans.Count; i++)
        {
            Activity span = spans[i];
            DateTime utcStart = span.StartTimeUtc.Kind == DateTimeKind.Utc
                ? span.StartTimeUtc
                : DateTime.SpecifyKind(span.StartTimeUtc, DateTimeKind.Utc);
            long startNs = new DateTimeOffset(utcStart).ToUnixTimeMilliseconds() * 1_000_000L;
            long durNs = Math.Max(1000L, (long)(span.Duration.TotalMilliseconds * 1_000_000.0));

            writer.WriteStartObject();
            writer.WriteString("traceId", span.TraceId.ToHexString());
            writer.WriteString("spanId", span.SpanId.ToHexString());
            if (span.ParentSpanId != default)
            {
                writer.WriteString("parentSpanId", span.ParentSpanId.ToHexString());
            }
            writer.WriteString("name", span.OperationName);
            writer.WriteNumber("kind", (int)span.Kind + 1);
            writer.WriteString("startTimeUnixNano", startNs.ToString());
            writer.WriteString("endTimeUnixNano", (startNs + durNs).ToString());

            writer.WriteStartArray("attributes");
            foreach (KeyValuePair<string, object?> tag in span.TagObjects)
            {
                if (tag.Value is not null)
                {
                    WriteStringAttribute(writer, tag.Key, tag.Value.ToString() ?? "");
                }
            }
            writer.WriteEndArray();

            writer.WriteStartObject("status");
            writer.WriteNumber("code", span.Status switch
            {
                ActivityStatusCode.Ok => 1,
                ActivityStatusCode.Error => 2,
                _ => 0
            });
            writer.WriteEndObject();

            writer.WriteEndObject();
        }
        writer.WriteEndArray();

        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteStringAttribute(Utf8JsonWriter writer, string key, string value)
    {
        writer.WriteStartObject();
        writer.WriteString("key", key);
        writer.WriteStartObject("value");
        writer.WriteString("stringValue", value);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private async Task FlushLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            int intervalMs = Math.Clamp(_config.ExportIntervalMilliseconds, 250, 60_000);
            try
            {
                await Task.Delay(intervalMs, ct).ConfigureAwait(false);
                await FlushNowAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_flushLoopTask is not null)
        {
            try
            {
                await _flushLoopTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }
        await FlushNowAsync(CancellationToken.None).ConfigureAwait(false);
        _listener?.Dispose();
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
        _cts.Dispose();
    }
}
