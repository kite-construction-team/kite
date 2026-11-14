using System.Diagnostics;
using Kite.Core.Models;
using Kite.Core.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Primitives;

namespace Kite.Server.HttpModules;

/// <summary>
/// Pooled per-request Failed Request Tracing (FREB) diagnostic recorder tracking pipeline stage timings
/// and capturing fault stack traces and HTTP envelopes on 5xx / matching status codes or latency spikes.
/// </summary>
public sealed class FrebTraceScope : IResettable
{
    private readonly List<FrebStageTiming> _stages = new(8);
    private long _lastStageTimestamp;

    public long RequestStartTimestamp { get; private set; }
    public string CorrelationId { get; private set; } = "";
    public Exception? CapturedException { get; private set; }
    public string? SubStatus { get; set; }

    public void Begin(long startTimestamp)
    {
        RequestStartTimestamp = startTimestamp;
        _lastStageTimestamp = startTimestamp;
        CorrelationId = Guid.NewGuid().ToString("N")[..12];
        CapturedException = null;
        SubStatus = null;
        _stages.Clear();
    }

    public void RecordStage(string stageName, string outcome = "Completed")
    {
        long now = Stopwatch.GetTimestamp();
        double ms = Stopwatch.GetElapsedTime(_lastStageTimestamp, now).TotalMilliseconds;
        _lastStageTimestamp = now;
        _stages.Add(new FrebStageTiming
        {
            StageName = stageName,
            DurationMs = Math.Round(ms, 3),
            Outcome = outcome
        });
    }

    public void CaptureException(Exception ex) => CapturedException = ex;

    public bool ShouldEmitTrace(CompiledSiteFeatures compiled, int statusCode, double totalDurationMs)
    {
        FailedRequestTracingConfig freb = compiled.EffectiveConfig.FailedRequestTracing;
        if (!freb.Enabled)
        {
            return false;
        }

        if (CapturedException is not null || compiled.IsFrebStatusTriggered(statusCode))
        {
            return true;
        }

        return freb.LatencyCutoffMilliseconds > 0 && totalDurationMs >= freb.LatencyCutoffMilliseconds;
    }

    public FrebTraceRecord BuildRecord(
        HttpContext context,
        string siteId,
        string rawHost,
        DateTimeOffset timestampUtc,
        double totalDurationMs,
        CompiledSiteFeatures compiled)
    {
        int statusCode = context.Response.StatusCode;
        string reason = CapturedException is not null
            ? $"Exception ({CapturedException.GetType().Name})"
            : (compiled.IsFrebStatusTriggered(statusCode)
                ? $"StatusCode ({statusCode})"
                : $"Latency ({totalDurationMs:F1}ms > {compiled.EffectiveConfig.FailedRequestTracing.LatencyCutoffMilliseconds}ms)");

        var reqHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, StringValues> kvp in context.Request.Headers)
        {
            // Never log raw secrets in trace dumps
            string val = string.Equals(kvp.Key, "Authorization", StringComparison.OrdinalIgnoreCase)
                ? "[REDACTED]"
                : kvp.Value.ToString();
            reqHeaders[kvp.Key] = val;
        }

        var respHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<string, StringValues> kvp in context.Response.Headers)
        {
            respHeaders[kvp.Key] = kvp.Value.ToString();
        }

        return new FrebTraceRecord
        {
            TraceId = $"FREB-{timestampUtc:yyyyMMddHHmmss}-{CorrelationId}",
            CorrelationId = CorrelationId,
            SiteId = siteId,
            TimestampUtc = timestampUtc,
            Method = context.Request.Method,
            Host = rawHost,
            Path = context.Request.Path.Value ?? "/",
            QueryString = context.Request.QueryString.Value ?? "",
            ClientIp = context.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1",
            UserAgent = context.Request.Headers.UserAgent.ToString(),
            StatusCode = statusCode,
            SubStatus = SubStatus,
            TotalDurationMs = Math.Round(totalDurationMs, 3),
            TriggerReason = reason,
            ExceptionMessage = CapturedException?.Message,
            ExceptionStackTrace = CapturedException?.ToString(),
            RequestHeaders = reqHeaders,
            ResponseHeaders = respHeaders,
            Stages = new List<FrebStageTiming>(_stages)
        };
    }

    public bool TryReset()
    {
        _stages.Clear();
        CapturedException = null;
        SubStatus = null;
        CorrelationId = string.Empty;
        RequestStartTimestamp = 0;
        _lastStageTimestamp = 0;
        return true;
    }
}
