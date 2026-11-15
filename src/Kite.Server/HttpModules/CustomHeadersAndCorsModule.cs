using Kite.Core.Models;
using Kite.Core.Routing;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Kite.Server.HttpModules;

/// <summary>
/// Applies defensive HTTP compliance headers (RFC 6797 HSTS, <c>nosniff</c>, Server header stripping,
/// custom response headers, header removal) and enforces explicit CORS policies including <c>OPTIONS</c> preflight.
/// </summary>
public static class CustomHeadersAndCorsModule
{
    public static bool TryApplyCorsAndHandlePreflight(HttpContext context, CompiledSiteFeatures compiled)
    {
        CorsPolicyConfig cors = compiled.EffectiveConfig.Cors;
        if (!cors.Enabled)
        {
            return false;
        }

        if (!context.Request.Headers.TryGetValue("Origin", out StringValues originVal) ||
            StringValues.IsNullOrEmpty(originVal))
        {
            return false;
        }

        string origin = originVal.ToString().Trim().TrimEnd('/');
        bool allowAll = compiled.CorsAllowedOrigins.Contains("*");
        bool matched = allowAll || compiled.CorsAllowedOrigins.Contains(origin);

        if (matched)
        {
            context.Response.Headers["Access-Control-Allow-Origin"] = allowAll && !cors.AllowCredentials ? "*" : origin;
            context.Response.Headers["Vary"] = "Origin";
            context.Response.Headers["Access-Control-Allow-Methods"] = cors.AllowedMethods;
            context.Response.Headers["Access-Control-Allow-Headers"] = cors.AllowedHeaders;
            if (cors.AllowCredentials)
            {
                context.Response.Headers["Access-Control-Allow-Credentials"] = "true";
            }
        }

        if (HttpMethods.IsOptions(context.Request.Method) &&
            context.Request.Headers.ContainsKey("Access-Control-Request-Method"))
        {
            context.Response.StatusCode = matched ? StatusCodes.Status204NoContent : StatusCodes.Status403Forbidden;
            return true;
        }

        return false;
    }

    private static readonly StringValues ServerHeaderValue = new("Kite/2.0");
    private static readonly StringValues NoSniffHeaderValue = new("nosniff");

    public static void ApplyResponseHeaders(HttpContext context, CompiledSiteFeatures compiled)
    {
        SiteFeaturesConfig features = compiled.EffectiveConfig;
        PipelineActiveModules flags = compiled.ActiveModules;
        IHeaderDictionary headers = context.Response.Headers;
        int initialCount = headers.Count;

        if (!features.StripServerHeader)
        {
            headers.Server = ServerHeaderValue;
        }
        else if (initialCount > 0)
        {
            headers.Remove("Server");
        }

        if ((flags & PipelineActiveModules.SuppressXContentTypeOptions) == 0 &&
            (initialCount == 0 || !headers.ContainsKey("X-Content-Type-Options")))
        {
            headers.XContentTypeOptions = NoSniffHeaderValue;
        }

        if (features.Hsts.Enabled)
        {
            string hstsValue = features.Hsts.IncludeSubDomains
                ? (features.Hsts.Preload
                    ? $"max-age={features.Hsts.MaxAgeSeconds}; includeSubDomains; preload"
                    : $"max-age={features.Hsts.MaxAgeSeconds}; includeSubDomains")
                : $"max-age={features.Hsts.MaxAgeSeconds}";
            headers.StrictTransportSecurity = hstsValue;
        }

        if ((flags & PipelineActiveModules.CustomHeaders) != 0)
        {
            foreach (KeyValuePair<string, string> custom in features.CustomHeaders)
            {
                headers[custom.Key] = custom.Value;
            }
        }

        if ((flags & PipelineActiveModules.RemoveHeaders) != 0 &&
            headers.Count > 0 &&
            (initialCount > 0 || (flags & PipelineActiveModules.CustomHeaders) != 0 || features.Hsts.Enabled))
        {
            for (int i = 0; i < features.RemoveHeaders.Count; i++)
            {
                headers.Remove(features.RemoveHeaders[i]);
            }
        }
    }
}
