using System.Buffers;
using System.Net;
using System.Text;
using System.Text.Json;
using Kite.Core.Models;
using Kite.Core.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Kite.Server.HttpModules;

/// <summary>
/// Enforces per-site/server IP &amp; Subnet Restrictions (Allow/Deny CIDR rules) and IIS-style Request Filtering
/// (HTTP verb allowlist, max content length, max URL/query lengths, double-escaping block, high-bit char gate,
/// and blocked file extensions) using pre-compiled <see cref="CompiledSiteFeatures"/>.
/// </summary>
public static class RequestFilteringAndIpGateModule
{
    public static bool Evaluate(
        HttpContext context,
        CompiledSiteFeatures compiled,
        out int statusCode,
        out string subStatus,
        out string title,
        out string detail)
    {
        SiteFeaturesConfig cfg = compiled.EffectiveConfig;

        // 1. IP & Domain Restrictions
        if (cfg.IpRestrictions.Enabled)
        {
            IPAddress remoteIp = context.Connection.RemoteIpAddress ?? IPAddress.Loopback;
            if (remoteIp.IsIPv4MappedToIPv6)
            {
                remoteIp = remoteIp.MapToIPv4();
            }

            bool? matchedAllow = null;
            for (int i = 0; i < compiled.IpRules.Count; i++)
            {
                CompiledIpRule rule = compiled.IpRules[i];
                if (rule.Network.Contains(remoteIp))
                {
                    matchedAllow = rule.Allow;
                    break;
                }
            }

            bool allowed = matchedAllow ?? cfg.IpRestrictions.AllowUnmatched;
            if (!allowed)
            {
                statusCode = Math.Clamp(cfg.IpRestrictions.DenyStatusCode, 400, 599);
                subStatus = "403.6";
                title = "IP Address Rejected";
                detail = $"Client IP '{remoteIp}' is denied by configured subnet restrictions (403.6).";
                return false;
            }
        }

        // 2. Request Filtering Gate
        RequestFilteringConfig rf = cfg.RequestFiltering;
        if (rf.Enabled)
        {
            string method = context.Request.Method;

            // Keep RFC 9110 TRACE 405 behavior
            if (HttpMethods.IsTrace(method) ||
                (compiled.AllowedVerbs.Count > 0 && !compiled.AllowedVerbs.Contains(method)))
            {
                statusCode = StatusCodes.Status405MethodNotAllowed;
                subStatus = "405.0";
                title = "Method Not Allowed";
                detail = HttpMethods.IsTrace(method)
                    ? "TRACE method is disabled for security compliance (RFC 9110 Section 9.3.8)."
                    : $"HTTP verb '{method}' is blocked by Request Filtering policy.";
                return false;
            }

            if (rf.MaxAllowedContentLength > 0 &&
                context.Request.ContentLength is long contentLen &&
                contentLen > rf.MaxAllowedContentLength)
            {
                statusCode = StatusCodes.Status413PayloadTooLarge;
                subStatus = "413.1";
                title = "Payload Too Large";
                detail = $"Content-Length ({contentLen} bytes) exceeds MaxAllowedContentLength ({rf.MaxAllowedContentLength} bytes).";
                return false;
            }

            string path = context.Request.Path.Value ?? "/";
            string rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? path;

            if (rf.MaxUrlLength > 0 && path.Length > rf.MaxUrlLength)
            {
                statusCode = StatusCodes.Status414UriTooLong;
                subStatus = "414.0";
                title = "URI Too Long";
                detail = $"Request path length ({path.Length}) exceeds MaxUrlLength ({rf.MaxUrlLength}).";
                return false;
            }

            string query = context.Request.QueryString.Value ?? "";
            if (rf.MaxQueryStringLength > 0 && query.Length > rf.MaxQueryStringLength)
            {
                statusCode = StatusCodes.Status414UriTooLong;
                subStatus = "414.1";
                title = "Query String Too Long";
                detail = $"Query string length ({query.Length}) exceeds MaxQueryStringLength ({rf.MaxQueryStringLength}).";
                return false;
            }

            if (!rf.AllowDoubleEscaping &&
                (rawTarget.Contains("%25", StringComparison.OrdinalIgnoreCase) ||
                 path.Contains("%25", StringComparison.OrdinalIgnoreCase) ||
                 path.Contains("..", StringComparison.Ordinal)))
            {
                statusCode = StatusCodes.Status404NotFound;
                subStatus = "404.11";
                title = "Request Filtering Violation";
                detail = "Double-escaped or traversal sequence detected and blocked (404.11).";
                return false;
            }

            if (!rf.AllowHighBitCharacters)
            {
                for (int i = 0; i < path.Length; i++)
                {
                    if (path[i] > 0x7F)
                    {
                        statusCode = StatusCodes.Status400BadRequest;
                        subStatus = "400.0";
                        title = "Bad Request";
                        detail = "High-bit non-ASCII URL characters are disallowed by Request Filtering.";
                        return false;
                    }
                }
            }

            if (compiled.BlockedExtensions.Count > 0)
            {
                ReadOnlySpan<char> ext = Path.GetExtension(path.AsSpan());
                if (!ext.IsEmpty && compiled.BlockedExtensions.Contains(ext.ToString()))
                {
                    statusCode = StatusCodes.Status404NotFound;
                    subStatus = "404.7";
                    title = "File Extension Blocked";
                    detail = $"Extension '{ext}' is blocked by Request Filtering policy (404.7).";
                    return false;
                }
            }
        }

        // 3. Zero-Allocation Edge WAF (SQLi / XSS / Traversal / User-Agent inspection via precompiled SearchValues)
        if (cfg.EdgeWaf.Enabled)
        {
            if (compiled.WafUserAgentPatterns is SearchValues<string> uaPatterns &&
                context.Request.Headers.TryGetValue("User-Agent", out var uaValues))
            {
                for (int i = 0; i < uaValues.Count; i++)
                {
                    string? ua = uaValues[i];
                    if (!string.IsNullOrEmpty(ua) && ua.AsSpan().ContainsAny(uaPatterns))
                    {
                        statusCode = StatusCodes.Status403Forbidden;
                        subStatus = "403.501";
                        title = "Edge WAF Blocked";
                        detail = "Request User-Agent matched an Edge WAF security rule (403.501).";
                        return false;
                    }
                }
            }

            if (compiled.WafTargetPatterns is SearchValues<string> targetPatterns)
            {
                ReadOnlySpan<char> pathSpan = (context.Request.Path.Value ?? "/").AsSpan();
                ReadOnlySpan<char> querySpan = (context.Request.QueryString.Value ?? "").AsSpan();
                ReadOnlySpan<char> rawSpan = (context.Features.Get<IHttpRequestFeature>()?.RawTarget ?? "").AsSpan();

                if (pathSpan.ContainsAny(targetPatterns) ||
                    querySpan.ContainsAny(targetPatterns) ||
                    rawSpan.ContainsAny(targetPatterns))
                {
                    statusCode = StatusCodes.Status403Forbidden;
                    subStatus = "403.501";
                    title = "Edge WAF Blocked";
                    detail = "Request target matched a SQLi, XSS, or path traversal Edge WAF signature (403.501).";
                    return false;
                }
            }
        }

        statusCode = StatusCodes.Status200OK;
        subStatus = "";
        title = "";
        detail = "";
        return true;
    }

