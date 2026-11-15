using System.Buffers;
using System.Collections.Frozen;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Kite.Core.Models;

namespace Kite.Core.Routing;

public sealed record CompiledIpRule(IPNetwork Network, bool Allow, string RawCidr, string Description);

public sealed record CompiledRewriteRule(
    UrlRewriteRuleConfig Config,
    Regex CompiledRegex);

public sealed record StatusCodeRange(int Min, int Max);

[Flags]
public enum PipelineActiveModules
{
    None = 0,
    HttpsRedirect = 1 << 0,
    Mtls = 1 << 1,
    Cors = 1 << 2,
    CustomHeaders = 1 << 3,
    RemoveHeaders = 1 << 4,
    IpOrRequestOrWafGate = 1 << 5,
    WafBodyInspection = 1 << 6,
    RateLimiting = 1 << 7,
    UrlRewriteOrRedirect = 1 << 8,
    Authentication = 1 << 9,
    ForwardAuth = 1 << 10,
    FrebTracing = 1 << 11,
    W3COrJsonLogging = 1 << 12,
    SuppressXContentTypeOptions = 1 << 13
}

/// <summary>
/// Immutable, lock-free compiled representation of a site's effective <see cref="SiteFeaturesConfig"/>
/// (inheriting <see cref="ServerConfig.Features"/> when configured). Compiled once per config reload
/// so the HTTP request pipeline performs zero per-request config parsing or regex compilation.
/// </summary>
public sealed class CompiledSiteFeatures
{
    public required SiteFeaturesConfig EffectiveConfig { get; init; }
    public required bool InheritingFromServer { get; init; }
    public required IReadOnlyList<CompiledRewriteRule> RewriteRules { get; init; }
    public required IReadOnlyList<CompiledIpRule> IpRules { get; init; }
    public required FrozenSet<string> AllowedVerbs { get; init; }
    public required FrozenSet<string> BlockedExtensions { get; init; }
    public required FrozenSet<string> AllowedApiKeys { get; init; }
    public required FrozenSet<string> CorsAllowedOrigins { get; init; }
    public required FrozenDictionary<string, string> MimeMappings { get; init; }
    public required FrozenDictionary<string, string>.AlternateLookup<ReadOnlySpan<char>> MimeSpanLookup { get; init; }
    public required IReadOnlyList<StatusCodeRange> FrebStatusTriggers { get; init; }
    public required byte[]? JwtSymmetricKeyBytes { get; init; }
    public required RSA? JwtRsaPublicKey { get; init; }
    public required FrozenSet<string> TrustedMtlsThumbprints { get; init; }
    public required SearchValues<string>? WafTargetPatterns { get; init; }
    public required SearchValues<string>? WafUserAgentPatterns { get; init; }
    public PipelineActiveModules ActiveModules { get; init; }

    public bool IsFrebStatusTriggered(int statusCode)
    {
        for (int i = 0; i < FrebStatusTriggers.Count; i++)
        {
            StatusCodeRange range = FrebStatusTriggers[i];
            if (statusCode >= range.Min && statusCode <= range.Max)
            {
                return true;
            }
        }
        return false;
    }

