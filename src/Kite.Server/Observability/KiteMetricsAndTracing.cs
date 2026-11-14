using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net.Http;
using System.Text;
using Kite.Core.Memory;
using Kite.Core.Models;
using Kite.Core.Routing;
using Kite.Server.Pipeline;
using Kite.Server.Pools;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Kite.Server.Observability;

/// <summary>
/// Built-in cloud-native observability: W3C <see cref="ActivitySource"/> distributed tracing,
/// OpenMetrics/Prometheus text exporter (<c>/__kite/metrics</c>), and Kubernetes readiness/liveness
/// probes (<c>/__kite/healthz</c>, <c>/__kite/readyz</c>) with zero third-party dependencies.
/// </summary>
public static class KiteMetricsAndTracing
{
    public static readonly ActivitySource ServerActivitySource = new("Kite.Server", "1.0.0");
    public static readonly Meter ServerMeter = new("Kite.Server", "1.0.0");

    public static Activity? StartRequestActivity(HttpContext context, ObservabilityConfig cfg)
    {
        if (!cfg.EnableOpenTelemetryTracing)
        {
            return null;
        }

        Activity? activity = null;
        if (context.Request.Headers.TryGetValue("traceparent", out StringValues tpValues) &&
            !StringValues.IsNullOrEmpty(tpValues) &&
            ActivityContext.TryParse(tpValues.ToString(), context.Request.Headers["tracestate"].ToString(), out ActivityContext parentContext))
        {
            activity = ServerActivitySource.StartActivity("http.server.request", ActivityKind.Server, parentContext);
        }
        else
        {
            activity = ServerActivitySource.StartActivity("http.server.request", ActivityKind.Server);
        }

        if (activity is not null)
        {
            activity.SetTag("http.request.method", context.Request.Method);
            activity.SetTag("url.path", context.Request.Path.Value ?? "/");
            activity.SetTag("server.address", context.Request.Host.Host);
            if (activity.Id is not null)
            {
                context.Response.Headers["X-Trace-Id"] = activity.TraceId.ToHexString();
            }
        }

        return activity;
    }

    public static void InjectTraceContext(HttpRequestMessage upstreamRequest)
    {
        Activity? current = Activity.Current;
        if (current is null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(current.Id))
        {
            upstreamRequest.Headers.TryAddWithoutValidation("traceparent", current.Id);
        }

        if (!string.IsNullOrEmpty(current.TraceStateString))
        {
            upstreamRequest.Headers.TryAddWithoutValidation("tracestate", current.TraceStateString);
        }
    }

    public static async ValueTask<bool> TryHandleObservabilityEndpointAsync(
        HttpContext context,
        string requestPath,
        ServerConfig server,
        KiteRequestPipeline pipeline,
        HostRouteTable routeTable,
        UpstreamClusterManager? upstreamManager)
    {
        ObservabilityConfig obs = server.Observability;

        if (obs.EnableHealthEndpoints)
        {
            if (string.Equals(requestPath, obs.HealthEndpointPath, StringComparison.Ordinal))
            {
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "application/json; charset=utf-8";
                string payload = $$"""{"status":"healthy","server":"{{EscapeJson(server.ServerName)}}","activeConnections":{{pipeline.ActiveConnections}}}""";
                await context.Response.WriteAsync(payload, context.RequestAborted).ConfigureAwait(false);
                return true;
            }

            if (string.Equals(requestPath, obs.ReadyEndpointPath, StringComparison.Ordinal))
            {
                bool hasRouteTable = routeTable is not null;
                IReadOnlyList<UpstreamClusterStatus> clusters = upstreamManager?.CaptureStatusSnapshot() ?? [];
                bool anyClusterDegraded = clusters.Any(c => c.Destinations.Count > 0 && c.Destinations.All(d => !d.IsHealthy));
                bool isReady = hasRouteTable && !anyClusterDegraded;

                context.Response.StatusCode = isReady ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;
                context.Response.ContentType = "application/json; charset=utf-8";
                string payload = $$"""{"status":"{{(isReady ? "ready" : "not_ready")}}","clusters":{{clusters.Count}},"bindingConflicts":{{routeTable?.BindingConflicts.Count ?? 0}}}""";
                await context.Response.WriteAsync(payload, context.RequestAborted).ConfigureAwait(false);
                return true;
            }
        }

        if (obs.EnablePrometheusMetrics &&
            string.Equals(requestPath, obs.MetricsPath, StringComparison.Ordinal))
        {
            string metricsText = FormatPrometheusMetrics(pipeline, routeTable, upstreamManager);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/plain; version=0.0.4; charset=utf-8";
            await context.Response.WriteAsync(metricsText, context.RequestAborted).ConfigureAwait(false);
            return true;
        }

        return false;
    }

