using System.Text;
using System.Text.RegularExpressions;
using Kite.Core.Memory;
using Kite.Core.Models;
using Kite.Core.Routing;
using Microsoft.AspNetCore.Http;

namespace Kite.Server.HttpModules;

public enum UrlRewriteOutcome
{
    Continue,
    RewrittenInPlace,
    Responded
}

/// <summary>
/// Evaluates global redirects, canonical trailing-slash enforcement, and compiled regex <see cref="UrlRewriteConfig"/>
/// rules (supporting <c>{R:1}</c>..<c>{R:9}</c>, <c>{HTTP_HOST}</c>, <c>{REQUEST_URI}</c> backreferences).
/// </summary>
public static class UrlRewriteModule
{
    public static async ValueTask<UrlRewriteOutcome> ExecuteAsync(
        HttpContext context,
        CompiledSiteFeatures compiled,
        string host,
        CancellationToken ct)
    {
        SiteFeaturesConfig cfg = compiled.EffectiveConfig;
        string path = context.Request.Path.Value ?? "/";
        string query = context.Request.QueryString.Value ?? "";

        // Never rewrite ACME HTTP-01 challenge when enabled
        if (cfg.TlsPolicy.EnableAcmeHttp01PassThrough &&
            path.StartsWith("/.well-known/acme-challenge/", StringComparison.OrdinalIgnoreCase))
        {
            return UrlRewriteOutcome.Continue;
        }

        // 1. Global site redirect
        if (!string.IsNullOrWhiteSpace(cfg.RedirectsAndErrors.GlobalRedirectUrl))
        {
            string target = cfg.RedirectsAndErrors.GlobalRedirectUrl
                .Replace("{REQUEST_URI}", path + query, StringComparison.OrdinalIgnoreCase)
                .Replace("{HTTP_HOST}", host, StringComparison.OrdinalIgnoreCase);

            context.Response.StatusCode = cfg.RedirectsAndErrors.PermanentRedirect
                ? StatusCodes.Status301MovedPermanently
                : StatusCodes.Status302Found;
            context.Response.Headers["Location"] = target;
            return UrlRewriteOutcome.Responded;
        }

        // 2. Canonical trailing slash enforcement
        if (cfg.RedirectsAndErrors.EnforceTrailingSlash &&
            (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) &&
            path.Length > 1 &&
            !path.EndsWith('/') &&
            Path.GetExtension(path.AsSpan()).IsEmpty)
        {
            context.Response.StatusCode = cfg.RedirectsAndErrors.PermanentRedirect
                ? StatusCodes.Status301MovedPermanently
                : StatusCodes.Status302Found;
            context.Response.Headers["Location"] = $"{path}/{query}";
            return UrlRewriteOutcome.Responded;
        }

        // 3. Ordered URL Rewrite Rules
        if (!cfg.UrlRewrite.Enabled || compiled.RewriteRules.Count == 0)
        {
            return UrlRewriteOutcome.Continue;
        }

        bool anyRewrite = false;
        string workingPath = path;

        for (int i = 0; i < compiled.RewriteRules.Count; i++)
        {
            CompiledRewriteRule compiledRule = compiled.RewriteRules[i];
            UrlRewriteRuleConfig rule = compiledRule.Config;

            if (rule.RequireHttps && context.Request.IsHttps)
            {
                continue;
            }

            Match? match = null;
            bool isMatch = false;

            if (rule.NegatePattern)
            {
                isMatch = !compiledRule.CompiledRegex.IsMatch(workingPath);
            }
            else
            {
                match = compiledRule.CompiledRegex.Match(workingPath);
                if (!match.Success && workingPath.Length > 0 && !rule.Pattern.StartsWith('/'))
                {
                    // Allow patterns written as ^api/(.*)$ as well as ^/api/(.*)$
                    match = compiledRule.CompiledRegex.Match(workingPath.TrimStart('/'));
                }
                isMatch = match.Success;
            }

            if (!isMatch)
            {
                continue;
            }

            string expanded = ExpandTargetTokens(rule.Target, match, host, workingPath, query);

            switch (rule.ActionType)
            {
                case UrlRewriteActionType.Redirect301:
                    context.Response.StatusCode = StatusCodes.Status301MovedPermanently;
                    context.Response.Headers["Location"] = expanded;
                    return UrlRewriteOutcome.Responded;

                case UrlRewriteActionType.Redirect302:
                    context.Response.StatusCode = StatusCodes.Status302Found;
                    context.Response.Headers["Location"] = expanded;
                    return UrlRewriteOutcome.Responded;

                case UrlRewriteActionType.CustomResponse:
                    context.Response.StatusCode = Math.Clamp(rule.CustomStatusCode, 200, 599);
                    if (!string.IsNullOrEmpty(expanded))
                    {
                        byte[] bytes = Encoding.UTF8.GetBytes(expanded);
                        context.Response.ContentType = "text/plain; charset=utf-8";
                        context.Response.ContentLength = bytes.Length;
                        await context.Response.Body.WriteAsync(bytes, ct).ConfigureAwait(false);
                    }
                    return UrlRewriteOutcome.Responded;

                case UrlRewriteActionType.Rewrite:
                case UrlRewriteActionType.ReverseProxy:
                default:
                    int qIdx = expanded.IndexOf('?');
                    if (qIdx >= 0)
                    {
                        string newPath = expanded[..qIdx];
                        string newQuery = expanded[qIdx..];
                        workingPath = newPath.StartsWith('/') ? newPath : "/" + newPath;
                        context.Request.QueryString = new QueryString(newQuery);
                    }
                    else
                    {
                        workingPath = expanded.StartsWith('/') ? expanded : "/" + expanded;
                    }

                    context.Request.Path = new PathString(workingPath);
                    anyRewrite = true;
                    break;
            }

            if (rule.StopProcessing)
            {
                break;
            }
        }

        return anyRewrite ? UrlRewriteOutcome.RewrittenInPlace : UrlRewriteOutcome.Continue;
    }

    private static string ExpandTargetTokens(
        string template,
        Match? match,
        string host,
        string path,
        string query)
    {
        if (string.IsNullOrEmpty(template) || !template.Contains('{'))
        {
            return template;
        }

        StringBuilder sb = KiteObjectPools.StringBuilderPool.Get();
        try
        {
            sb.Append(template);
            sb.Replace("{HTTP_HOST}", host);
            sb.Replace("{REQUEST_URI}", path + query);
            sb.Replace("{QUERY_STRING}", query.TrimStart('?'));

            if (match is not null)
            {
                for (int g = 0; g < Math.Min(10, match.Groups.Count); g++)
                {
                    sb.Replace($"{{R:{g}}}", match.Groups[g].Value);
                }
            }

            return sb.ToString();
        }
        finally
        {
            KiteObjectPools.StringBuilderPool.Return(sb);
        }
    }
}