    public static CompiledSiteFeatures Compile(SiteConfig site, ServerConfig? server = null)
    {
        SiteFeaturesConfig effective = ResolveEffectiveConfig(site.Features, server?.Features, out bool inherited);

        var rewriteRules = new List<CompiledRewriteRule>(effective.UrlRewrite.Rules.Count);
        if (effective.UrlRewrite.Enabled)
        {
            foreach (UrlRewriteRuleConfig rule in effective.UrlRewrite.Rules)
            {
                if (!rule.Enabled || string.IsNullOrEmpty(rule.Pattern))
                {
                    continue;
                }

                Regex regex = CompileSafeRegex(rule.Pattern);
                rewriteRules.Add(new CompiledRewriteRule(rule, regex));
            }
        }

        var ipRules = new List<CompiledIpRule>(effective.IpRestrictions.Rules.Count);
        if (effective.IpRestrictions.Enabled)
        {
            foreach (IpSubnetRuleConfig r in effective.IpRestrictions.Rules)
            {
                if (TryParseCidrOrIp(r.Cidr, out IPNetwork network))
                {
                    ipRules.Add(new CompiledIpRule(network, r.Allow, r.Cidr, r.Description));
                }
            }
        }

        var allowedVerbs = effective.RequestFiltering.AllowedVerbs
            .Where(static v => !string.IsNullOrWhiteSpace(v))
            .Select(static v => v.Trim())
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        var blockedExts = effective.RequestFiltering.BlockedExtensions
            .Where(static e => !string.IsNullOrWhiteSpace(e))
            .Select(static e => e.StartsWith('.') ? e.Trim() : "." + e.Trim())
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        var apiKeys = effective.Authentication.AllowedApiKeys
            .Where(static k => !string.IsNullOrWhiteSpace(k))
            .ToFrozenSet(StringComparer.Ordinal);

        var corsOrigins = effective.Cors.AllowedOrigins
            .Where(static o => !string.IsNullOrWhiteSpace(o))
            .Select(static o => o.Trim().TrimEnd('/'))
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        var mimeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in effective.MimeMappings.CustomMappings)
        {
            string key = kvp.Key.StartsWith('.') ? kvp.Key : "." + kvp.Key;
            mimeMap[key] = kvp.Value;
        }
        FrozenDictionary<string, string> frozenMimes = mimeMap.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<StatusCodeRange> frebTriggers = ParseStatusCodeTriggers(effective.FailedRequestTracing.StatusCodeTrigger);

        byte[]? hmacKey = null;
        RSA? rsaKey = null;
        if (effective.Authentication.JwtEnabled && !string.IsNullOrWhiteSpace(effective.Authentication.JwtSigningKey))
        {
            string rawKey = effective.Authentication.JwtSigningKey.Trim();
            if (rawKey.Contains("BEGIN PUBLIC KEY", StringComparison.OrdinalIgnoreCase) ||
                rawKey.Contains("BEGIN RSA PUBLIC KEY", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    rsaKey = RSA.Create();
                    rsaKey.ImportFromPem(rawKey);
                }
                catch
                {
                    rsaKey?.Dispose();
                    rsaKey = null;
                }
            }
            else
            {
                hmacKey = Encoding.UTF8.GetBytes(rawKey);
            }
        }

        FrozenSet<string> trustedThumbprints = effective.TlsPolicy.Mtls.TrustedThumbprints
            .Where(static t => !string.IsNullOrWhiteSpace(t))
            .Select(static t => t.Replace(":", "", StringComparison.Ordinal).Trim())
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        (SearchValues<string>? wafTargetPatterns, SearchValues<string>? wafUaPatterns) = CompileWafPatterns(effective.EdgeWaf);

        PipelineActiveModules activeModules = PipelineActiveModules.None;
        if (effective.TlsPolicy.RedirectHttpToHttps) activeModules |= PipelineActiveModules.HttpsRedirect;
        if (effective.TlsPolicy.Mtls.ClientCertificateMode != MtlsClientCertificateMode.None) activeModules |= PipelineActiveModules.Mtls;
        if (effective.Cors.Enabled) activeModules |= PipelineActiveModules.Cors;
        if (effective.CustomHeaders.Count > 0) activeModules |= PipelineActiveModules.CustomHeaders;
        if (effective.RemoveHeaders.Count > 0) activeModules |= PipelineActiveModules.RemoveHeaders;
        if (effective.RemoveHeaders.Any(static h => string.Equals(h, "X-Content-Type-Options", StringComparison.OrdinalIgnoreCase)))
        {
            activeModules |= PipelineActiveModules.SuppressXContentTypeOptions;
        }
        if (effective.IpRestrictions.Enabled || effective.RequestFiltering.Enabled || effective.EdgeWaf.Enabled)
        {
            activeModules |= PipelineActiveModules.IpOrRequestOrWafGate;
        }
        if (effective.EdgeWaf.Enabled && effective.EdgeWaf.InspectRequestBody)
        {
            activeModules |= PipelineActiveModules.WafBodyInspection;
        }
        if (effective.RateLimiting.Enabled) activeModules |= PipelineActiveModules.RateLimiting;
        if (!string.IsNullOrWhiteSpace(effective.RedirectsAndErrors.GlobalRedirectUrl) ||
            effective.RedirectsAndErrors.EnforceTrailingSlash ||
            (effective.UrlRewrite.Enabled && rewriteRules.Count > 0))
        {
            activeModules |= PipelineActiveModules.UrlRewriteOrRedirect;
        }
        if (!effective.Authentication.AnonymousEnabled ||
            effective.Authentication.JwtEnabled ||
            effective.Authentication.ApiKeyEnabled ||
            effective.Authentication.BasicAuthEnabled)
        {
            activeModules |= PipelineActiveModules.Authentication;
        }
        if (effective.ForwardAuth.Enabled && !string.IsNullOrWhiteSpace(effective.ForwardAuth.EndpointUrl))
        {
            activeModules |= PipelineActiveModules.ForwardAuth;
        }
        if (effective.FailedRequestTracing.Enabled) activeModules |= PipelineActiveModules.FrebTracing;
        if (effective.W3CLoggingEnabled) activeModules |= PipelineActiveModules.W3COrJsonLogging;