    public static string FormatPrometheusMetrics(
        KiteRequestPipeline pipeline,
        HostRouteTable? routeTable,
        UpstreamClusterManager? upstreamManager)
    {
        StringBuilder sb = KiteObjectPools.StringBuilderPool.Get();
        try
        {
            sb.AppendLine("# HELP kite_requests_total Total HTTP requests processed.");
            sb.AppendLine("# TYPE kite_requests_total counter");
            sb.Append("kite_requests_total ").Append(pipeline.TotalRequestsProcessed.ToString(CultureInfo.InvariantCulture)).Append('\n');

            sb.AppendLine("# HELP kite_active_connections Current active HTTP connections.");
            sb.AppendLine("# TYPE kite_active_connections gauge");
            sb.Append("kite_active_connections ").Append(pipeline.ActiveConnections.ToString(CultureInfo.InvariantCulture)).Append('\n');

            sb.AppendLine("# HELP kite_bytes_sent_total Total response bytes sent.");
            sb.AppendLine("# TYPE kite_bytes_sent_total counter");
            sb.Append("kite_bytes_sent_total ").Append(pipeline.TotalBytesSent.ToString(CultureInfo.InvariantCulture)).Append('\n');

            sb.AppendLine("# HELP kite_bytes_received_total Total request bytes received.");
            sb.AppendLine("# TYPE kite_bytes_received_total counter");
            sb.Append("kite_bytes_received_total ").Append(pipeline.TotalBytesReceived.ToString(CultureInfo.InvariantCulture)).Append('\n');

            sb.AppendLine("# HELP kite_managed_heap_bytes Current managed GC heap size in bytes.");
            sb.AppendLine("# TYPE kite_managed_heap_bytes gauge");
            sb.Append("kite_managed_heap_bytes ").Append(GC.GetTotalMemory(false).ToString(CultureInfo.InvariantCulture)).Append('\n');

            sb.AppendLine("# HELP kite_gc_collections_total GC collections by generation.");
            sb.AppendLine("# TYPE kite_gc_collections_total counter");
            sb.Append("kite_gc_collections_total{gen=\"0\"} ").Append(GC.CollectionCount(0).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("kite_gc_collections_total{gen=\"1\"} ").Append(GC.CollectionCount(1).ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("kite_gc_collections_total{gen=\"2\"} ").Append(GC.CollectionCount(2).ToString(CultureInfo.InvariantCulture)).Append('\n');

            if (routeTable is not null)
            {
                sb.AppendLine("# HELP kite_site_requests_total Requests processed per site.");
                sb.AppendLine("# TYPE kite_site_requests_total counter");
                foreach (string siteId in pipeline.GetTrackedSiteIds())
                {
                    SiteRuntimeTelemetry t = pipeline.GetSiteTelemetry(siteId);
                    sb.Append("kite_site_requests_total{site=\"").Append(EscapeLabel(siteId)).Append("\"} ")
                        .Append(Interlocked.Read(ref t.TotalRequests).ToString(CultureInfo.InvariantCulture)).Append('\n');
                }
            }

            if (upstreamManager is not null)
            {
                IReadOnlyList<UpstreamClusterStatus> clusters = upstreamManager.CaptureStatusSnapshot();
                if (clusters.Count > 0)
                {
                    sb.AppendLine("# HELP kite_upstream_destination_healthy Upstream health status (1=healthy, 0=unhealthy).");
                    sb.AppendLine("# TYPE kite_upstream_destination_healthy gauge");
                    sb.AppendLine("# HELP kite_upstream_requests_total Total requests forwarded to upstream destination.");
                    sb.AppendLine("# TYPE kite_upstream_requests_total counter");
                    foreach (UpstreamClusterStatus c in clusters)
                    {
                        foreach (UpstreamDestinationStatus d in c.Destinations)
                        {
                            string cl = EscapeLabel(c.ClusterId);
                            string dl = EscapeLabel(d.Id);
                            sb.Append("kite_upstream_destination_healthy{cluster=\"").Append(cl).Append("\",destination=\"").Append(dl).Append("\"} ")
                                .Append(d.IsHealthy ? "1" : "0").Append('\n');
                            sb.Append("kite_upstream_requests_total{cluster=\"").Append(cl).Append("\",destination=\"").Append(dl).Append("\"} ")
                                .Append(d.TotalRequests.ToString(CultureInfo.InvariantCulture)).Append('\n');
                        }
                    }
                }
            }

            return sb.ToString();
        }
        finally
        {
            KiteObjectPools.StringBuilderPool.Return(sb);
        }
    }

    private static string EscapeLabel(string val) =>
        val.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string EscapeJson(string val) =>
        val.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