    /// <summary>
    /// Performs bounded request-body Edge WAF inspection (SQLi/XSS/traversal patterns and JSON max nesting depth)
    /// while buffering and rewinding the request stream so downstream handlers read the intact payload.
    /// </summary>
    public static async ValueTask<(bool Allowed, int StatusCode, string SubStatus, string Title, string Detail)> EvaluateBodyAsync(
        HttpContext context,
        CompiledSiteFeatures compiled,
        CancellationToken ct)
    {
        EdgeWafConfig waf = compiled.EffectiveConfig.EdgeWaf;
        if (!waf.Enabled || !waf.InspectRequestBody)
        {
            return (true, StatusCodes.Status200OK, "", "", "");
        }

        string method = context.Request.Method;
        if (!HttpMethods.IsPost(method) && !HttpMethods.IsPut(method) && !HttpMethods.IsPatch(method))
        {
            return (true, StatusCodes.Status200OK, "", "", "");
        }

        int maxBytes = Math.Clamp(waf.MaxBodyInspectBytes, 256, 1024 * 1024);
        context.Request.EnableBuffering(maxBytes);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(maxBytes);
        try
        {
            int totalRead = 0;
            while (totalRead < maxBytes)
            {
                int n = await context.Request.Body.ReadAsync(buffer.AsMemory(totalRead, maxBytes - totalRead), ct).ConfigureAwait(false);
                if (n <= 0)
                {
                    break;
                }
                totalRead += n;
            }

            if (context.Request.Body.CanSeek)
            {
                context.Request.Body.Position = 0;
            }

            if (totalRead == 0)
            {
                return (true, StatusCodes.Status200OK, "", "", "");
            }

            ReadOnlySpan<byte> bodySpan = buffer.AsSpan(0, totalRead);
            if (compiled.WafTargetPatterns is SearchValues<string> patterns)
            {
                string decoded = Encoding.UTF8.GetString(bodySpan);
                if (decoded.AsSpan().ContainsAny(patterns))
                {
                    return (false, StatusCodes.Status403Forbidden, "403.502", "Edge WAF Body Blocked",
                        "Request body matched an Edge WAF payload inspection rule (403.502).");
                }
            }

            if (waf.MaxJsonDepth > 0 &&
                context.Request.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
            {
                try
                {
                    var reader = new Utf8JsonReader(bodySpan, new JsonReaderOptions
                    {
                        MaxDepth = Math.Clamp(waf.MaxJsonDepth, 1, 256),
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    });
                    while (reader.Read())
                    {
                    }
                }
                catch (JsonException ex) when (ex.Message.Contains("depth", StringComparison.OrdinalIgnoreCase))
                {
                    return (false, StatusCodes.Status400BadRequest, "400.502", "JSON Depth Exceeded",
                        $"JSON nesting depth exceeded Edge WAF limit ({waf.MaxJsonDepth}).");
                }
                catch (JsonException)
                {
                    // Partial body beyond MaxBodyInspectBytes may truncate JSON; ignore non-depth errors
                }
            }

            return (true, StatusCodes.Status200OK, "", "", "");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