        return new CompiledSiteFeatures
        {
            EffectiveConfig = effective,
            InheritingFromServer = inherited,
            RewriteRules = rewriteRules,
            IpRules = ipRules,
            AllowedVerbs = allowedVerbs,
            BlockedExtensions = blockedExts,
            AllowedApiKeys = apiKeys,
            CorsAllowedOrigins = corsOrigins,
            MimeMappings = frozenMimes,
            MimeSpanLookup = frozenMimes.GetAlternateLookup<ReadOnlySpan<char>>(),
            FrebStatusTriggers = frebTriggers,
            JwtSymmetricKeyBytes = hmacKey,
            JwtRsaPublicKey = rsaKey,
            TrustedMtlsThumbprints = trustedThumbprints,
            WafTargetPatterns = wafTargetPatterns,
            WafUserAgentPatterns = wafUaPatterns,
            ActiveModules = activeModules
        };
    }

    public static SiteFeaturesConfig ResolveEffectiveConfig(
        SiteFeaturesConfig siteFeatures,
        SiteFeaturesConfig? serverFeatures,
        out bool inherited)
    {
        if (serverFeatures is null || !siteFeatures.InheritServerDefaults)
        {
            inherited = false;
            return siteFeatures;
        }

        inherited = true;

        var mergedHeaders = new Dictionary<string, string>(serverFeatures.CustomHeaders, StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in siteFeatures.CustomHeaders)
        {
            mergedHeaders[kvp.Key] = kvp.Value;
        }

        var mergedMime = new Dictionary<string, string>(serverFeatures.MimeMappings.CustomMappings, StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in siteFeatures.MimeMappings.CustomMappings)
        {
            mergedMime[kvp.Key] = kvp.Value;
        }

        var mergedErrorPages = new Dictionary<int, string>(serverFeatures.RedirectsAndErrors.CustomErrorPages);
        foreach (var kvp in siteFeatures.RedirectsAndErrors.CustomErrorPages)
        {
            mergedErrorPages[kvp.Key] = kvp.Value;
        }

        var mergedBasicUsers = new Dictionary<string, string>(serverFeatures.Authentication.BasicAuthUsers, StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in siteFeatures.Authentication.BasicAuthUsers)
        {
            mergedBasicUsers[kvp.Key] = kvp.Value;
        }

        var mergedRewriteRules = new List<UrlRewriteRuleConfig>(serverFeatures.UrlRewrite.Rules.Count + siteFeatures.UrlRewrite.Rules.Count);
        mergedRewriteRules.AddRange(serverFeatures.UrlRewrite.Rules);
        mergedRewriteRules.AddRange(siteFeatures.UrlRewrite.Rules);

        var mergedIpRules = new List<IpSubnetRuleConfig>(serverFeatures.IpRestrictions.Rules.Count + siteFeatures.IpRestrictions.Rules.Count);
        mergedIpRules.AddRange(serverFeatures.IpRestrictions.Rules);
        mergedIpRules.AddRange(siteFeatures.IpRestrictions.Rules);

        var mergedApiKeys = serverFeatures.Authentication.AllowedApiKeys
            .Concat(siteFeatures.Authentication.AllowedApiKeys)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var mergedCorsOrigins = serverFeatures.Cors.AllowedOrigins
            .Concat(siteFeatures.Cors.AllowedOrigins)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var mergedRemoveHeaders = serverFeatures.RemoveHeaders
            .Concat(siteFeatures.RemoveHeaders)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var defaultRf = new RequestFilteringConfig();
        RequestFilteringConfig srvRf = serverFeatures.RequestFiltering;
        RequestFilteringConfig siteRf = siteFeatures.RequestFiltering;

        var merged = new SiteFeaturesConfig
        {
            InheritServerDefaults = true,
            DefaultDocuments = siteFeatures.DefaultDocuments.Count > 0 ? siteFeatures.DefaultDocuments : serverFeatures.DefaultDocuments,
            DirectoryBrowsing = siteFeatures.DirectoryBrowsing || serverFeatures.DirectoryBrowsing,
            W3CLoggingEnabled = siteFeatures.W3CLoggingEnabled && serverFeatures.W3CLoggingEnabled,
            StripServerHeader = serverFeatures.StripServerHeader && siteFeatures.StripServerHeader,
            RemoveHeaders = mergedRemoveHeaders,
            CustomHeaders = mergedHeaders,
            Compression = new SiteCompressionConfig
            {
                Enabled = siteFeatures.Compression.Enabled && serverFeatures.Compression.Enabled,
                MinSizeBytes = Math.Min(siteFeatures.Compression.MinSizeBytes, serverFeatures.Compression.MinSizeBytes),
                Encodings = siteFeatures.Compression.Encodings.Count > 0 ? siteFeatures.Compression.Encodings : serverFeatures.Compression.Encodings,
                BrotliLevel = siteFeatures.Compression.BrotliLevel != 5 ? siteFeatures.Compression.BrotliLevel : serverFeatures.Compression.BrotliLevel,
                ZstdLevel = siteFeatures.Compression.ZstdLevel != 3 ? siteFeatures.Compression.ZstdLevel : serverFeatures.Compression.ZstdLevel,
                GzipLevel = siteFeatures.Compression.GzipLevel != 7 ? siteFeatures.Compression.GzipLevel : serverFeatures.Compression.GzipLevel
            },
            Caching = new SiteCachingConfig
            {
                Enabled = siteFeatures.Caching.Enabled && serverFeatures.Caching.Enabled,
                DefaultMaxAgeSeconds = siteFeatures.Caching.DefaultMaxAgeSeconds,
                MaxCacheBodyBytes = Math.Min(siteFeatures.Caching.MaxCacheBodyBytes, serverFeatures.Caching.MaxCacheBodyBytes),
                MaxCacheSizeMegabytes = Math.Min(siteFeatures.Caching.MaxCacheSizeMegabytes, serverFeatures.Caching.MaxCacheSizeMegabytes),
                FlushGeneration = Math.Max(serverFeatures.Caching.FlushGeneration, siteFeatures.Caching.FlushGeneration)
            },
            Hsts = new SiteHstsConfig
            {
                Enabled = siteFeatures.Hsts.Enabled || serverFeatures.Hsts.Enabled,
                MaxAgeSeconds = siteFeatures.Hsts.Enabled ? siteFeatures.Hsts.MaxAgeSeconds : serverFeatures.Hsts.MaxAgeSeconds,
                IncludeSubDomains = siteFeatures.Hsts.IncludeSubDomains && serverFeatures.Hsts.IncludeSubDomains,
                Preload = siteFeatures.Hsts.Preload || serverFeatures.Hsts.Preload
            },
            UrlRewrite = new UrlRewriteConfig
            {
                Enabled = serverFeatures.UrlRewrite.Enabled && siteFeatures.UrlRewrite.Enabled,
                Rules = mergedRewriteRules
            },
            RedirectsAndErrors = new RedirectAndErrorsConfig
            {
                EnforceTrailingSlash = serverFeatures.RedirectsAndErrors.EnforceTrailingSlash || siteFeatures.RedirectsAndErrors.EnforceTrailingSlash,
                GlobalRedirectUrl = !string.IsNullOrWhiteSpace(siteFeatures.RedirectsAndErrors.GlobalRedirectUrl)
                    ? siteFeatures.RedirectsAndErrors.GlobalRedirectUrl
                    : serverFeatures.RedirectsAndErrors.GlobalRedirectUrl,
                PermanentRedirect = siteFeatures.RedirectsAndErrors.PermanentRedirect && serverFeatures.RedirectsAndErrors.PermanentRedirect,
                ErrorMode = siteFeatures.RedirectsAndErrors.ErrorMode != CustomErrorMode.PassThrough
                    ? siteFeatures.RedirectsAndErrors.ErrorMode
                    : serverFeatures.RedirectsAndErrors.ErrorMode,
                CustomErrorPages = mergedErrorPages
            },
            MimeMappings = new MimeMappingsConfig
            {
                FallbackContentType = serverFeatures.MimeMappings.FallbackContentType,
                CustomMappings = mergedMime
            },
            Cors = new CorsPolicyConfig
            {
                Enabled = serverFeatures.Cors.Enabled || siteFeatures.Cors.Enabled,
                AllowedOrigins = mergedCorsOrigins,
                AllowedMethods = serverFeatures.Cors.AllowedMethods,
                AllowedHeaders = serverFeatures.Cors.AllowedHeaders,
                AllowCredentials = serverFeatures.Cors.AllowCredentials || siteFeatures.Cors.AllowCredentials
            },
            TlsPolicy = new TlsPolicyConfig
            {
                EnforceTls13 = serverFeatures.TlsPolicy.EnforceTls13 || siteFeatures.TlsPolicy.EnforceTls13,
                StrictSni = serverFeatures.TlsPolicy.StrictSni && siteFeatures.TlsPolicy.StrictSni,
                EnableAcmeHttp01PassThrough = serverFeatures.TlsPolicy.EnableAcmeHttp01PassThrough && siteFeatures.TlsPolicy.EnableAcmeHttp01PassThrough,
                Acme = siteFeatures.TlsPolicy.Acme.Enabled ? siteFeatures.TlsPolicy.Acme : serverFeatures.TlsPolicy.Acme,
                Mtls = siteFeatures.TlsPolicy.Mtls.ClientCertificateMode != MtlsClientCertificateMode.None
                    ? siteFeatures.TlsPolicy.Mtls
                    : serverFeatures.TlsPolicy.Mtls
            },
            Authentication = new AuthenticationConfig
            {
                AnonymousEnabled = serverFeatures.Authentication.AnonymousEnabled && siteFeatures.Authentication.AnonymousEnabled,
                JwtEnabled = serverFeatures.Authentication.JwtEnabled || siteFeatures.Authentication.JwtEnabled,
                JwtSigningAlgorithm = siteFeatures.Authentication.JwtEnabled ? siteFeatures.Authentication.JwtSigningAlgorithm : serverFeatures.Authentication.JwtSigningAlgorithm,
                JwtIssuer = siteFeatures.Authentication.JwtIssuer ?? serverFeatures.Authentication.JwtIssuer,
                JwtAudience = siteFeatures.Authentication.JwtAudience ?? serverFeatures.Authentication.JwtAudience,
                JwtSigningKey = siteFeatures.Authentication.JwtSigningKey ?? serverFeatures.Authentication.JwtSigningKey,
                ApiKeyEnabled = serverFeatures.Authentication.ApiKeyEnabled || siteFeatures.Authentication.ApiKeyEnabled,
                ApiKeyHeaderName = serverFeatures.Authentication.ApiKeyEnabled ? serverFeatures.Authentication.ApiKeyHeaderName : siteFeatures.Authentication.ApiKeyHeaderName,
                AllowedApiKeys = mergedApiKeys,
                BasicAuthEnabled = serverFeatures.Authentication.BasicAuthEnabled || siteFeatures.Authentication.BasicAuthEnabled,
                BasicAuthRealm = siteFeatures.Authentication.BasicAuthEnabled ? siteFeatures.Authentication.BasicAuthRealm : serverFeatures.Authentication.BasicAuthRealm,
                BasicAuthUsers = mergedBasicUsers
            },
            IpRestrictions = new IpRestrictionsConfig
            {
                Enabled = serverFeatures.IpRestrictions.Enabled || siteFeatures.IpRestrictions.Enabled,
                AllowUnmatched = serverFeatures.IpRestrictions.AllowUnmatched && siteFeatures.IpRestrictions.AllowUnmatched,
                DenyStatusCode = siteFeatures.IpRestrictions.Enabled ? siteFeatures.IpRestrictions.DenyStatusCode : serverFeatures.IpRestrictions.DenyStatusCode,
                Rules = mergedIpRules
            },
            RequestFiltering = new RequestFilteringConfig
            {
                Enabled = serverFeatures.RequestFiltering.Enabled && siteRf.Enabled,
                MaxAllowedContentLength = siteRf.MaxAllowedContentLength != defaultRf.MaxAllowedContentLength
                    ? siteRf.MaxAllowedContentLength
                    : srvRf.MaxAllowedContentLength,
                MaxUrlLength = siteRf.MaxUrlLength != defaultRf.MaxUrlLength ? siteRf.MaxUrlLength : srvRf.MaxUrlLength,
                MaxQueryStringLength = siteRf.MaxQueryStringLength != defaultRf.MaxQueryStringLength ? siteRf.MaxQueryStringLength : srvRf.MaxQueryStringLength,
                AllowHighBitCharacters = siteRf.AllowHighBitCharacters != defaultRf.AllowHighBitCharacters
                    ? siteRf.AllowHighBitCharacters
                    : srvRf.AllowHighBitCharacters,
                AllowDoubleEscaping = siteRf.AllowDoubleEscaping != defaultRf.AllowDoubleEscaping
                    ? siteRf.AllowDoubleEscaping
                    : srvRf.AllowDoubleEscaping,
                AllowedVerbs = !siteRf.AllowedVerbs.SequenceEqual(defaultRf.AllowedVerbs, StringComparer.OrdinalIgnoreCase)
                    ? siteRf.AllowedVerbs
                    : srvRf.AllowedVerbs,
                BlockedExtensions = srvRf.BlockedExtensions.Concat(siteRf.BlockedExtensions).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            },
            FailedRequestTracing = new FailedRequestTracingConfig
            {
                Enabled = serverFeatures.FailedRequestTracing.Enabled || siteFeatures.FailedRequestTracing.Enabled,
                StatusCodeTrigger = siteFeatures.FailedRequestTracing.StatusCodeTrigger != "500-599"
                    ? siteFeatures.FailedRequestTracing.StatusCodeTrigger
                    : serverFeatures.FailedRequestTracing.StatusCodeTrigger,
                LatencyCutoffMilliseconds = Math.Min(serverFeatures.FailedRequestTracing.LatencyCutoffMilliseconds, siteFeatures.FailedRequestTracing.LatencyCutoffMilliseconds),
                MaxRetainedTraces = Math.Max(serverFeatures.FailedRequestTracing.MaxRetainedTraces, siteFeatures.FailedRequestTracing.MaxRetainedTraces)
            },
            EarlyHintsLinks = serverFeatures.EarlyHintsLinks
                .Concat(siteFeatures.EarlyHintsLinks)
                .Distinct(StringComparer.Ordinal)
                .ToList(),
            RateLimiting = siteFeatures.RateLimiting.Enabled ? siteFeatures.RateLimiting : serverFeatures.RateLimiting,
            SpaFallback = siteFeatures.SpaFallback.Enabled
                ? siteFeatures.SpaFallback
                : serverFeatures.SpaFallback,
            EdgeWaf = new EdgeWafConfig
            {
                Enabled = serverFeatures.EdgeWaf.Enabled || siteFeatures.EdgeWaf.Enabled,
                BlockSqlInjection = serverFeatures.EdgeWaf.BlockSqlInjection || siteFeatures.EdgeWaf.BlockSqlInjection,
                BlockXss = serverFeatures.EdgeWaf.BlockXss || siteFeatures.EdgeWaf.BlockXss,
                BlockPathTraversal = serverFeatures.EdgeWaf.BlockPathTraversal || siteFeatures.EdgeWaf.BlockPathTraversal,
                BlockedUserAgents = serverFeatures.EdgeWaf.BlockedUserAgents
                    .Concat(siteFeatures.EdgeWaf.BlockedUserAgents)
                    .Where(static u => !string.IsNullOrWhiteSpace(u))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                CustomBlockedPatterns = serverFeatures.EdgeWaf.CustomBlockedPatterns
                    .Concat(siteFeatures.EdgeWaf.CustomBlockedPatterns)
                    .Where(static p => !string.IsNullOrWhiteSpace(p))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            }
        };

        return merged;
    }

    private static (SearchValues<string>? TargetPatterns, SearchValues<string>? UserAgentPatterns) CompileWafPatterns(EdgeWafConfig waf)
    {
        if (!waf.Enabled)
        {
            return (null, null);
        }

        var patterns = new List<string>();
        if (waf.BlockSqlInjection)
        {
            patterns.AddRange(["union select", "union all select", "' or '1'='1", "\" or \"1\"=\"1", "'; drop table", "-- ", "/*!", "xp_cmdshell", "information_schema"]);
        }

        if (waf.BlockXss)
        {
            patterns.AddRange(["<script", "javascript:", "onerror=", "onload=", "document.cookie", "<iframe"]);
        }

        if (waf.BlockPathTraversal)
        {
            patterns.AddRange(["../", "..\\", "%2e%2e", "/etc/passwd", "win.ini", "boot.ini"]);
        }

        foreach (string custom in waf.CustomBlockedPatterns)
        {
            if (!string.IsNullOrWhiteSpace(custom))
            {
                patterns.Add(custom.Trim());
            }
        }

        SearchValues<string>? targetSearch = patterns.Count > 0
            ? SearchValues.Create(patterns.ToArray(), StringComparison.OrdinalIgnoreCase)
            : null;

        string[] uaPatterns = waf.BlockedUserAgents
            .Where(static u => !string.IsNullOrWhiteSpace(u))
            .Select(static u => u.Trim())
            .ToArray();

        SearchValues<string>? uaSearch = uaPatterns.Length > 0
            ? SearchValues.Create(uaPatterns, StringComparison.OrdinalIgnoreCase)
            : null;

        return (targetSearch, uaSearch);
    }

    private static Regex CompileSafeRegex(string pattern)
    {
        try
        {
            return new Regex(
                pattern,
                RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
                TimeSpan.FromMilliseconds(100));
        }
        catch (NotSupportedException)
        {
            return new Regex(
                pattern,
                RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }
        catch
        {
            return new Regex(
                Regex.Escape(pattern),
                RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }
    }

    public static bool TryParseCidrOrIp(string raw, out IPNetwork network)
    {
        network = default;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string trimmed = raw.Trim();
        if (IPNetwork.TryParse(trimmed, out network))
        {
            return true;
        }

        if (IPAddress.TryParse(trimmed, out IPAddress? addr))
        {
            int prefixLen = addr.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
            network = new IPNetwork(addr, prefixLen);
            return true;
        }

        return false;
    }

    public static IReadOnlyList<StatusCodeRange> ParseStatusCodeTriggers(string? spec)
    {
        var list = new List<StatusCodeRange>();
        if (string.IsNullOrWhiteSpace(spec))
        {
            return list;
        }

        foreach (string part in spec.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int dash = part.IndexOf('-');
            if (dash > 0)
            {
                if (int.TryParse(part[..dash].Trim(), out int min) &&
                    int.TryParse(part[(dash + 1)..].Trim(), out int max) &&
                    min <= max)
                {
                    list.Add(new StatusCodeRange(min, max));
                }
            }
            else if (int.TryParse(part, out int exact))
            {
                list.Add(new StatusCodeRange(exact, exact));
            }
        }

        return list;
    }
}
