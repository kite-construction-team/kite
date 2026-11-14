using Kite.Core.Models;
using Microsoft.AspNetCore.Http;

namespace Kite.Core.Routing;

/// <summary>
/// Result of evaluating <see cref="SiteRouteRuleConfig"/> rules and transforms against a request.
/// </summary>
public readonly record struct RouteMatchResult(
    SiteRouteRuleConfig? Rule,
    string TransformedPath);

/// <summary>
/// Evaluates declarative API gateway <see cref="SiteRouteRuleConfig"/> rules (path, method, headers, query)
/// and applies path prefix stripping or rewriting without dynamic regex backtracking.
/// </summary>
public static class RouteRuleMatcher
{
    /// <summary>
    /// Finds the first matching enabled route rule for the given request, preferring rules in config order.
    /// </summary>
    public static RouteMatchResult Match(IReadOnlyList<SiteRouteRuleConfig> rules, HttpRequest request)
    {
        string path = request.Path.Value ?? "/";
        if (rules.Count == 0)
        {
            return new RouteMatchResult(null, path);
        }

        string method = request.Method;
        for (int i = 0; i < rules.Count; i++)
        {
            SiteRouteRuleConfig rule = rules[i];
            if (!rule.Enabled)
            {
                continue;
            }

            if (!MatchesPath(rule, path) ||
                !MatchesMethod(rule.Methods, method) ||
                !MatchesHeaders(rule.MatchHeaders, request.Headers) ||
                !MatchesQuery(rule.MatchQueryParams, request.Query))
            {
                continue;
            }

            string transformedPath = ApplyPathTransform(path, rule.Transform);
            return new RouteMatchResult(rule, transformedPath);
        }

        return new RouteMatchResult(null, path);
    }

    public static bool MatchesPath(SiteRouteRuleConfig rule, string path)
    {
        string prefix = string.IsNullOrEmpty(rule.PathPrefix) ? "/" : rule.PathPrefix;
        if (!prefix.StartsWith('/'))
        {
            prefix = "/" + prefix;
        }

        if (rule.ExactPathOnly)
        {
            return string.Equals(path, prefix, StringComparison.OrdinalIgnoreCase);
        }

        if (prefix == "/")
        {
            return true;
        }

        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return path.Length == prefix.Length || prefix.EndsWith('/') || path[prefix.Length] == '/';
    }

    public static string ApplyPathTransform(string path, RouteTransformConfig transform)
    {
        string result = path;
        if (!string.IsNullOrEmpty(transform.StripPathPrefix))
        {
            string strip = transform.StripPathPrefix.StartsWith('/')
                ? transform.StripPathPrefix
                : "/" + transform.StripPathPrefix;
            if (strip.Length > 1 && strip.EndsWith('/'))
            {
                strip = strip.TrimEnd('/');
            }

            if (result.StartsWith(strip, StringComparison.OrdinalIgnoreCase) &&
                (result.Length == strip.Length || result[strip.Length] == '/'))
            {
                result = result.Length == strip.Length ? "/" : result[strip.Length..];
            }
        }

        if (!string.IsNullOrEmpty(transform.RewritePathPrefix))
        {
            string rewrite = transform.RewritePathPrefix.StartsWith('/')
                ? transform.RewritePathPrefix
                : "/" + transform.RewritePathPrefix;
            rewrite = rewrite.TrimEnd('/');
            result = result == "/" ? (string.IsNullOrEmpty(rewrite) ? "/" : rewrite) : rewrite + result;
        }

        return string.IsNullOrEmpty(result) ? "/" : result;
    }

    private static bool MatchesMethod(IReadOnlyList<string> methods, string requestMethod)
    {
        if (methods.Count == 0)
        {
            return true;
        }

        for (int i = 0; i < methods.Count; i++)
        {
            if (string.Equals(methods[i], requestMethod, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesHeaders(IReadOnlyDictionary<string, string> expected, IHeaderDictionary headers)
    {
        if (expected.Count == 0)
        {
            return true;
        }

        foreach (KeyValuePair<string, string> kvp in expected)
        {
            if (!headers.TryGetValue(kvp.Key, out var actual))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(kvp.Value) &&
                !string.Equals(actual.ToString(), kvp.Value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesQuery(IReadOnlyDictionary<string, string> expected, IQueryCollection query)
    {
        if (expected.Count == 0)
        {
            return true;
        }

        foreach (KeyValuePair<string, string> kvp in expected)
        {
            if (!query.TryGetValue(kvp.Key, out var actual))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(kvp.Value) &&
                !string.Equals(actual.ToString(), kvp.Value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
