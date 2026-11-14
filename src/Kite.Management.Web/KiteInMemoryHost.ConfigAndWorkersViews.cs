using System.Net;
using System.Text;
using System.Text.Json;
using Kite.Core.Models;
using Kite.Core.Routing;
using Kite.Core.Serialization;
using Kite.Management.Core;
using Kite.Management.Core.Importers;

namespace Kite.Management.Web;

public sealed partial class KiteInMemoryHost
{
    private async ValueTask<(string Scope, string? Entity, string? Tab, string? Flash)> HandleFeatureAndWorkerPostAsync(
        string path,
        Dictionary<string, string> form,
        string defaultScope,
        string? defaultEntity,
        string? defaultTab,
        CancellationToken ct)
    {
        string? targetSiteId = form.GetValueOrDefault("targetSiteId");
        if (string.IsNullOrWhiteSpace(targetSiteId))
        {
            targetSiteId = null;
        }

        string returnTab = targetSiteId is null ? "settings" : "settings";
        string returnScope = targetSiteId is null ? "config" : "config";

        switch (path.ToLowerInvariant())
        {
            case "/actions/feature-inheritance":
            {
                if (targetSiteId is not null)
                {
                    bool inherit = string.Equals(form.GetValueOrDefault("inheritFromServer"), "true", StringComparison.OrdinalIgnoreCase);
                    await _managementService.SetSiteInheritanceAsync(targetSiteId, inherit, ct).ConfigureAwait(false);
                    return ("config", targetSiteId, null, inherit
                        ? $"Site '{targetSiteId}' now inherits Server Level defaults."
                        : $"Site '{targetSiteId}' now uses independent site-level overrides.");
                }
                return (defaultScope, defaultEntity, defaultTab, null);
            }

            case "/actions/feature-rewrite":
            {
                bool enabled = string.Equals(form.GetValueOrDefault("rewriteEnabled", "true"), "true", StringComparison.OrdinalIgnoreCase);
                string ruleName = form.GetValueOrDefault("ruleName", "CustomRule").Trim();
                string rulePattern = form.GetValueOrDefault("rulePattern", "").Trim();
                string ruleTarget = form.GetValueOrDefault("ruleTarget", "/{R:1}").Trim();
                string actionStr = form.GetValueOrDefault("ruleActionType", "Rewrite");
                bool requireHttps = string.Equals(form.GetValueOrDefault("requireHttps"), "true", StringComparison.OrdinalIgnoreCase);
                bool replaceExisting = string.Equals(form.GetValueOrDefault("replaceExisting"), "true", StringComparison.OrdinalIgnoreCase);
                bool clearAll = string.Equals(form.GetValueOrDefault("clearAll"), "true", StringComparison.OrdinalIgnoreCase);

                if (!Enum.TryParse<UrlRewriteActionType>(actionStr, ignoreCase: true, out UrlRewriteActionType actionType))
                {
                    actionType = UrlRewriteActionType.Rewrite;
                }

                await _managementService.MutateFeaturesAsync(targetSiteId, f =>
                {
                    f.UrlRewrite.Enabled = enabled;
                    if (clearAll)
                    {
                        f.UrlRewrite.Rules.Clear();
                    }
                    else if (!string.IsNullOrWhiteSpace(rulePattern))
                    {
                        if (replaceExisting)
                        {
                            f.UrlRewrite.Rules.RemoveAll(r => string.Equals(r.Name, ruleName, StringComparison.OrdinalIgnoreCase));
                        }
                        f.UrlRewrite.Rules.Add(new UrlRewriteRuleConfig
                        {
                            Name = string.IsNullOrWhiteSpace(ruleName) ? $"Rule-{f.UrlRewrite.Rules.Count + 1}" : ruleName,
                            Enabled = true,
                            Pattern = rulePattern,
                            RequireHttps = requireHttps,
                            ActionType = actionType,
                            Target = ruleTarget,
                            StopProcessing = true
                        });
                    }
                }, ct).ConfigureAwait(false);

                return (returnScope, targetSiteId, returnTab, $"Updated URL Rewrite rules ({(targetSiteId ?? "Server Level")}).");
            }

            case "/actions/feature-redirects":
            {
                string defaultDocsRaw = form.GetValueOrDefault("defaultDocuments", "index.html, default.html");
                bool dirBrowsing = string.Equals(form.GetValueOrDefault("directoryBrowsing"), "true", StringComparison.OrdinalIgnoreCase);
                bool enforceSlash = string.Equals(form.GetValueOrDefault("enforceTrailingSlash"), "true", StringComparison.OrdinalIgnoreCase);
                string globalRedir = form.GetValueOrDefault("globalRedirectUrl", "").Trim();
                string errorModeStr = form.GetValueOrDefault("errorMode", "PassThrough");
                string customPagesRaw = form.GetValueOrDefault("customErrorPages", "");

                if (!Enum.TryParse<CustomErrorMode>(errorModeStr, ignoreCase: true, out CustomErrorMode errorMode))
                {
                    errorMode = CustomErrorMode.PassThrough;
                }

                await _managementService.MutateFeaturesAsync(targetSiteId, f =>
                {
                    List<string> docs = defaultDocsRaw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    if (docs.Count > 0)
                    {
                        f.DefaultDocuments = docs;
                    }
                    f.DirectoryBrowsing = dirBrowsing;
                    f.RedirectsAndErrors.EnforceTrailingSlash = enforceSlash;
                    f.RedirectsAndErrors.GlobalRedirectUrl = string.IsNullOrWhiteSpace(globalRedir) ? null : globalRedir;
                    f.RedirectsAndErrors.ErrorMode = errorMode;
                    f.RedirectsAndErrors.CustomErrorPages.Clear();
                    foreach (string entry in customPagesRaw.Split(['\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        int eq = entry.IndexOf('=');
                        if (eq > 0 && int.TryParse(entry[..eq].Trim(), out int code))
                        {
                            f.RedirectsAndErrors.CustomErrorPages[code] = entry[(eq + 1)..].Trim();
                        }
                    }
                }, ct).ConfigureAwait(false);

                return (returnScope, targetSiteId, returnTab, $"Updated Redirect & Default Doc settings ({(targetSiteId ?? "Server Level")}).");
            }

            case "/actions/feature-mime":
            {
                string fallback = form.GetValueOrDefault("fallbackContentType", "application/octet-stream").Trim();
                string ext = form.GetValueOrDefault("extension", "").Trim();
                string mime = form.GetValueOrDefault("mimeType", "").Trim();

                await _managementService.MutateFeaturesAsync(targetSiteId, f =>
                {
                    if (!string.IsNullOrWhiteSpace(fallback))
                    {
                        f.MimeMappings.FallbackContentType = fallback;
                    }
                    if (!string.IsNullOrWhiteSpace(ext) && !string.IsNullOrWhiteSpace(mime))
                    {
                        string normalizedExt = ext.StartsWith('.') ? ext : "." + ext;
                        f.MimeMappings.CustomMappings[normalizedExt] = mime;
                    }
                }, ct).ConfigureAwait(false);

                return (returnScope, targetSiteId, returnTab, $"Updated MIME mappings ({(targetSiteId ?? "Server Level")}).");
            }

            case "/actions/feature-headers":
            {
                bool stripServer = string.Equals(form.GetValueOrDefault("stripServerHeader", "true"), "true", StringComparison.OrdinalIgnoreCase);
                bool hstsEnabled = string.Equals(form.GetValueOrDefault("hstsEnabled"), "true", StringComparison.OrdinalIgnoreCase);
                int hstsMaxAge = int.TryParse(form.GetValueOrDefault("hstsMaxAgeSeconds"), out int hm) ? hm : 31_536_000;
                bool corsEnabled = string.Equals(form.GetValueOrDefault("corsEnabled"), "true", StringComparison.OrdinalIgnoreCase);
                string corsOrigins = form.GetValueOrDefault("corsAllowedOrigins", "*");
                string corsMethods = form.GetValueOrDefault("corsAllowedMethods", "GET, POST, PUT, DELETE, OPTIONS");
                string customHeadersRaw = form.GetValueOrDefault("customHeadersText", "");
                string removeHeadersRaw = form.GetValueOrDefault("removeHeaders", "X-Powered-By");

                await _managementService.MutateFeaturesAsync(targetSiteId, f =>
                {
                    f.StripServerHeader = stripServer;
                    f.Hsts.Enabled = hstsEnabled;
                    f.Hsts.MaxAgeSeconds = Math.Max(0, hstsMaxAge);
                    f.Cors.Enabled = corsEnabled;
                    f.Cors.AllowedOrigins = corsOrigins.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    f.Cors.AllowedMethods = string.IsNullOrWhiteSpace(corsMethods) ? "GET, POST, PUT, DELETE, OPTIONS" : corsMethods.Trim();
                    f.RemoveHeaders = removeHeadersRaw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    f.CustomHeaders.Clear();
                    foreach (string line in customHeadersRaw.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        int sep = line.IndexOf(':');
                        if (sep <= 0) sep = line.IndexOf('=');
                        if (sep > 0)
                        {
                            string k = line[..sep].Trim();
                            string v = line[(sep + 1)..].Trim();
                            if (!string.IsNullOrEmpty(k))
                            {
                                f.CustomHeaders[k] = v;
                            }
                        }
                    }
                }, ct).ConfigureAwait(false);

                return (returnScope, targetSiteId, returnTab, $"Updated Response Headers & CORS ({(targetSiteId ?? "Server Level")}).");
            }

            case "/actions/feature-tls":
            {
                bool enforceTls13 = string.Equals(form.GetValueOrDefault("enforceTls13"), "true", StringComparison.OrdinalIgnoreCase);
                bool strictSni = string.Equals(form.GetValueOrDefault("strictSni", "true"), "true", StringComparison.OrdinalIgnoreCase);
                bool acmePass = string.Equals(form.GetValueOrDefault("enableAcmeHttp01", "true"), "true", StringComparison.OrdinalIgnoreCase);

                await _managementService.MutateFeaturesAsync(targetSiteId, f =>
                {
                    f.TlsPolicy.EnforceTls13 = enforceTls13;
                    f.TlsPolicy.StrictSni = strictSni;
                    f.TlsPolicy.EnableAcmeHttp01PassThrough = acmePass;
                }, ct).ConfigureAwait(false);

                return (returnScope, targetSiteId, returnTab, $"Updated TLS & SNI Cryptography policy ({(targetSiteId ?? "Server Level")}).");
            }

            case "/actions/feature-auth":
            {
                bool anon = string.Equals(form.GetValueOrDefault("anonymousEnabled", "true"), "true", StringComparison.OrdinalIgnoreCase);
                bool jwtEnabled = string.Equals(form.GetValueOrDefault("jwtEnabled"), "true", StringComparison.OrdinalIgnoreCase);
                string jwtAlg = form.GetValueOrDefault("jwtAlgorithm", "HS256").Trim();
                string jwtIssuer = form.GetValueOrDefault("jwtIssuer", "").Trim();
                string jwtAudience = form.GetValueOrDefault("jwtAudience", "").Trim();
                string jwtKey = form.GetValueOrDefault("jwtSigningKey", "").Trim();
                bool apiKeyEnabled = string.Equals(form.GetValueOrDefault("apiKeyEnabled"), "true", StringComparison.OrdinalIgnoreCase);
                string apiKeyHeader = form.GetValueOrDefault("apiKeyHeaderName", "X-API-Key").Trim();
                string allowedKeysRaw = form.GetValueOrDefault("allowedApiKeys", "");
                bool basicEnabled = string.Equals(form.GetValueOrDefault("basicAuthEnabled"), "true", StringComparison.OrdinalIgnoreCase);
                string basicRealm = form.GetValueOrDefault("basicAuthRealm", "Kite Restricted").Trim();
                string basicUsersRaw = form.GetValueOrDefault("basicAuthUsers", "");
                bool fwdEnabled = string.Equals(form.GetValueOrDefault("forwardAuthEnabled"), "true", StringComparison.OrdinalIgnoreCase);
                string fwdEndpoint = form.GetValueOrDefault("forwardAuthEndpointUrl", "").Trim();
                bool fwdFailOpen = string.Equals(form.GetValueOrDefault("forwardAuthFailOpen"), "true", StringComparison.OrdinalIgnoreCase);
                string fwdCopyRespRaw = form.GetValueOrDefault("forwardAuthCopyResponseHeaders", "X-Auth-User, X-Auth-Roles, X-Auth-Tenant");

                await _managementService.MutateFeaturesAsync(targetSiteId, f =>
                {
                    f.Authentication.AnonymousEnabled = anon;
                    f.Authentication.JwtEnabled = jwtEnabled;
                    f.Authentication.JwtSigningAlgorithm = string.IsNullOrWhiteSpace(jwtAlg) ? "HS256" : jwtAlg;
                    f.Authentication.JwtIssuer = string.IsNullOrWhiteSpace(jwtIssuer) ? null : jwtIssuer;
                    f.Authentication.JwtAudience = string.IsNullOrWhiteSpace(jwtAudience) ? null : jwtAudience;
                    if (!string.IsNullOrWhiteSpace(jwtKey))
                    {
                        f.Authentication.JwtSigningKey = jwtKey;
                    }
                    f.Authentication.ApiKeyEnabled = apiKeyEnabled;
                    f.Authentication.ApiKeyHeaderName = string.IsNullOrWhiteSpace(apiKeyHeader) ? "X-API-Key" : apiKeyHeader;
                    f.Authentication.AllowedApiKeys = allowedKeysRaw.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    f.Authentication.BasicAuthEnabled = basicEnabled;
                    f.Authentication.BasicAuthRealm = string.IsNullOrWhiteSpace(basicRealm) ? "Kite Restricted" : basicRealm;
                    if (!string.IsNullOrWhiteSpace(basicUsersRaw))
                    {
                        f.Authentication.BasicAuthUsers.Clear();
                        foreach (string line in basicUsersRaw.Split(['\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        {
                            int eq = line.IndexOf('=');
                            if (eq <= 0) eq = line.IndexOf(':');
                            if (eq > 0)
                            {
                                f.Authentication.BasicAuthUsers[line[..eq].Trim()] = line[(eq + 1)..].Trim();
                            }
                        }
                    }

                    f.ForwardAuth.Enabled = fwdEnabled;
                    f.ForwardAuth.EndpointUrl = fwdEndpoint;
                    f.ForwardAuth.FailOpen = fwdFailOpen;
                    f.ForwardAuth.CopyResponseHeaders = SplitFormList(fwdCopyRespRaw);
                }, ct).ConfigureAwait(false);

                return (returnScope, targetSiteId, returnTab, $"Updated Edge Authentication & ForwardAuth ({(targetSiteId ?? "Server Level")}).");
            }

            case "/actions/feature-ip":
            {
                bool ipEnabled = string.Equals(form.GetValueOrDefault("ipEnabled"), "true", StringComparison.OrdinalIgnoreCase);
                bool allowUnmatched = string.Equals(form.GetValueOrDefault("allowUnmatched", "true"), "true", StringComparison.OrdinalIgnoreCase);
                int denyStatus = int.TryParse(form.GetValueOrDefault("denyStatusCode"), out int ds) ? ds : 403;
                string cidr = form.GetValueOrDefault("cidr", "").Trim();
                bool ruleAllow = string.Equals(form.GetValueOrDefault("ruleAllow", "true"), "true", StringComparison.OrdinalIgnoreCase);
                string desc = form.GetValueOrDefault("description", "Custom CIDR").Trim();
                bool clearRules = string.Equals(form.GetValueOrDefault("clearRules"), "true", StringComparison.OrdinalIgnoreCase);

                await _managementService.MutateFeaturesAsync(targetSiteId, f =>
                {
                    f.IpRestrictions.Enabled = ipEnabled;
                    f.IpRestrictions.AllowUnmatched = allowUnmatched;
                    f.IpRestrictions.DenyStatusCode = Math.Clamp(denyStatus, 400, 599);
                    if (clearRules)
                    {
                        f.IpRestrictions.Rules.Clear();
                    }
                    else if (!string.IsNullOrWhiteSpace(cidr))
                    {
                        f.IpRestrictions.Rules.RemoveAll(r => string.Equals(r.Cidr, cidr, StringComparison.OrdinalIgnoreCase));
                        f.IpRestrictions.Rules.Add(new IpSubnetRuleConfig
                        {
                            Cidr = cidr,
                            Allow = ruleAllow,
                            Description = string.IsNullOrWhiteSpace(desc) ? cidr : desc
                        });
                    }
                }, ct).ConfigureAwait(false);

                return (returnScope, targetSiteId, returnTab, $"Updated IP & Subnet Filtering rules ({(targetSiteId ?? "Server Level")}).");
            }

            case "/actions/feature-filtering":
            {
                bool enabled = string.Equals(form.GetValueOrDefault("filteringEnabled", "true"), "true", StringComparison.OrdinalIgnoreCase);
                long maxContent = long.TryParse(form.GetValueOrDefault("maxAllowedContentLength"), out long mc) ? mc : 31_457_280;
                int maxUrl = int.TryParse(form.GetValueOrDefault("maxUrlLength"), out int mu) ? mu : 4096;
                int maxQuery = int.TryParse(form.GetValueOrDefault("maxQueryStringLength"), out int mq) ? mq : 2048;
                bool allowHighBit = string.Equals(form.GetValueOrDefault("allowHighBitCharacters", "true"), "true", StringComparison.OrdinalIgnoreCase);
                bool allowDoubleEsc = string.Equals(form.GetValueOrDefault("allowDoubleEscaping"), "true", StringComparison.OrdinalIgnoreCase);
                string verbsRaw = form.GetValueOrDefault("allowedVerbs", "GET, POST, PUT, DELETE, HEAD, OPTIONS, PATCH");
                string blockedExtRaw = form.GetValueOrDefault("blockedExtensions", ".config, .env, .pfx, .key, .pdb");

                await _managementService.MutateFeaturesAsync(targetSiteId, f =>
                {
                    f.RequestFiltering.Enabled = enabled;
                    f.RequestFiltering.MaxAllowedContentLength = Math.Max(0, maxContent);
                    f.RequestFiltering.MaxUrlLength = Math.Max(64, maxUrl);
                    f.RequestFiltering.MaxQueryStringLength = Math.Max(0, maxQuery);
                    f.RequestFiltering.AllowHighBitCharacters = allowHighBit;
                    f.RequestFiltering.AllowDoubleEscaping = allowDoubleEsc;
                    f.RequestFiltering.AllowedVerbs = verbsRaw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    f.RequestFiltering.BlockedExtensions = blockedExtRaw.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                }, ct).ConfigureAwait(false);

                return (returnScope, targetSiteId, returnTab, $"Updated Request Filtering security gate ({(targetSiteId ?? "Server Level")}).");
            }

            case "/actions/feature-cache-flush":
            {
                bool triggerFlush = string.Equals(form.GetValueOrDefault("triggerFlush"), "true", StringComparison.OrdinalIgnoreCase);
                if (!triggerFlush && form.ContainsKey("cachingEnabled"))
                {
                    bool enabled = string.Equals(form.GetValueOrDefault("cachingEnabled", "true"), "true", StringComparison.OrdinalIgnoreCase);
                    int ttl = int.TryParse(form.GetValueOrDefault("defaultMaxAgeSeconds"), out int t) ? t : 3600;
                    int maxItemBytes = int.TryParse(form.GetValueOrDefault("maxCacheBodyBytes"), out int mb) ? mb : 4 * 1024 * 1024;
                    int maxTotalMb = int.TryParse(form.GetValueOrDefault("maxCacheSizeMegabytes"), out int mm) ? mm : 500;
                    await _managementService.MutateFeaturesAsync(targetSiteId, f =>
                    {
                        f.Caching.Enabled = enabled;
                        f.Caching.DefaultMaxAgeSeconds = Math.Max(0, ttl);
                        f.Caching.MaxCacheBodyBytes = Math.Max(1024, maxItemBytes);
                        f.Caching.MaxCacheSizeMegabytes = Math.Max(1, maxTotalMb);
                    }, ct).ConfigureAwait(false);
                    return (returnScope, targetSiteId, returnTab, $"Updated Output Cache policy ({(targetSiteId ?? "Server Level")}).");
                }

                long gen = await _managementService.FlushOutputCacheAsync(targetSiteId, ct).ConfigureAwait(false);
                return (returnScope, targetSiteId, returnTab, $"Flushed Output Cache (FlushGeneration={gen}).");
            }

            case "/actions/feature-freb":
            {
                bool enabled = string.Equals(form.GetValueOrDefault("frebEnabled", "true"), "true", StringComparison.OrdinalIgnoreCase);
                string statusTrigger = form.GetValueOrDefault("statusCodeTrigger", "500-599").Trim();
                int latencyCutoff = int.TryParse(form.GetValueOrDefault("latencyCutoffMilliseconds"), out int lc) ? lc : 3000;
                int maxRetained = int.TryParse(form.GetValueOrDefault("maxRetainedTraces"), out int mr) ? mr : 50;

                await _managementService.MutateFeaturesAsync(targetSiteId, f =>
                {
                    f.FailedRequestTracing.Enabled = enabled;
                    f.FailedRequestTracing.StatusCodeTrigger = string.IsNullOrWhiteSpace(statusTrigger) ? "500-599" : statusTrigger;
                    f.FailedRequestTracing.LatencyCutoffMilliseconds = Math.Max(0, latencyCutoff);
                    f.FailedRequestTracing.MaxRetainedTraces = Math.Clamp(maxRetained, 5, 500);
                }, ct).ConfigureAwait(false);

                return (returnScope, targetSiteId, returnTab, $"Updated FREB Tracing triggers ({(targetSiteId ?? "Server Level")}).");
            }

            case "/actions/feature-compression":
            {
                bool enabled = string.Equals(form.GetValueOrDefault("compressionEnabled", "true"), "true", StringComparison.OrdinalIgnoreCase);
                int minBytes = int.TryParse(form.GetValueOrDefault("minSizeBytes"), out int mb) ? mb : 128;
                int brotli = int.TryParse(form.GetValueOrDefault("brotliLevel"), out int bl) ? bl : 5;
                int gzip = int.TryParse(form.GetValueOrDefault("gzipLevel"), out int gl) ? gl : 7;
                int zstd = int.TryParse(form.GetValueOrDefault("zstdLevel"), out int zl) ? zl : 3;

                await _managementService.MutateFeaturesAsync(targetSiteId, f =>
                {
                    f.Compression.Enabled = enabled;
                    f.Compression.MinSizeBytes = Math.Max(0, minBytes);
                    f.Compression.BrotliLevel = Math.Clamp(brotli, 0, 11);
                    f.Compression.GzipLevel = Math.Clamp(gzip, 0, 9);
                    f.Compression.ZstdLevel = Math.Clamp(zstd, 0, 19);
                }, ct).ConfigureAwait(false);

                return (returnScope, targetSiteId, returnTab, $"Updated Compression schemes ({(targetSiteId ?? "Server Level")}).");
            }

            case "/actions/feature-ratelimit":
            {
                bool enabled = string.Equals(form.GetValueOrDefault("rateLimitEnabled"), "true", StringComparison.OrdinalIgnoreCase);
                string algRaw = form.GetValueOrDefault("algorithm", "SlidingWindow");
                int permits = int.TryParse(form.GetValueOrDefault("permitLimit"), out int pl) ? pl : 100;
                int windowSec = int.TryParse(form.GetValueOrDefault("windowSeconds"), out int ws) ? ws : 10;
                int retryAfter = int.TryParse(form.GetValueOrDefault("retryAfterSeconds"), out int ra) ? ra : 10;
                string partitionHeader = form.GetValueOrDefault("partitionByHeader", "").Trim();
                if (!Enum.TryParse<RateLimitAlgorithm>(algRaw, ignoreCase: true, out RateLimitAlgorithm alg))
                {
                    alg = RateLimitAlgorithm.SlidingWindow;
                }

                await _managementService.MutateFeaturesAsync(targetSiteId, f =>
                {
                    f.RateLimiting.Enabled = enabled;
                    f.RateLimiting.Algorithm = alg;
                    f.RateLimiting.PermitLimit = Math.Max(1, permits);
                    f.RateLimiting.WindowSeconds = Math.Max(1, windowSec);
                    f.RateLimiting.RetryAfterSeconds = Math.Max(1, retryAfter);
                    f.RateLimiting.PartitionByHeader = partitionHeader;
                }, ct).ConfigureAwait(false);

                return (returnScope, targetSiteId, returnTab, $"Updated Edge Rate Limiting ({(targetSiteId ?? "Server Level")}).");
            }

            case "/actions/feature-spawaf":
            {
                bool spaEnabled = string.Equals(form.GetValueOrDefault("spaEnabled"), "true", StringComparison.OrdinalIgnoreCase);
                string fallbackDoc = form.GetValueOrDefault("fallbackDocument", "/index.html").Trim();
                string excludePrefixesRaw = form.GetValueOrDefault("excludePrefixes", "/api, /__kite, /.well-known");
                bool wafEnabled = string.Equals(form.GetValueOrDefault("wafEnabled"), "true", StringComparison.OrdinalIgnoreCase);
                bool sqli = string.Equals(form.GetValueOrDefault("blockSqlInjection", "true"), "true", StringComparison.OrdinalIgnoreCase);
                bool xss = string.Equals(form.GetValueOrDefault("blockXss", "true"), "true", StringComparison.OrdinalIgnoreCase);
                bool traversal = string.Equals(form.GetValueOrDefault("blockPathTraversal", "true"), "true", StringComparison.OrdinalIgnoreCase);
                bool inspectBody = string.Equals(form.GetValueOrDefault("inspectRequestBody"), "true", StringComparison.OrdinalIgnoreCase);
                int maxBodyBytes = int.TryParse(form.GetValueOrDefault("maxBodyInspectBytes"), out int mbb) ? mbb : 65536;
                int maxJsonDepth = int.TryParse(form.GetValueOrDefault("maxJsonDepth"), out int mjd) ? mjd : 32;
                string blockedUaRaw = form.GetValueOrDefault("blockedUserAgents", "");
                string customPatternsRaw = form.GetValueOrDefault("customBlockedPatterns", "");

                await _managementService.MutateFeaturesAsync(targetSiteId, f =>
                {
                    f.SpaFallback.Enabled = spaEnabled;
                    f.SpaFallback.FallbackDocument = string.IsNullOrWhiteSpace(fallbackDoc) ? "/index.html" : fallbackDoc;
                    f.SpaFallback.ExcludePrefixes = SplitFormList(excludePrefixesRaw);
                    f.EdgeWaf.Enabled = wafEnabled;
                    f.EdgeWaf.BlockSqlInjection = sqli;
                    f.EdgeWaf.BlockXss = xss;
                    f.EdgeWaf.BlockPathTraversal = traversal;
                    f.EdgeWaf.InspectRequestBody = inspectBody;
                    f.EdgeWaf.MaxBodyInspectBytes = Math.Clamp(maxBodyBytes, 1024, 10 * 1024 * 1024);
                    f.EdgeWaf.MaxJsonDepth = Math.Clamp(maxJsonDepth, 0, 256);
                    f.EdgeWaf.BlockedUserAgents = SplitFormList(blockedUaRaw);
                    f.EdgeWaf.CustomBlockedPatterns = SplitFormList(customPatternsRaw);
                }, ct).ConfigureAwait(false);

                return (returnScope, targetSiteId, returnTab, $"Updated SPA Fallback & Edge WAF ({(targetSiteId ?? "Server Level")}).");
            }

            case "/actions/feature-acme-mtls":
            {
                bool acmeEnabled = string.Equals(form.GetValueOrDefault("acmeEnabled"), "true", StringComparison.OrdinalIgnoreCase);
                bool acceptTos = string.Equals(form.GetValueOrDefault("acceptTermsOfService"), "true", StringComparison.OrdinalIgnoreCase);
                string directoryUrl = form.GetValueOrDefault("directoryUrl", "https://acme-v02.api.letsencrypt.org/directory").Trim();
                int renewBeforeDays = int.TryParse(form.GetValueOrDefault("renewBeforeDays"), out int rbd) ? rbd : 30;
                string email = form.GetValueOrDefault("contactEmail", "").Trim();
                string domainsRaw = form.GetValueOrDefault("acmeDomains", "");
                string chRaw = form.GetValueOrDefault("challengeType", "Http01");
                string dnsWebhookUrl = form.GetValueOrDefault("dnsWebhookUrl", "").Trim();
                string dnsWebhookToken = form.GetValueOrDefault("dnsWebhookAuthToken", "").Trim();
                bool redirectHttps = string.Equals(form.GetValueOrDefault("redirectHttpToHttps"), "true", StringComparison.OrdinalIgnoreCase);
                int redirectCode = int.TryParse(form.GetValueOrDefault("redirectHttpToHttpsStatusCode"), out int rc) ? rc : 308;
                bool onDemandEnabled = string.Equals(form.GetValueOrDefault("onDemandTlsEnabled"), "true", StringComparison.OrdinalIgnoreCase);
                string allowedSuffixesRaw = form.GetValueOrDefault("allowedHostSuffixes", "");
                string mtlsRaw = form.GetValueOrDefault("mtlsMode", "None");
                string thumbsRaw = form.GetValueOrDefault("trustedThumbprints", "");
                string hintsRaw = form.GetValueOrDefault("earlyHintsLinks", "");

                if (!Enum.TryParse<AcmeChallengeType>(chRaw, ignoreCase: true, out AcmeChallengeType challengeType))
                {
                    challengeType = AcmeChallengeType.Http01;
                }
                if (!Enum.TryParse<MtlsClientCertificateMode>(mtlsRaw, ignoreCase: true, out MtlsClientCertificateMode mtlsMode))
                {
                    mtlsMode = MtlsClientCertificateMode.None;
                }

                await _managementService.MutateFeaturesAsync(targetSiteId, f =>
                {
                    f.TlsPolicy.RedirectHttpToHttps = redirectHttps;
                    f.TlsPolicy.RedirectHttpToHttpsStatusCode = redirectCode is 301 or 302 or 307 or 308 ? redirectCode : 308;
                    f.TlsPolicy.OnDemandTls.Enabled = onDemandEnabled;
                    f.TlsPolicy.OnDemandTls.AllowedHostSuffixes = SplitFormList(allowedSuffixesRaw);
                    f.TlsPolicy.Acme.Enabled = acmeEnabled;
                    f.TlsPolicy.Acme.AcceptTermsOfService = acceptTos;
                    f.TlsPolicy.Acme.DirectoryUrl = string.IsNullOrWhiteSpace(directoryUrl)
                        ? "https://acme-v02.api.letsencrypt.org/directory"
                        : directoryUrl;
                    f.TlsPolicy.Acme.RenewBeforeDays = Math.Clamp(renewBeforeDays, 1, 89);
                    f.TlsPolicy.Acme.ContactEmail = email;
                    f.TlsPolicy.Acme.ChallengeType = challengeType;
                    f.TlsPolicy.Acme.DnsWebhookUrl = string.IsNullOrWhiteSpace(dnsWebhookUrl) ? null : dnsWebhookUrl;
                    f.TlsPolicy.Acme.DnsWebhookAuthToken = string.IsNullOrWhiteSpace(dnsWebhookToken) ? null : dnsWebhookToken;
                    f.TlsPolicy.Acme.Domains = SplitFormList(domainsRaw);
                    f.TlsPolicy.Mtls.ClientCertificateMode = mtlsMode;
                    f.TlsPolicy.Mtls.TrustedThumbprints = SplitFormList(thumbsRaw);
                    f.EarlyHintsLinks = hintsRaw.Split(['\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                }, ct).ConfigureAwait(false);

                return (returnScope, targetSiteId, returnTab, $"Updated Auto-HTTPS ACME, Redirect, On-Demand TLS & mTLS ({(targetSiteId ?? "Server Level")}).");
            }

            case "/actions/feature-transport-obs":
            {
                bool http3 = string.Equals(form.GetValueOrDefault("enableHttp3"), "true", StringComparison.OrdinalIgnoreCase);
                bool quicZeroRtt = string.Equals(form.GetValueOrDefault("enableQuicZeroRtt"), "true", StringComparison.OrdinalIgnoreCase);
                int drainSec = int.TryParse(form.GetValueOrDefault("gracefulDrainTimeoutSeconds"), out int ds) ? ds : 15;
                double minRate = double.TryParse(form.GetValueOrDefault("minRequestBodyDataRateBytesPerSecond"), System.Globalization.CultureInfo.InvariantCulture, out double mr) ? mr : 240.0;
                bool otel = string.Equals(form.GetValueOrDefault("enableOpenTelemetryTracing", "true"), "true", StringComparison.OrdinalIgnoreCase);
                bool prom = string.Equals(form.GetValueOrDefault("enablePrometheusMetrics", "true"), "true", StringComparison.OrdinalIgnoreCase);
                bool healthz = string.Equals(form.GetValueOrDefault("enableHealthEndpoints", "true"), "true", StringComparison.OrdinalIgnoreCase);
                bool otlpEnabled = string.Equals(form.GetValueOrDefault("otlpEnabled"), "true", StringComparison.OrdinalIgnoreCase);
                string otlpUrl = form.GetValueOrDefault("otlpEndpointUrl", "http://127.0.0.1:4318/v1/traces").Trim();
                string otlpService = form.GetValueOrDefault("otlpServiceName", "Kite.Server").Trim();
                bool redisEnabled = string.Equals(form.GetValueOrDefault("redisEnabled"), "true", StringComparison.OrdinalIgnoreCase);
                string redisHost = form.GetValueOrDefault("redisHost", "127.0.0.1").Trim();
                int redisPort = int.TryParse(form.GetValueOrDefault("redisPort"), out int rp) ? rp : 6379;
                string logFmtRaw = form.GetValueOrDefault("accessLogFormat", "W3C");
                if (!Enum.TryParse<AccessLogFormat>(logFmtRaw, ignoreCase: true, out AccessLogFormat logFmt))
                {
                    logFmt = AccessLogFormat.W3C;
                }

                await _managementService.MutateServerConfigAsync(srv =>
                {
                    srv.Transport.EnableHttp3 = http3;
                    srv.Transport.EnableQuicZeroRtt = quicZeroRtt;
                    srv.Transport.GracefulDrainTimeoutSeconds = Math.Clamp(drainSec, 1, 120);
                    srv.Transport.MinRequestBodyDataRateBytesPerSecond = Math.Max(0, minRate);
                    srv.Observability.EnableOpenTelemetryTracing = otel;
                    srv.Observability.EnablePrometheusMetrics = prom;
                    srv.Observability.EnableHealthEndpoints = healthz;
                    srv.Observability.AccessLogFormat = logFmt;
                    srv.Observability.Otlp.Enabled = otlpEnabled;
                    srv.Observability.Otlp.EndpointUrl = string.IsNullOrWhiteSpace(otlpUrl) ? "http://127.0.0.1:4318/v1/traces" : otlpUrl;
                    srv.Observability.Otlp.ServiceName = string.IsNullOrWhiteSpace(otlpService) ? "Kite.Server" : otlpService;
                    srv.DistributedState.Enabled = redisEnabled;
                    srv.DistributedState.RedisHost = string.IsNullOrWhiteSpace(redisHost) ? "127.0.0.1" : redisHost;
                    srv.DistributedState.RedisPort = Math.Clamp(redisPort, 1, 65535);
                }, ct).ConfigureAwait(false);

                return (returnScope, targetSiteId, returnTab, "Updated HTTP/3 QUIC, OTLP Exporter, Redis State & Observability.");
            }

            case "/actions/feature-upstreams":
            {
                string clusterId = form.GetValueOrDefault("clusterId", "backend-cluster").Trim();
                string lbRaw = form.GetValueOrDefault("loadBalancingPolicy", "PowerOfTwoChoices");
                string addressesRaw = form.GetValueOrDefault("destinationAddresses", "");
                bool activeHc = string.Equals(form.GetValueOrDefault("activeHealthCheckEnabled", "true"), "true", StringComparison.OrdinalIgnoreCase);
                string hcPath = form.GetValueOrDefault("activeHealthCheckPath", "/healthz").Trim();
                int hcIntervalSec = int.TryParse(form.GetValueOrDefault("activeHealthCheckIntervalSeconds"), out int hci) ? hci : 10;
                int retryCount = int.TryParse(form.GetValueOrDefault("retryCount"), out int rc) ? rc : 2;
                int reqTimeoutSec = int.TryParse(form.GetValueOrDefault("requestTimeoutSeconds"), out int rts) ? rts : 30;
                string stickyHeader = form.GetValueOrDefault("stickyHeaderOrCookie", "").Trim();
                int failureThreshold = int.TryParse(form.GetValueOrDefault("passiveFailureThreshold"), out int pft) ? pft : 5;
                int ejectionSec = int.TryParse(form.GetValueOrDefault("passiveEjectionSeconds"), out int pes) ? pes : 30;
                bool removeCluster = string.Equals(form.GetValueOrDefault("removeCluster"), "true", StringComparison.OrdinalIgnoreCase);

                if (!Enum.TryParse<LoadBalancingPolicy>(lbRaw, ignoreCase: true, out LoadBalancingPolicy lbPolicy))
                {
                    lbPolicy = LoadBalancingPolicy.PowerOfTwoChoices;
                }

                await _managementService.MutateServerConfigAsync(srv =>
                {
                    if (removeCluster)
                    {
                        srv.Upstreams.RemoveAll(u => string.Equals(u.Id, clusterId, StringComparison.OrdinalIgnoreCase));
                        return;
                    }

                    List<string> addrs = SplitFormList(addressesRaw);
                    UpstreamClusterConfig? existing = srv.Upstreams.FirstOrDefault(u => string.Equals(u.Id, clusterId, StringComparison.OrdinalIgnoreCase));
                    if (existing is null)
                    {
                        existing = new UpstreamClusterConfig { Id = clusterId };
                        srv.Upstreams.Add(existing);
                    }

                    existing.LoadBalancingPolicy = lbPolicy;
                    existing.RetryCount = Math.Clamp(retryCount, 0, 5);
                    existing.RequestTimeoutSeconds = Math.Clamp(reqTimeoutSec, 1, 600);
                    existing.StickyHeaderOrCookie = string.IsNullOrWhiteSpace(stickyHeader) ? null : stickyHeader;
                    existing.ActiveHealthCheck.Enabled = activeHc;
                    existing.ActiveHealthCheck.Path = string.IsNullOrWhiteSpace(hcPath) ? "/healthz" : (hcPath.StartsWith('/') ? hcPath : "/" + hcPath);
                    existing.ActiveHealthCheck.IntervalSeconds = Math.Clamp(hcIntervalSec, 2, 300);
                    existing.PassiveCircuitBreaker.FailureThreshold = Math.Clamp(failureThreshold, 1, 100);
                    existing.PassiveCircuitBreaker.EjectionSeconds = Math.Clamp(ejectionSec, 5, 600);
                    existing.Destinations = addrs
                        .Select((addr, idx) => new UpstreamDestinationConfig
                        {
                            Id = $"node-{idx + 1}",
                            Address = addr
                        })
                        .ToList();
                }, ct).ConfigureAwait(false);

                return (returnScope, targetSiteId, returnTab, removeCluster
                    ? $"Removed Upstream Cluster '{clusterId}'."
                    : $"Saved Upstream Reverse Proxy Cluster '{clusterId}'.");
            }

            case "/actions/raw-config-save":
            {
                string rawJson = form.GetValueOrDefault("rawJson", "");
                try
                {
                    string msg = await _managementService.SaveRawConfigJsonAsync(targetSiteId, rawJson, ct).ConfigureAwait(false);
                    return (returnScope, targetSiteId, returnTab, msg);
                }
                catch (Exception ex)
                {
                    return (returnScope, targetSiteId, returnTab, $"Invalid JSON configuration: {ex.Message}");
                }
            }

            case "/actions/worker-dump":
            {
                int pid = int.TryParse(form.GetValueOrDefault("processId"), out int p) ? p : Environment.ProcessId;
                string poolId = form.GetValueOrDefault("poolId", "default-pool");
                string dumpPath = await _managementService.CreateWorkerDumpAsync(pid, poolId, ct).ConfigureAwait(false);
                return ("workers", null, null, $"Created crash dump snapshot for PID {pid} at '{dumpPath}'.");
            }

            case "/actions/feature-l4streams":
            {
                string streamId = form.GetValueOrDefault("streamId", "tcp-stream-1").Trim();
                if (string.IsNullOrWhiteSpace(streamId)) streamId = "tcp-stream-1";
                bool removeStream = string.Equals(form.GetValueOrDefault("removeStream"), "true", StringComparison.OrdinalIgnoreCase);
                bool streamEnabled = string.Equals(form.GetValueOrDefault("streamEnabled", "true"), "true", StringComparison.OrdinalIgnoreCase);
                int listenPort = int.TryParse(form.GetValueOrDefault("listenPort"), out int lp) ? Math.Clamp(lp, 1, 65535) : 9443;
                bool sniRouting = string.Equals(form.GetValueOrDefault("enableTlsSniRouting", "true"), "true", StringComparison.OrdinalIgnoreCase);
                string defHost = form.GetValueOrDefault("defaultUpstreamHost", "127.0.0.1").Trim();
                int defPort = int.TryParse(form.GetValueOrDefault("defaultUpstreamPort"), out int dp) ? Math.Clamp(dp, 1, 65535) : 5432;
                string sniRaw = form.GetValueOrDefault("sniRoutesRaw", "");

                var sniRoutes = new List<L4SniRouteConfig>();
                foreach (string item in SplitFormList(sniRaw))
                {
                    int eq = item.IndexOf('=');
                    if (eq <= 0) continue;
                    string sni = item[..eq].Trim();
                    string target = item[(eq + 1)..].Trim();
                    int colon = target.LastIndexOf(':');
                    string uHost = colon > 0 ? target[..colon].Trim() : target;
                    int uPort = colon > 0 && int.TryParse(target[(colon + 1)..], out int p2) ? Math.Clamp(p2, 1, 65535) : 443;
                    sniRoutes.Add(new L4SniRouteConfig { SniHostname = sni, UpstreamHost = uHost, UpstreamPort = uPort });
                }

                await _managementService.MutateServerConfigAsync(srv =>
                {
                    srv.TcpStreams.RemoveAll(s => string.Equals(s.Id, streamId, StringComparison.OrdinalIgnoreCase));
                    if (!removeStream)
                    {
                        srv.TcpStreams.Add(new L4StreamProxyConfig
                        {
                            Id = streamId,
                            Enabled = streamEnabled,
                            ListenPort = listenPort,
                            EnableTlsSniRouting = sniRouting,
                            DefaultUpstreamHost = string.IsNullOrWhiteSpace(defHost) ? "127.0.0.1" : defHost,
                            DefaultUpstreamPort = defPort,
                            SniRoutes = sniRoutes
                        });
                    }
                }, ct).ConfigureAwait(false);

                return (returnScope, targetSiteId, returnTab, removeStream ? $"Removed L4 Stream '{streamId}'." : $"Saved L4 Stream '{streamId}' (port {listenPort}).");
            }

            case "/actions/config-rollback":
            {
                string snapId = form.GetValueOrDefault("snapshotId", "").Trim();
                bool ok = !string.IsNullOrWhiteSpace(snapId) && await _managementService.RollbackSnapshotAsync(snapId, ct).ConfigureAwait(false);
                return ("config", targetSiteId, null, ok ? $"Rolled back configuration to snapshot '{snapId}'." : $"Snapshot '{snapId}' was not found.");
            }

            case "/actions/config-import":
            {
                string format = form.GetValueOrDefault("format", "nginx").Trim();
                string content = form.GetValueOrDefault("content", "");
                LegacyImportResult res = await _managementService.ImportLegacyConfigAsync(format, content, apply: true, ct).ConfigureAwait(false);
                return ("config", targetSiteId, null, $"Imported {res.Sites.Count} site(s), {res.Pools.Count} pool(s), {res.Upstreams.Count} cluster(s) from {format}.");
            }

            case "/actions/worker-kill":
            {
                int pid = int.TryParse(form.GetValueOrDefault("processId"), out int p) ? p : Environment.ProcessId;
                string poolId = form.GetValueOrDefault("poolId", "default-pool");
                string msg = await _managementService.KillWorkerProcessAsync(pid, poolId, ct).ConfigureAwait(false);
                return ("workers", null, null, msg);
            }

            default:
                return (defaultScope, defaultEntity, defaultTab, null);
        }
    }

    // ================================================================================================
    // SCREEN 3: CONFIGURATION & FEATURES MATRIX (design/DesktopWinForms/configuration_features_matrix)
    // ================================================================================================
    private static void RenderConfigurationMatrixView(
        StringBuilder sb,
        ManagementWorkspaceSnapshot workspace,
        SiteCompositeView? selectedSite)
    {
        ServerConfig srvCfg = workspace.ServerConfig;
        bool isSiteContext = selectedSite is not null;
        string? targetSiteId = isSiteContext ? selectedSite!.Config.Id : null;
        bool inherited = false;
        SiteFeaturesConfig activeFeatures = isSiteContext
            ? CompiledSiteFeatures.ResolveEffectiveConfig(selectedSite!.Config.Features, srvCfg.Features, out inherited)
            : srvCfg.Features;

        string inspectorFileLabel = isSiteContext
            ? $"config/sites/{selectedSite!.Config.Id}.json"
            : "config/server.json";
        string rawJson = isSiteContext
            ? JsonSerializer.Serialize(selectedSite!.Config, KiteJsonContext.Default.SiteConfig)
            : JsonSerializer.Serialize(srvCfg, KiteJsonContext.Default.ServerConfig);
        string firstSiteId = workspace.Sites.FirstOrDefault()?.Config.Id ?? "default-site";

        IReadOnlyList<TlsCertificateChainStatus> certs = isSiteContext
            ? (selectedSite!.Status?.Certificates ?? [])
            : workspace.Sites.SelectMany(s => s.Status?.Certificates ?? []).ToList();
        TlsCertificateChainStatus? primaryCert = certs.FirstOrDefault();

        string authSummary =
            $"{(activeFeatures.Authentication.AnonymousEnabled ? "Anon" : "Closed")}" +
            $"{(activeFeatures.Authentication.JwtEnabled ? " + JWT" : "")}" +
            $"{(activeFeatures.Authentication.ApiKeyEnabled ? " + Key" : "")}" +
            $"{(activeFeatures.Authentication.BasicAuthEnabled ? " + Basic" : "")}";

        long totalCacheHits = workspace.Sites.Sum(s => s.Status?.CacheHits ?? 0);
        double cacheHitPct = workspace.ServerStatus?.CacheHitRatioPercent ?? 0.0;

        string targetInputHtml = targetSiteId is not null
            ? $"""<input type="hidden" name="targetSiteId" value="{Enc(targetSiteId)}" />"""
            : """<input type="hidden" name="targetSiteId" value="" />""";

        sb.Append($"""
            <section class="rounded-xl obsidian-card p-space-md shadow-xl flex flex-wrap items-center justify-between gap-space-md">
                <div class="flex flex-wrap items-center gap-space-md">
                    <div class="flex items-center gap-space-xs px-space-sm py-1.5 rounded-lg bg-surface-container-high text-on-surface">
                        <span class="text-primary-container font-bold">⚙</span>
                        <div class="flex flex-col">
                            <span class="font-badge-label text-on-surface-variant uppercase">Target Context</span>
                            <span class="font-label-code-md font-semibold text-primary">{Enc(isSiteContext ? selectedSite!.Config.Name : srvCfg.ServerName)} / {Enc(inspectorFileLabel)}</span>
                        </div>
                    </div>
                    <div class="flex items-center bg-surface-container-lowest rounded-lg p-0.5 border border-surface-variant">
                        <a href="/?scope=server&tab=settings" class="px-space-sm py-1 rounded font-label-code-sm {(!isSiteContext ? "bg-primary-container text-on-primary-container font-semibold" : "text-on-surface-variant hover:text-on-surface")}">Server Level</a>
                        <a href="/?scope=config&entity={Uri.EscapeDataString(firstSiteId)}" class="px-space-sm py-1 rounded font-label-code-sm {(isSiteContext ? "bg-primary-container text-on-primary-container font-semibold" : "text-on-surface-variant hover:text-on-surface")}">Site Level</a>
                    </div>
            """);

        if (isSiteContext)
        {
            sb.Append("""
                    <select onchange="if(this.value) location.href='/?scope=config&entity=' + encodeURIComponent(this.value);" class="obsidian-input" style="width:auto; min-width: 170px;">
                """);
            foreach (SiteCompositeView s in workspace.Sites)
            {
                string sel = string.Equals(s.Config.Id, selectedSite!.Config.Id, StringComparison.OrdinalIgnoreCase) ? " selected" : "";
                sb.Append($"<option value=\"{Enc(s.Config.Id)}\"{sel}>{Enc(s.Config.Name)} ({Enc(s.Config.Id)})</option>");
            }
            sb.Append($"""
                    </select>
                    <form method="post" action="/actions/feature-inheritance" style="display:inline;">
                        {targetInputHtml}
                        <input type="hidden" name="inheritFromServer" value="{(inherited ? "false" : "true")}" />
                        <button type="submit" class="px-2.5 py-1 rounded font-label-code-sm {(inherited ? "bg-tertiary/10 text-tertiary-container border border-surface-variant" : "bg-secondary/10 text-secondary border border-surface-variant")}">
                            {(inherited ? "✓ Inheriting Server Defaults (Click to Override)" : "✎ Custom Site Override (Click to Inherit)")}
                        </button>
                    </form>
                """);
        }

        sb.Append("""
                </div>
                <div class="flex flex-wrap items-center gap-space-sm">
                    <button type="button" id="btn-open-history" onclick="toggleModal('modal-history', true); loadConfigHistory();" class="cyber-btn-secondary px-2.5 py-1.5 rounded-lg font-label-code-sm">🕒 History</button>
                    <button type="button" id="btn-open-import" onclick="toggleModal('modal-import', true)" class="cyber-btn-secondary px-2.5 py-1.5 rounded-lg font-label-code-sm">⇄ Import</button>
                    <button type="button" id="btn-open-debugger" onclick="toggleModal('modal-debugger', true)" class="cyber-btn-secondary px-2.5 py-1.5 rounded-lg font-label-code-sm text-primary">🧪 Debugger</button>
                    <input id="feature-search" type="text" class="obsidian-input max-w-xs" placeholder="Search directives or modules..." oninput="filterGlobalElements(this.value)" />
            """);

        if (!isSiteContext)
        {
            sb.Append("""
                    <button type="submit" form="form-save-server" class="cyber-btn-solid flex items-center gap-space-xs px-space-md py-1.5 rounded-lg font-headline-sm">
                        <span>↑ Commit Changes</span>
                    </button>
                """);
        }

        sb.Append("""
                </div>
            </section>
            """);

        sb.Append("""
            <div class="grid grid-cols-1 lg:grid-cols-12 gap-space-md items-start mt-space-md">
            """);

        // ====================================================================================
        // LEFT COLUMN: FRONT & CENTER JSON CONFIG & DIRECTIVES (lg:col-span-7)
        // ====================================================================================
        sb.Append($"""
                <div class="lg:col-span-7 flex flex-col gap-space-md min-w-0">
                    <!-- PRIMARY: LIVE JSON INSPECTOR & EDITOR -->
                    <section class="rounded-xl obsidian-card p-space-md shadow-2xl flex flex-col gap-space-sm laser-edge-cyan">
                        <div class="flex flex-wrap items-center justify-between gap-space-sm pb-space-xs border-b border-surface-variant">
                            <div class="flex items-center gap-space-sm">
                                <div class="flex items-center gap-2">
                                    <span class="text-primary-container font-mono font-bold text-xs px-2 py-0.5 rounded bg-primary-container/15">JSON</span>
                                    <span class="font-headline-sm text-on-surface font-semibold">{Enc(inspectorFileLabel)} Live Inspector</span>
                                </div>
                                <div class="flex items-center bg-surface-container-lowest rounded-lg p-0.5 border border-surface-variant">
                                    <button type="button" id="btn-toggle-json" onclick="switchInspectorTab('json')" class="px-space-sm py-1 rounded font-label-code-sm bg-primary-container text-on-primary-container font-semibold">Raw JSON ({Enc(inspectorFileLabel)})</button>
                                    <button type="button" id="btn-toggle-visual" onclick="switchInspectorTab('visual')" class="px-space-sm py-1 rounded font-label-code-sm text-on-surface-variant hover:text-on-surface">Visual Tree</button>
                                </div>
                            </div>
                            <div class="flex items-center gap-space-sm">
                                <span class="hidden sm:inline-block px-2 py-0.5 rounded-full bg-tertiary/10 text-tertiary font-label-code-sm">✓ Pure JSON Schema Valid (.NET 10)</span>
                                <button type="button" onclick="copyToClipboard(document.getElementById('rawJsonTextarea').value)" class="cyber-btn-secondary px-2 py-1 rounded font-label-code-sm">Copy JSON</button>
                            </div>
                        </div>

                        <form id="json-editor-container" method="post" action="/actions/raw-config-save" class="flex flex-col gap-space-sm">
                            {targetInputHtml}
                            <div class="relative">
                                <textarea id="rawJsonTextarea" name="rawJson" rows="22" class="obsidian-input font-mono text-primary bg-surface-container-lowest" style="min-height: 460px; font-size: 12px; line-height: 1.6;" spellcheck="false">{Enc(rawJson)}</textarea>
                            </div>
                            <div class="flex items-center justify-between font-label-code-sm">
                                <span class="text-on-surface-variant/70 text-xs">Direct filesystem write to {Enc(inspectorFileLabel)} • Hot-reloads in &lt; 1ms via FileSystemWatcher</span>
                                <button type="submit" class="cyber-btn-primary px-space-md py-1.5 rounded font-label-code-md">Validate &amp; Save Raw JSON to {Enc(inspectorFileLabel)}</button>
                            </div>
                        </form>

                        <div id="visual-tree-container" class="hidden flex flex-col gap-space-sm font-label-code-sm">
                            <div class="p-space-sm rounded bg-surface-container-lowest border border-surface-variant">
                                <div class="text-primary font-semibold mb-1">features / urlRewrite ({activeFeatures.UrlRewrite.Rules.Count} rules)</div>
                                <div class="text-on-surface-variant">Trailing Slash: {activeFeatures.RedirectsAndErrors.EnforceTrailingSlash} • Errors: {activeFeatures.RedirectsAndErrors.ErrorMode}</div>
                            </div>
                            <div class="p-space-sm rounded bg-surface-container-lowest border border-surface-variant">
                                <div class="text-secondary font-semibold mb-1">features / security</div>
                                <div class="text-on-surface-variant">Auth: {Enc(authSummary)} • IP Rules: {activeFeatures.IpRestrictions.Rules.Count} • MaxBody: {activeFeatures.RequestFiltering.MaxAllowedContentLength:N0} B</div>
                            </div>
                            <div class="p-space-sm rounded bg-surface-container-lowest border border-surface-variant">
                                <div class="text-tertiary-container font-semibold mb-1">features / diagnostics</div>
                                <div class="text-on-surface-variant">Cache Gen #{activeFeatures.Caching.FlushGeneration} • FREB: {Enc(activeFeatures.FailedRequestTracing.StatusCodeTrigger)} • Br Lvl {activeFeatures.Compression.BrotliLevel}</div>
                            </div>
                        </div>
                    </section>
            """);

        if (!isSiteContext)
        {
            sb.Append($"""
                    <!-- GLOBAL SERVER DIRECTIVES -->
                    <form id="form-save-server" method="post" action="/actions/server-settings" class="rounded-xl obsidian-card p-space-md shadow-lg">
                        <input type="hidden" name="inlineTransportFieldsPresent" value="true" />
                        <div class="flex items-center justify-between mb-space-sm pb-1 border-b border-surface-variant">
                            <span class="font-headline-sm text-primary font-semibold uppercase tracking-wide">Global Kestrel Engine &amp; Control Plane Directives (config/server.json)</span>
                            <span class="font-badge-label px-2 py-0.5 rounded bg-tertiary/10 text-tertiary">Atomic Write + SHA-256 Reload</span>
                        </div>
                        <div class="grid grid-cols-1 md:grid-cols-2 gap-space-md font-label-code-sm">
                            <div>
                                <label class="text-on-surface-variant">Server Node Name</label>
                                <input type="text" name="serverName" value="{Enc(srvCfg.ServerName)}" class="obsidian-input mt-1" />
                            </div>
                            <div>
                                <label class="text-on-surface-variant">Max Concurrent Connections</label>
                                <input type="number" name="maxConcurrentConnections" value="{srvCfg.MaxConcurrentConnections}" class="obsidian-input mt-1" />
                            </div>
                            <div>
                                <label class="text-on-surface-variant">Max Request Body Size (Bytes)</label>
                                <input type="number" name="maxRequestBodySize" value="{srvCfg.MaxRequestBodySize}" class="obsidian-input mt-1" />
                            </div>
                            <div>
                                <label class="text-on-surface-variant">Keep-Alive Timeout (Seconds)</label>
                                <input type="number" name="keepAliveTimeoutSeconds" value="{srvCfg.KeepAliveTimeoutSeconds}" class="obsidian-input mt-1" />
                            </div>
                            <div>
                                <label class="text-on-surface-variant">Header Timeout (Seconds)</label>
                                <input type="number" name="headerTimeoutSeconds" value="{srvCfg.HeaderTimeoutSeconds}" class="obsidian-input mt-1" />
                            </div>
                            <div>
                                <label class="text-on-surface-variant">Status Publish Interval (ms)</label>
                                <input type="number" name="statusPublishIntervalMilliseconds" value="{srvCfg.StatusPublishIntervalMilliseconds}" class="obsidian-input mt-1" />
                            </div>
                            <div>
                                <label class="text-on-surface-variant">Access Log Serialization Format</label>
                                <select name="accessLogFormat" class="obsidian-input mt-1">
                                    <option value="W3C" {(srvCfg.Observability.AccessLogFormat == AccessLogFormat.W3C ? "selected" : "")}>W3C Extended (.log)</option>
                                    <option value="Json" {(srvCfg.Observability.AccessLogFormat == AccessLogFormat.Json ? "selected" : "")}>Structured JSON (.ndjson)</option>
                                    <option value="Both" {(srvCfg.Observability.AccessLogFormat == AccessLogFormat.Both ? "selected" : "")}>Both (W3C + JSON)</option>
                                </select>
                            </div>
                            <div class="flex flex-col justify-end gap-1.5 pb-1">
                                <label class="flex items-center gap-2 text-on-surface">
                                    <input type="checkbox" name="enableHttp3" value="true" {(srvCfg.Transport.EnableHttp3 ? "checked" : "")} />
                                    HTTP/3 (QUIC) + Alt-Svc Header
                                </label>
                                <label class="flex items-center gap-2 text-on-surface">
                                    <input type="checkbox" name="enablePrometheusMetrics" value="true" {(srvCfg.Observability.EnablePrometheusMetrics ? "checked" : "")} />
                                    Prometheus Scrape Endpoint ({Enc(srvCfg.Observability.MetricsPath)})
                                </label>
                            </div>
                        </div>
                        <div class="flex justify-end mt-space-sm pt-space-xs border-t border-surface-variant">
                            <button type="submit" class="cyber-btn-primary px-4 py-1.5 rounded font-label-code-sm">Save Directives</button>
                        </div>
                    </form>
                """);
        }
        else
        {
            SiteConfig sCfg = selectedSite!.Config;
            string upstreamLabel = !string.IsNullOrWhiteSpace(sCfg.RootApplication.UpstreamClusterId)
                ? $"Cluster '{sCfg.RootApplication.UpstreamClusterId}' (Canary: {(string.IsNullOrWhiteSpace(sCfg.RootApplication.CanaryClusterId) ? "Off" : $"{sCfg.RootApplication.CanaryClusterId} @ {sCfg.RootApplication.CanaryWeightPercent}%")})"
                : (!string.IsNullOrWhiteSpace(sCfg.RootApplication.UpstreamUrl) ? $"Direct: {sCfg.RootApplication.UpstreamUrl}" : "Local Static / Worker");

            sb.Append($"""
                    <!-- SITE DIRECTIVES & BINDINGS SUMMARY -->
                    <div class="rounded-xl obsidian-card p-space-md shadow-lg font-label-code-sm">
                        <div class="flex items-center justify-between mb-space-sm pb-1 border-b border-surface-variant">
                            <span class="font-headline-sm text-primary font-semibold uppercase tracking-wide">Site Directives &amp; Bindings Summary</span>
                            <div class="flex items-center gap-2">
                                <span class="font-badge-label px-2 py-0.5 rounded bg-surface-container-high text-on-surface">Pool: {Enc(sCfg.RootApplication.PoolId)}</span>
                                <a href="/?scope=sites&entity={Uri.EscapeDataString(sCfg.Id)}" class="cyber-btn-secondary px-2.5 py-0.5 rounded">Edit Bindings &amp; Canary Routing →</a>
                            </div>
                        </div>
                        <div class="grid grid-cols-1 md:grid-cols-2 gap-space-md">
                            <div>
                                <span class="text-on-surface-variant">Physical Root Directory:</span>
                                <div class="p-1.5 rounded bg-surface-container-lowest text-on-surface truncate font-mono mt-1">{Enc(sCfg.RootApplication.PhysicalPath)}</div>
                            </div>
                            <div>
                                <span class="text-on-surface-variant">Active Listeners &amp; Bindings:</span>
                                <div class="p-1.5 rounded bg-surface-container-lowest text-primary-container truncate font-mono mt-1">{Enc(string.Join(", ", sCfg.Bindings.Select(b => $"{b.Protocol}://{b.Host}:{b.Port}")))}</div>
                            </div>
                            <div>
                                <span class="text-on-surface-variant">Hosting Mode:</span>
                                <div class="p-1.5 rounded bg-surface-container-lowest text-on-surface font-mono mt-1">{sCfg.RootApplication.HostingMode} {(sCfg.RootApplication.EnableStreamingPassThrough ? "• SSE/gRPC Pass-Through" : "")}</div>
                            </div>
                            <div>
                                <span class="text-on-surface-variant">Upstream / Canary Route:</span>
                                <div class="p-1.5 rounded bg-surface-container-lowest text-tertiary-container truncate font-mono mt-1">{Enc(upstreamLabel)}</div>
                            </div>
                        </div>
                    </div>
                """);
        }

        sb.Append("""
                </div>
            """);

        // ====================================================================================
        // RIGHT COLUMN: FEATURE MODULES TOOLBOX (lg:col-span-5)
        // ====================================================================================
        sb.Append($"""
                <div class="lg:col-span-5 flex flex-col gap-space-sm min-w-0">
                    <div class="rounded-xl obsidian-card p-space-sm flex items-center justify-between border-b border-surface-variant">
                        <div class="flex items-center gap-2">
                            <span class="font-headline-sm text-on-surface font-semibold uppercase tracking-wide">HTTP &amp; Routing Engine</span>
                            <span class="font-badge-label px-1.5 py-0.5 rounded bg-surface-container-high text-primary font-semibold">16 Modules</span>
                        </div>
                        <span class="font-label-code-sm text-tertiary-container">Zero-Allocation Integrated</span>
                    </div>

                    <!-- 1. URL Rewrite -->
                    <div class="rounded-lg obsidian-card obsidian-card-interactive p-space-sm flex flex-col gap-1.5" data-filterable>
                        <div class="flex items-center justify-between">
                            <div class="flex items-center gap-1.5">
                                <span class="text-primary-container font-mono text-xs">◆</span>
                                <span class="font-label-code-md text-on-surface font-semibold">URL Rewrite</span>
                            </div>
                            <span class="font-badge-label px-1.5 py-0.5 rounded bg-tertiary/10 text-tertiary">{activeFeatures.UrlRewrite.Rules.Count} Rules</span>
                        </div>
                        <p class="font-body-sm text-on-surface-variant text-xs">Inbound regex redirects (301/302), path rewrites &amp; canonical maps.</p>
                        <div class="flex items-center justify-between pt-1 border-t border-surface-variant font-label-code-sm">
                            <span class="text-on-surface-variant">{(activeFeatures.UrlRewrite.Enabled ? "Active" : "Disabled")}</span>
                            <button type="button" data-modal-trigger="modal-rewrite" onclick="toggleModal('modal-rewrite', true)" class="text-primary hover:text-primary-container font-semibold">Edit Rules →</button>
                        </div>
                    </div>

                    <!-- 2. Redirect & Default Doc -->
                    <div class="rounded-lg obsidian-card obsidian-card-interactive p-space-sm flex flex-col gap-1.5" data-filterable>
                        <div class="flex items-center justify-between">
                            <div class="flex items-center gap-1.5">
                                <span class="text-primary-container font-mono text-xs">◆</span>
                                <span class="font-label-code-md text-on-surface font-semibold">Redirect &amp; Default Doc</span>
                            </div>
                            <span class="font-badge-label px-1.5 py-0.5 rounded bg-surface-container-highest text-on-surface-variant">Static File</span>
                        </div>
                        <p class="font-body-sm text-on-surface-variant text-xs">Directory root resolution, trailing slashes &amp; custom errors.</p>
                        <div class="flex items-center justify-between pt-1 border-t border-surface-variant font-label-code-sm">
                            <span class="text-tertiary-container">{(inherited ? "Inherited Root" : "Explicit")}</span>
                            <button type="button" onclick="toggleModal('modal-redirects', true)" class="text-primary hover:text-primary-container">Configure</button>
                        </div>
                    </div>

                    <!-- 3. MIME Mapping -->
                    <div class="rounded-lg obsidian-card obsidian-card-interactive p-space-sm flex flex-col gap-1.5" data-filterable>
                        <div class="flex items-center justify-between">
                            <div class="flex items-center gap-1.5">
                                <span class="text-primary-container font-mono text-xs">◆</span>
                                <span class="font-label-code-md text-on-surface font-semibold">MIME Mapping</span>
                            </div>
                            <span class="font-badge-label px-1.5 py-0.5 rounded bg-surface-container-high text-primary">{activeFeatures.MimeMappings.CustomMappings.Count} Overrides</span>
                        </div>
                        <p class="font-body-sm text-on-surface-variant text-xs">Content-Type dispatch with binary &amp; WebAssembly support.</p>
                        <div class="flex items-center justify-between pt-1 border-t border-surface-variant font-label-code-sm">
                            <span class="text-on-surface-variant">Static File Handler</span>
                            <button type="button" onclick="toggleModal('modal-mime', true)" class="text-primary hover:text-primary-container">Add MIME</button>
                        </div>
                    </div>

                    <!-- 4. Response Headers & CORS -->
                    <div class="rounded-lg obsidian-card obsidian-card-interactive p-space-sm flex flex-col gap-1.5" data-filterable>
                        <div class="flex items-center justify-between">
                            <div class="flex items-center gap-1.5">
                                <span class="text-primary-container font-mono text-xs">◆</span>
                                <span class="font-label-code-md text-on-surface font-semibold">Response Headers &amp; CORS</span>
                            </div>
                            <span class="font-badge-label px-1.5 py-0.5 rounded bg-tertiary/10 text-tertiary">{(activeFeatures.StripServerHeader ? "Hardened" : "Standard")}</span>
                        </div>
                        <p class="font-body-sm text-on-surface-variant text-xs">Compliance headers, HSTS, Server stripping &amp; CORS policy.</p>
                        <div class="flex items-center justify-between pt-1 border-t border-surface-variant font-label-code-sm">
                            <span class="text-on-surface-variant">Server: {(activeFeatures.StripServerHeader ? "Stripped" : "Kite/2.0")}</span>
                            <button type="button" onclick="toggleModal('modal-headers', true)" class="text-primary hover:text-primary-container">Edit Headers</button>
                        </div>
                    </div>

                    <!-- 5. SSL / TLS Bindings -->
                    <div class="rounded-lg obsidian-card obsidian-card-interactive p-space-sm flex flex-col gap-1.5" data-filterable>
                        <div class="flex items-center justify-between">
                            <div class="flex items-center gap-1.5">
                                <span class="text-tertiary-container font-mono text-xs">◆</span>
                                <span class="font-label-code-md text-on-surface font-semibold">SSL / TLS Bindings</span>
                            </div>
                            <span class="font-badge-label px-1.5 py-0.5 rounded bg-tertiary/10 text-tertiary">{(activeFeatures.TlsPolicy.StrictSni ? "SNI Active" : "Fallback")}</span>
                        </div>
                        <p class="font-body-sm text-on-surface-variant text-xs">X.509 cert chain inspection &amp; TLS 1.3 enforcement.</p>
                        <div class="flex items-center justify-between pt-1 border-t border-surface-variant font-label-code-sm">
                            <span class="text-on-surface-variant">{certs.Count} TLS Cert(s)</span>
                            <button type="button" onclick="toggleModal('modal-tls', true)" class="text-primary hover:text-primary-container">Inspect Chain</button>
                        </div>
                    </div>

                    <!-- 6. Authentication -->
                    <div class="rounded-lg obsidian-card obsidian-card-interactive p-space-sm flex flex-col gap-1.5" data-filterable>
                        <div class="flex items-center justify-between">
                            <div class="flex items-center gap-1.5">
                                <span class="text-tertiary-container font-mono text-xs">◆</span>
                                <span class="font-label-code-md text-on-surface font-semibold">Authentication</span>
                            </div>
                            <span class="font-badge-label px-1.5 py-0.5 rounded bg-surface-container-high text-primary">{Enc(authSummary)}</span>
                        </div>
                        <p class="font-body-sm text-on-surface-variant text-xs">Edge Anonymous, JWT Bearer (HS256/RS256), API Key &amp; Basic auth.</p>
                        <div class="flex items-center justify-between pt-1 border-t border-surface-variant font-label-code-sm">
                            <span class="text-on-surface-variant">Challenge: 401</span>
                            <button type="button" onclick="toggleModal('modal-auth', true)" class="text-primary hover:text-primary-container">Configure Providers</button>
                        </div>
                    </div>

                    <!-- 7. IP & Domain Filters -->
                    <div class="rounded-lg obsidian-card obsidian-card-interactive p-space-sm flex flex-col gap-1.5" data-filterable>
                        <div class="flex items-center justify-between">
                            <div class="flex items-center gap-1.5">
                                <span class="text-tertiary-container font-mono text-xs">◆</span>
                                <span class="font-label-code-md text-on-surface font-semibold">IP &amp; Domain Filters</span>
                            </div>
                            <span class="font-badge-label px-1.5 py-0.5 rounded {(activeFeatures.IpRestrictions.Enabled ? "bg-tertiary/10 text-tertiary" : "bg-surface-container-high text-on-surface-variant")}">{(activeFeatures.IpRestrictions.Enabled ? (activeFeatures.IpRestrictions.AllowUnmatched ? "Blocklist" : "Allowlist") : "Pass All")}</span>
                        </div>
                        <p class="font-body-sm text-on-surface-variant text-xs">Subnet CIDR perimeter guarding (403.6 SubStatus rejection).</p>
                        <div class="flex items-center justify-between pt-1 border-t border-surface-variant font-label-code-sm">
                            <span class="text-on-surface-variant">{activeFeatures.IpRestrictions.Rules.Count} Rules Active</span>
                            <button type="button" onclick="toggleModal('modal-ip', true)" class="text-primary hover:text-primary-container font-semibold">Edit Subnets</button>
                        </div>
                    </div>

                    <!-- 8. Request Filtering -->
                    <div class="rounded-lg obsidian-card obsidian-card-interactive p-space-sm flex flex-col gap-1.5" data-filterable>
                        <div class="flex items-center justify-between">
                            <div class="flex items-center gap-1.5">
                                <span class="text-tertiary-container font-mono text-xs">◆</span>
                                <span class="font-label-code-md text-on-surface font-semibold">Request Filtering</span>
                            </div>
                            <span class="font-badge-label px-1.5 py-0.5 rounded bg-surface-container-high text-primary">{(activeFeatures.RequestFiltering.Enabled ? "Active Gate" : "Bypassed")}</span>
                        </div>
                        <p class="font-body-sm text-on-surface-variant text-xs">Payload size limits, HTTP verb whitelist &amp; blocked extensions.</p>
                        <div class="flex items-center justify-between pt-1 border-t border-surface-variant font-label-code-sm">
                            <span class="text-on-surface-variant">Max: {activeFeatures.RequestFiltering.MaxAllowedContentLength:N0} B</span>
                            <button type="button" onclick="toggleModal('modal-filtering', true)" class="text-primary hover:text-primary-container">Edit Gate</button>
                        </div>
                    </div>

                    <!-- 9. Output Caching -->
                    <div class="rounded-lg obsidian-card obsidian-card-interactive p-space-sm flex flex-col gap-1.5" data-filterable>
                        <div class="flex items-center justify-between">
                            <div class="flex items-center gap-1.5">
                                <span class="text-secondary font-mono text-xs">◆</span>
                                <span class="font-label-code-md text-on-surface font-semibold">Output Caching</span>
                            </div>
                            <span class="font-badge-label px-1.5 py-0.5 rounded bg-tertiary/10 text-tertiary">{(activeFeatures.Caching.Enabled ? $"Gen #{activeFeatures.Caching.FlushGeneration}" : "Disabled")}</span>
                        </div>
                        <p class="font-body-sm text-on-surface-variant text-xs">RFC 9111 segmented cache with instant atomic generation flush.</p>
                        <div class="flex items-center justify-between pt-1 border-t border-surface-variant font-label-code-sm">
                            <button type="button" onclick="toggleModal('modal-cache', true)" class="text-on-surface-variant hover:text-on-surface">Configure Policy</button>
                            <form method="post" action="/actions/feature-cache-flush" style="display:inline;">
                                {targetInputHtml}
                                <input type="hidden" name="triggerFlush" value="true" />
                                <button type="submit" class="text-primary hover:text-primary-container font-semibold">Flush Cache</button>
                            </form>
                        </div>
                    </div>

                    <!-- 10. Failed Request Tracing (FREB) -->
                    <div class="rounded-lg obsidian-card obsidian-card-interactive p-space-sm flex flex-col gap-1.5" data-filterable>
                        <div class="flex items-center justify-between">
                            <div class="flex items-center gap-1.5">
                                <span class="text-secondary font-mono text-xs">◆</span>
                                <span class="font-label-code-md text-on-surface font-semibold">Failed Request Tracing (FREB)</span>
                            </div>
                            <span class="font-badge-label px-1.5 py-0.5 rounded bg-secondary-container/20 text-secondary">{(activeFeatures.FailedRequestTracing.Enabled ? "Armed" : "Off")}</span>
                        </div>
                        <p class="font-body-sm text-on-surface-variant text-xs">Pipeline stage timings &amp; CLR exception capture on faults/slow calls.</p>
                        <div class="flex items-center justify-between pt-1 border-t border-surface-variant font-label-code-sm">
                            <a href="/?scope=workers" class="text-tertiary-container hover:underline">View Traces →</a>
                            <button type="button" onclick="toggleModal('modal-freb', true)" class="text-primary hover:text-primary-container font-semibold">Configure Triggers</button>
                        </div>
                    </div>

                    <!-- 11. Content Compression -->
                    <div class="rounded-lg obsidian-card obsidian-card-interactive p-space-sm flex flex-col gap-1.5" data-filterable>
                        <div class="flex items-center justify-between">
                            <div class="flex items-center gap-1.5">
                                <span class="text-secondary font-mono text-xs">◆</span>
                                <span class="font-label-code-md text-on-surface font-semibold">Content Compression</span>
                            </div>
                            <span class="font-badge-label px-1.5 py-0.5 rounded bg-tertiary/10 text-tertiary">{(activeFeatures.Compression.Enabled ? "Static + Dynamic" : "Off")}</span>
                        </div>
                        <p class="font-body-sm text-on-surface-variant text-xs">Brotli (RFC 7932), Gzip (RFC 1952) &amp; Zstd (RFC 8878) levels.</p>
                        <div class="flex items-center justify-between pt-1 border-t border-surface-variant font-label-code-sm">
                            <span class="text-on-surface-variant">Brotli L{activeFeatures.Compression.BrotliLevel} • Gzip L{activeFeatures.Compression.GzipLevel}</span>
                            <button type="button" onclick="toggleModal('modal-compression', true)" class="text-primary hover:text-primary-container">Configure Schemes</button>
                        </div>
                    </div>

                    <!-- 12. Edge Rate Limiting -->
                    <div class="rounded-lg obsidian-card obsidian-card-interactive p-space-sm flex flex-col gap-1.5" data-filterable>
                        <div class="flex items-center justify-between">
                            <div class="flex items-center gap-1.5">
                                <span class="text-primary font-mono text-xs">◆</span>
                                <span class="font-label-code-md text-on-surface font-semibold">Edge Rate Limiting</span>
                            </div>
                            <span class="font-badge-label px-1.5 py-0.5 rounded {(activeFeatures.RateLimiting.Enabled ? "bg-tertiary/10 text-tertiary" : "bg-surface-container-high text-on-surface-variant")}">{(activeFeatures.RateLimiting.Enabled ? activeFeatures.RateLimiting.Algorithm.ToString() : "Off")}</span>
                        </div>
                        <p class="font-body-sm text-on-surface-variant text-xs">Partitioned SlidingWindow/TokenBucket throttle (HTTP 429 + Retry-After).</p>
                        <div class="flex items-center justify-between pt-1 border-t border-surface-variant font-label-code-sm">
                            <span class="text-on-surface-variant">{activeFeatures.RateLimiting.PermitLimit} req / {activeFeatures.RateLimiting.WindowSeconds}s</span>
                            <button type="button" data-modal-trigger="modal-ratelimit" onclick="toggleModal('modal-ratelimit', true)" class="text-primary hover:text-primary-container font-semibold">Configure Limits</button>
                        </div>
                    </div>

                    <!-- 13. Auto-HTTPS (ACME) & mTLS -->
                    <div class="rounded-lg obsidian-card obsidian-card-interactive p-space-sm flex flex-col gap-1.5" data-filterable>
                        <div class="flex items-center justify-between">
                            <div class="flex items-center gap-1.5">
                                <span class="text-tertiary font-mono text-xs">◆</span>
                                <span class="font-label-code-md text-on-surface font-semibold">Auto-HTTPS (ACME) &amp; mTLS</span>
                            </div>
                            <span class="font-badge-label px-1.5 py-0.5 rounded bg-surface-container-high text-tertiary-container">{(activeFeatures.TlsPolicy.Acme.Enabled ? "ACME Armed" : activeFeatures.TlsPolicy.Mtls.ClientCertificateMode.ToString())}</span>
                        </div>
                        <p class="font-body-sm text-on-surface-variant text-xs">RFC 8555 ACME v2 (HTTP-01/TLS-ALPN-01), zero-downtime rotation &amp; mTLS.</p>
                        <div class="flex items-center justify-between pt-1 border-t border-surface-variant font-label-code-sm">
                            <span class="text-on-surface-variant">{activeFeatures.TlsPolicy.Acme.Domains.Count} ACME domain(s)</span>
                            <button type="button" data-modal-trigger="modal-acme-mtls" onclick="toggleModal('modal-acme-mtls', true)" class="text-primary hover:text-primary-container font-semibold">ACME &amp; mTLS</button>
                        </div>
                    </div>

                    <!-- 14. HTTP/3 QUIC, Transport & Observability -->
                    <div class="rounded-lg obsidian-card obsidian-card-interactive p-space-sm flex flex-col gap-1.5" data-filterable>
                        <div class="flex items-center justify-between">
                            <div class="flex items-center gap-1.5">
                                <span class="text-primary-container font-mono text-xs">◆</span>
                                <span class="font-label-code-md text-on-surface font-semibold">HTTP/3 QUIC &amp; Observability</span>
                            </div>
                            <span class="font-badge-label px-1.5 py-0.5 rounded bg-surface-container-high text-primary">{(srvCfg.Transport.EnableHttp3 ? "H3 + Prom" : "H2 + Prom")}</span>
                        </div>
                        <p class="font-body-sm text-on-surface-variant text-xs">QUIC/Alt-Svc, Slowloris guard, W3C tracing &amp; /__kite/metrics.</p>
                        <div class="flex items-center justify-between pt-1 border-t border-surface-variant font-label-code-sm">
                            <span class="text-on-surface-variant">Logs: {srvCfg.Observability.AccessLogFormat}</span>
                            <button type="button" data-modal-trigger="modal-transport-obs" onclick="toggleModal('modal-transport-obs', true)" class="text-primary hover:text-primary-container font-semibold">Transport &amp; OTel</button>
                        </div>
                    </div>

                    <!-- 15. Upstream Reverse Proxy Clusters -->
                    <div class="rounded-lg obsidian-card obsidian-card-interactive p-space-sm flex flex-col gap-1.5" data-filterable>
                        <div class="flex items-center justify-between">
                            <div class="flex items-center gap-1.5">
                                <span class="text-secondary font-mono text-xs">◆</span>
                                <span class="font-label-code-md text-on-surface font-semibold">Upstream Proxy Clusters</span>
                            </div>
                            <span class="font-badge-label px-1.5 py-0.5 rounded bg-surface-container-high text-secondary">{srvCfg.Upstreams.Count} Cluster(s)</span>
                        </div>
                        <p class="font-body-sm text-on-surface-variant text-xs">RoundRobin, P2C, LeastReq, /healthz active probes &amp; canary splits.</p>
                        <div class="flex items-center justify-between pt-1 border-t border-surface-variant font-label-code-sm">
                            <span class="text-on-surface-variant">{srvCfg.Upstreams.Sum(u => u.Destinations.Count)} destination(s)</span>
                            <button type="button" data-modal-trigger="modal-upstreams" onclick="toggleModal('modal-upstreams', true)" class="text-primary hover:text-primary-container font-semibold">Edit Clusters</button>
                        </div>
                    </div>

                    <!-- 16. SPA Fallback & Edge WAF -->
                    <div class="rounded-lg obsidian-card obsidian-card-interactive p-space-sm flex flex-col gap-1.5" data-filterable>
                        <div class="flex items-center justify-between">
                            <div class="flex items-center gap-1.5">
                                <span class="text-tertiary font-mono text-xs">◆</span>
                                <span class="font-label-code-md text-on-surface font-semibold">SPA Fallback &amp; Edge WAF</span>
                            </div>
                            <span class="font-badge-label px-1.5 py-0.5 rounded bg-surface-container-high text-tertiary-container">{(activeFeatures.EdgeWaf.Enabled ? "WAF Active" : activeFeatures.SpaFallback.Enabled ? "SPA On" : "Standby")}</span>
                        </div>
                        <p class="font-body-sm text-on-surface-variant text-xs">SIMD SearchValues WAF (SQLi/XSS/Traversal/JSON depth) &amp; HTML5 SPA rewrite.</p>
                        <div class="flex items-center justify-between pt-1 border-t border-surface-variant font-label-code-sm">
                            <span class="text-on-surface-variant">{Enc(activeFeatures.SpaFallback.FallbackDocument)}</span>
                            <button type="button" data-modal-trigger="modal-spawaf" onclick="toggleModal('modal-spawaf', true)" class="text-primary hover:text-primary-container font-semibold">Configure WAF/SPA</button>
                        </div>
                    </div>

                    <!-- 17. L4 TCP & TLS SNI Passthrough Streams -->
                    <div class="rounded-lg obsidian-card obsidian-card-interactive p-space-sm flex flex-col gap-1.5" data-filterable>
                        <div class="flex items-center justify-between">
                            <div class="flex items-center gap-1.5">
                                <span class="text-primary-container font-mono text-xs">◆</span>
                                <span class="font-label-code-md text-on-surface font-semibold">L4 TCP &amp; TLS SNI Streams</span>
                            </div>
                            <span class="font-badge-label px-1.5 py-0.5 rounded bg-surface-container-high text-primary">{srvCfg.TcpStreams.Count} Listener(s)</span>
                        </div>
                        <p class="font-body-sm text-on-surface-variant text-xs">Zero-alloc ClientHello SNI passthrough &amp; raw TCP L4 socket proxy.</p>
                        <div class="flex items-center justify-between pt-1 border-t border-surface-variant font-label-code-sm">
                            <span class="text-on-surface-variant">{srvCfg.TcpStreams.Sum(t => t.SniRoutes.Count)} SNI route(s)</span>
                            <button type="button" data-modal-trigger="modal-l4streams" onclick="toggleModal('modal-l4streams', true)" class="text-primary hover:text-primary-container font-semibold">Configure Streams</button>
                        </div>
                    </div>
                </div>
            </div>
            """);

        RenderFeatureConfigModalPlaceholdersAndScripts(sb, targetSiteId);
    }

    private static readonly string[] ConfigModalIds =
    [
        "modal-rewrite", "modal-redirects", "modal-mime", "modal-headers", "modal-tls",
        "modal-auth", "modal-ip", "modal-filtering", "modal-cache", "modal-freb",
        "modal-compression", "modal-ratelimit", "modal-acme-mtls", "modal-transport-obs",
        "modal-upstreams", "modal-spawaf", "modal-l4streams", "modal-history",
        "modal-import", "modal-debugger"
    ];

    private static void RenderFeatureConfigModalPlaceholdersAndScripts(StringBuilder sb, string? targetSiteId)
    {
        sb.Append($"""<span id="kite-modals-ctx" class="hidden" data-site="{Enc(targetSiteId ?? "")}"></span>""");
        foreach (string modalId in ConfigModalIds)
        {
            sb.Append($"""<div id="{modalId}" class="fixed inset-0 bg-surface-container-lowest/90 z-50 hidden items-center justify-center p-space-lg"></div>""");
        }
        sb.Append("""
            <script>
                function switchInspectorTab(mode) {
                    const jsonBox = document.getElementById('json-editor-container');
                    const visBox = document.getElementById('visual-tree-container');
                    const bVis = document.getElementById('btn-toggle-visual');
                    const bJson = document.getElementById('btn-toggle-json');
                    if (mode === 'visual') {
                        jsonBox.classList.add('hidden');
                        visBox.classList.remove('hidden');
                        bVis.className = 'px-space-sm py-1 rounded font-label-code-sm bg-primary-container text-on-primary-container font-semibold';
                        bJson.className = 'px-space-sm py-1 rounded font-label-code-sm text-on-surface-variant hover:text-on-surface';
                    } else {
                        visBox.classList.add('hidden');
                        jsonBox.classList.remove('hidden');
                        bJson.className = 'px-space-sm py-1 rounded font-label-code-sm bg-primary-container text-on-primary-container font-semibold';
                        bVis.className = 'px-space-sm py-1 rounded font-label-code-sm text-on-surface-variant hover:text-on-surface';
                    }
                }

                function escMod(s) {
                    return String(s ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
                }

                function triggerRollback(snapId) {
                    document.getElementById('rollback-snapshot-id').value = snapId;
                    document.getElementById('form-rollback').submit();
                }

                async function loadConfigHistory() {
                    const el = document.getElementById('history-list');
                    if (!el) return;
                    el.innerHTML = 'Loading snapshots...';
                    try {
                        const res = await fetch('/api/history');
                        const items = await res.json();
                        if (!items || items.length === 0) {
                            el.innerHTML = '<div class="p-2 rounded bg-surface-container">No snapshots recorded yet. Any save or import creates an automatic snapshot in config/.history.</div>';
                            return;
                        }
                        el.innerHTML = items.map(s =>
                            '<div class="flex items-center justify-between p-2 rounded bg-surface-container border border-surface-variant">' +
                                '<div class="min-w-0">' +
                                    '<div class="font-semibold text-on-surface truncate">' + escMod(s.reason) + ' (' + escMod(s.resourceKind) + '/' + escMod(s.resourceId) + ')</div>' +
                                    '<div class="text-xs text-on-surface-variant font-mono">' + escMod(s.snapshotId) + ' • ' + escMod(s.createdAtUtc) + '</div>' +
                                '</div>' +
                                '<button type="button" onclick="triggerRollback(\'' + escMod(s.snapshotId).replace(/\'/g, '\\\'') + '\')" class="cyber-btn-secondary px-2.5 py-1 rounded shrink-0">↺ Restore</button>' +
                            '</div>'
                        ).join('');
                    } catch (e) {
                        el.innerHTML = '<div class="text-error">Failed to load snapshots: ' + escMod(e) + '</div>';
                    }
                }

                async function runPipelineSim() {
                    const out = document.getElementById('dbg-results');
                    if (!out) return;
                    out.innerHTML = 'Simulating pipeline...';
                    try {
                        const q = new URLSearchParams({
                            method: document.getElementById('dbg-method').value,
                            scheme: document.getElementById('dbg-scheme').value,
                            host: document.getElementById('dbg-host').value,
                            port: document.getElementById('dbg-port').value,
                            path: document.getElementById('dbg-path').value
                        });
                        const res = await fetch('/api/pipeline-simulate?' + q.toString());
                        const d = await res.json();
                        const stages = (d.stages || []).map(st =>
                            '<div class="flex justify-between py-0.5 border-b border-surface-variant/40"><span>' + escMod(st.stageName) + '</span><span class="text-primary-container">' + escMod(st.outcome) + '</span></div>'
                        ).join('');
                        out.innerHTML =
                            '<div class="mb-2 text-on-surface">' +
                                '<div><strong>Matched Site:</strong> ' + escMod(d.matchedSiteId || 'None') + ' | <strong>Route:</strong> ' + escMod(d.matchedRouteRuleId || '-') + '</div>' +
                                '<div><strong>Rewritten Path:</strong> ' + escMod(d.rewrittenPath) + ' | <strong>Target:</strong> ' + escMod(d.resolvedTarget || 'Static/Local') + '</div>' +
                                '<div><strong>Simulated Status:</strong> <span class="text-tertiary-container font-bold">' + escMod(d.simulatedStatusCode) + ' (' + escMod(d.finalOutcome) + ')</span></div>' +
                            '</div>' + stages;
                    } catch (e) {
                        out.innerHTML = '<div class="text-error">Simulation error: ' + escMod(e) + '</div>';
                    }
                }
            </script>
            """);
    }

    private static void RenderFeatureConfigModals(
        StringBuilder sb,
        string targetInputHtml,
        SiteFeaturesConfig f,
        IReadOnlyList<TlsCertificateChainStatus> certs,
        ServerConfig srv)
    {
        UpstreamClusterConfig? firstCluster = srv.Upstreams.FirstOrDefault();

        // Modal 1: URL Rewrite
        sb.Append($"""
            <div id="modal-rewrite" class="fixed inset-0 bg-surface-container-lowest/90 z-50 hidden items-center justify-center p-space-lg">
                <div class="obsidian-card p-space-lg rounded-xl shadow-2xl max-w-2xl w-full flex flex-col gap-space-md">
                    <div class="flex items-center justify-between border-b border-surface-variant pb-2">
                        <span class="font-headline-lg text-primary">URL Rewrite: Rule Pipeline Manager</span>
                        <button type="button" onclick="toggleModal('modal-rewrite', false)" class="text-on-surface-variant hover:text-on-surface">✕</button>
                    </div>
                    <form id="form-feature-rewrite" method="post" action="/actions/feature-rewrite" class="flex flex-col gap-space-sm font-label-code-sm">
                        {targetInputHtml}
                        <div class="grid grid-cols-2 gap-2">
                            <div><label>Pipeline Enabled</label><select name="rewriteEnabled" class="obsidian-input mt-1"><option value="true"{(f.UrlRewrite.Enabled ? " selected" : "")}>Enabled</option><option value="false"{(!f.UrlRewrite.Enabled ? " selected" : "")}>Disabled</option></select></div>
                            <div><label>Rule Action</label><select name="ruleActionType" class="obsidian-input mt-1"><option value="Redirect301">Redirect301</option><option value="Redirect302">Redirect302</option><option value="Rewrite">Rewrite</option><option value="CustomResponse">CustomResponse</option></select></div>
                            <div><label>Rule Name</label><input type="text" name="ruleName" value="LegacyToModern" class="obsidian-input mt-1" /></div>
                            <div><label>Require HTTP-Only (Non-HTTPS)</label><select name="requireHttps" class="obsidian-input mt-1"><option value="false">Always Match</option><option value="true">Only When Not HTTPS</option></select></div>
                            <div><label>Regex Pattern</label><input type="text" name="rulePattern" value="^/legacy/(.*)$" class="obsidian-input mt-1" /></div>
                            <div><label>Target Replacement</label><input type="text" name="ruleTarget" value="/v2/{"{R:1}"}" class="obsidian-input mt-1" /></div>
                        </div>
                        <div class="flex justify-between pt-2">
                            <button type="submit" name="clearAll" value="true" class="cyber-btn-danger px-3 py-1.5 rounded">Clear All Rules</button>
                            <div class="flex gap-2">
                                <button type="button" onclick="toggleModal('modal-rewrite', false)" class="cyber-btn-secondary px-3 py-1.5 rounded">Cancel</button>
                                <button type="submit" name="replaceExisting" value="true" class="cyber-btn-solid px-4 py-1.5 rounded">Add / Save Rule</button>
                            </div>
                        </div>
                    </form>
                </div>
            </div>
            """);

        // Modal 2: Redirects & Default Docs
        string customPagesText = string.Join("\n", f.RedirectsAndErrors.CustomErrorPages.Select(k => $"{k.Key}={k.Value}"));
        sb.Append($"""
            <div id="modal-redirects" class="fixed inset-0 bg-surface-container-lowest/90 z-50 hidden items-center justify-center p-space-lg">
                <div class="obsidian-card p-space-lg rounded-xl shadow-2xl max-w-lg w-full flex flex-col gap-space-md">
                    <div class="flex items-center justify-between border-b border-surface-variant pb-2">
                        <span class="font-headline-lg text-secondary">Redirect, Default Docs &amp; Custom Errors</span>
                        <button type="button" onclick="toggleModal('modal-redirects', false)" class="text-on-surface-variant hover:text-on-surface">✕</button>
                    </div>
                    <form id="form-feature-redirects" method="post" action="/actions/feature-redirects" class="flex flex-col gap-space-sm font-label-code-sm">
                        {targetInputHtml}
                        <div><label>Default Documents (comma-separated)</label><input type="text" name="defaultDocuments" value="{Enc(string.Join(", ", f.DefaultDocuments))}" class="obsidian-input mt-1" /></div>
                        <div class="grid grid-cols-2 gap-2">
                            <div><label>Directory Browsing</label><select name="directoryBrowsing" class="obsidian-input mt-1"><option value="false"{(!f.DirectoryBrowsing ? " selected" : "")}>Disabled</option><option value="true"{(f.DirectoryBrowsing ? " selected" : "")}>Enabled</option></select></div>
                            <div><label>Enforce Trailing Slash</label><select name="enforceTrailingSlash" class="obsidian-input mt-1"><option value="false"{(!f.RedirectsAndErrors.EnforceTrailingSlash ? " selected" : "")}>Disabled</option><option value="true"{(f.RedirectsAndErrors.EnforceTrailingSlash ? " selected" : "")}>Enforce 301</option></select></div>
                        </div>
                        <div><label>Error Mode</label><select name="errorMode" class="obsidian-input mt-1"><option value="PassThrough"{(f.RedirectsAndErrors.ErrorMode == CustomErrorMode.PassThrough ? " selected" : "")}>PassThrough (RFC 9457)</option><option value="CustomPages"{(f.RedirectsAndErrors.ErrorMode == CustomErrorMode.CustomPages ? " selected" : "")}>CustomPages</option></select></div>
                        <div><label>Global Redirect URL (optional)</label><input type="text" name="globalRedirectUrl" value="{Enc(f.RedirectsAndErrors.GlobalRedirectUrl ?? "")}" class="obsidian-input mt-1" /></div>
                        <div><label>Custom Error Pages (e.g. 404=/errors/404.html)</label><textarea name="customErrorPages" rows="3" class="obsidian-input mt-1">{Enc(customPagesText)}</textarea></div>
                        <div class="flex justify-end gap-2 pt-2">
                            <button type="button" onclick="toggleModal('modal-redirects', false)" class="cyber-btn-secondary px-3 py-1.5 rounded">Cancel</button>
                            <button type="submit" class="cyber-btn-solid px-4 py-1.5 rounded">Save Redirects</button>
                        </div>
                    </form>
                </div>
            </div>
            """);

        // Modal 3: MIME Mapping
        sb.Append($"""
            <div id="modal-mime" class="fixed inset-0 bg-surface-container-lowest/90 z-50 hidden items-center justify-center p-space-lg">
                <div class="obsidian-card p-space-lg rounded-xl shadow-2xl max-w-md w-full flex flex-col gap-space-md">
                    <div class="flex items-center justify-between border-b border-surface-variant pb-2">
                        <span class="font-headline-lg text-primary">MIME Type Mappings</span>
                        <button type="button" onclick="toggleModal('modal-mime', false)" class="text-on-surface-variant hover:text-on-surface">✕</button>
                    </div>
                    <form method="post" action="/actions/feature-mime" class="flex flex-col gap-space-sm font-label-code-sm">
                        {targetInputHtml}
                        <div><label>Fallback Content-Type</label><input type="text" name="fallbackContentType" value="{Enc(f.MimeMappings.FallbackContentType)}" class="obsidian-input mt-1" /></div>
                        <div class="grid grid-cols-2 gap-2">
                            <div><label>File Extension</label><input type="text" name="extension" placeholder=".webmanifest" class="obsidian-input mt-1" /></div>
                            <div><label>MIME Content-Type</label><input type="text" name="mimeType" placeholder="application/manifest+json" class="obsidian-input mt-1" /></div>
                        </div>
                        <div class="flex justify-end gap-2 pt-2">
                            <button type="button" onclick="toggleModal('modal-mime', false)" class="cyber-btn-secondary px-3 py-1.5 rounded">Cancel</button>
                            <button type="submit" class="cyber-btn-solid px-4 py-1.5 rounded">Save MIME Mapping</button>
                        </div>
                    </form>
                </div>
            </div>
            """);

        // Modal 4: Response Headers & CORS
        string customHeadersText = string.Join("\n", f.CustomHeaders.Select(k => $"{k.Key}: {k.Value}"));
        sb.Append($"""
            <div id="modal-headers" class="fixed inset-0 bg-surface-container-lowest/90 z-50 hidden items-center justify-center p-space-lg">
                <div class="obsidian-card p-space-lg rounded-xl shadow-2xl max-w-lg w-full flex flex-col gap-space-md">
                    <div class="flex items-center justify-between border-b border-surface-variant pb-2">
                        <span class="font-headline-lg text-primary">Response Headers &amp; CORS Policy</span>
                        <button type="button" onclick="toggleModal('modal-headers', false)" class="text-on-surface-variant hover:text-on-surface">✕</button>
                    </div>
                    <form method="post" action="/actions/feature-headers" class="flex flex-col gap-space-sm font-label-code-sm">
                        {targetInputHtml}
                        <div class="grid grid-cols-2 gap-2">
                            <div><label>Strip Server Header</label><select name="stripServerHeader" class="obsidian-input mt-1"><option value="true"{(f.StripServerHeader ? " selected" : "")}>True (Hardened)</option><option value="false"{(!f.StripServerHeader ? " selected" : "")}>False (Kite/2.0)</option></select></div>
                            <div><label>HSTS (RFC 6797)</label><select name="hstsEnabled" class="obsidian-input mt-1"><option value="false"{(!f.Hsts.Enabled ? " selected" : "")}>Disabled</option><option value="true"{(f.Hsts.Enabled ? " selected" : "")}>Enabled</option></select></div>
                            <div><label>CORS Policy</label><select name="corsEnabled" class="obsidian-input mt-1"><option value="false"{(!f.Cors.Enabled ? " selected" : "")}>Disabled</option><option value="true"{(f.Cors.Enabled ? " selected" : "")}>Enabled</option></select></div>
                            <div><label>CORS Allowed Origins</label><input type="text" name="corsAllowedOrigins" value="{Enc(string.Join(", ", f.Cors.AllowedOrigins))}" placeholder="https://app.example.com, *" class="obsidian-input mt-1" /></div>
                        </div>
                        <div><label>Remove Response Headers (comma-separated)</label><input type="text" name="removeHeaders" value="{Enc(string.Join(", ", f.RemoveHeaders))}" class="obsidian-input mt-1" /></div>
                        <div><label>Custom Headers (Name: Value per line)</label><textarea name="customHeadersText" rows="3" class="obsidian-input mt-1">{Enc(customHeadersText)}</textarea></div>
                        <div class="flex justify-end gap-2 pt-2">
                            <button type="button" onclick="toggleModal('modal-headers', false)" class="cyber-btn-secondary px-3 py-1.5 rounded">Cancel</button>
                            <button type="submit" class="cyber-btn-solid px-4 py-1.5 rounded">Save Headers &amp; CORS</button>
                        </div>
                    </form>
                </div>
            </div>
            """);

        // Modal 5: TLS Chain Inspector & Policy
        sb.Append($"""
            <div id="modal-tls" class="fixed inset-0 bg-surface-container-lowest/90 z-50 hidden items-center justify-center p-space-lg">
                <div class="obsidian-card p-space-lg rounded-xl shadow-2xl max-w-2xl w-full flex flex-col gap-space-md">
                    <div class="flex items-center justify-between border-b border-surface-variant pb-2">
                        <span class="font-headline-lg text-tertiary-container">X.509 Certificate Chain Inspector &amp; TLS Policy</span>
                        <button type="button" onclick="toggleModal('modal-tls', false)" class="text-on-surface-variant hover:text-on-surface">✕</button>
                    </div>
                    <div class="flex flex-col gap-1 font-label-code-sm max-h-44 overflow-y-auto bg-surface-container-lowest p-2 rounded border border-surface-variant">
            """);
        if (certs.Count == 0)
        {
            sb.Append("""<span class="text-on-surface-variant">No live HTTPS certificates published yet (start Kite.Server with an HTTPS binding to inspect live X.509 metadata).</span>""");
        }
        else
        {
            foreach (TlsCertificateChainStatus c in certs)
            {
                sb.Append($"""<div class="flex justify-between py-1 border-b border-surface-variant"><span class="text-primary">{Enc(c.Host)}:{c.Port} — {Enc(c.Subject)}</span><span class="text-tertiary-container">{c.Thumbprint[..Math.Min(12, c.Thumbprint.Length)]}... ({c.DaysRemaining}d left)</span></div>""");
            }
        }
        sb.Append($"""
                    </div>
                    <form method="post" action="/actions/feature-tls" class="flex flex-col gap-space-sm font-label-code-sm">
                        {targetInputHtml}
                        <div class="grid grid-cols-3 gap-2">
                            <div><label>Enforce TLS 1.3 Only</label><select name="enforceTls13" class="obsidian-input mt-1"><option value="false"{(!f.TlsPolicy.EnforceTls13 ? " selected" : "")}>TLS 1.2 + 1.3</option><option value="true"{(f.TlsPolicy.EnforceTls13 ? " selected" : "")}>TLS 1.3 Only</option></select></div>
                            <div><label>Strict SNI Routing</label><select name="strictSni" class="obsidian-input mt-1"><option value="true"{(f.TlsPolicy.StrictSni ? " selected" : "")}>Strict</option><option value="false"{(!f.TlsPolicy.StrictSni ? " selected" : "")}>Permissive</option></select></div>
                            <div><label>ACME HTTP-01 PassThrough</label><select name="enableAcmeHttp01" class="obsidian-input mt-1"><option value="true"{(f.TlsPolicy.EnableAcmeHttp01PassThrough ? " selected" : "")}>Enabled</option><option value="false"{(!f.TlsPolicy.EnableAcmeHttp01PassThrough ? " selected" : "")}>Disabled</option></select></div>
                        </div>
                        <div class="flex justify-end gap-2 pt-2">
                            <button type="button" onclick="toggleModal('modal-tls', false)" class="cyber-btn-secondary px-3 py-1.5 rounded">Close</button>
                            <button type="submit" class="cyber-btn-solid px-4 py-1.5 rounded">Save TLS Policy</button>
                        </div>
                    </form>
                </div>
            </div>
            """);

        // Modal 6: Edge Authentication
        sb.Append($"""
            <div id="modal-auth" class="fixed inset-0 bg-surface-container-lowest/90 z-50 hidden items-center justify-center p-space-lg">
                <div class="obsidian-card p-space-lg rounded-xl shadow-2xl max-w-2xl w-full flex flex-col gap-space-md">
                    <div class="flex items-center justify-between border-b border-surface-variant pb-2">
                        <span class="font-headline-lg text-primary">Edge Authentication Providers</span>
                        <button type="button" onclick="toggleModal('modal-auth', false)" class="text-on-surface-variant hover:text-on-surface">✕</button>
                    </div>
                    <form method="post" action="/actions/feature-auth" class="flex flex-col gap-space-sm font-label-code-sm">
                        {targetInputHtml}
                        <div class="grid grid-cols-3 gap-2">
                            <div><label>Anonymous Auth</label><select name="anonymousEnabled" class="obsidian-input mt-1"><option value="true"{(f.Authentication.AnonymousEnabled ? " selected" : "")}>Enabled</option><option value="false"{(!f.Authentication.AnonymousEnabled ? " selected" : "")}>Disabled (Require Auth)</option></select></div>
                            <div><label>JWT Bearer Validation</label><select name="jwtEnabled" class="obsidian-input mt-1"><option value="false"{(!f.Authentication.JwtEnabled ? " selected" : "")}>Disabled</option><option value="true"{(f.Authentication.JwtEnabled ? " selected" : "")}>Enabled</option></select></div>
                            <div><label>JWT Algorithm</label><select name="jwtAlgorithm" class="obsidian-input mt-1"><option value="HS256"{(f.Authentication.JwtSigningAlgorithm == "HS256" ? " selected" : "")}>HS256</option><option value="RS256"{(f.Authentication.JwtSigningAlgorithm == "RS256" ? " selected" : "")}>RS256</option></select></div>
                            <div><label>JWT Issuer (iss)</label><input type="text" name="jwtIssuer" value="{Enc(f.Authentication.JwtIssuer ?? "")}" class="obsidian-input mt-1" /></div>
                            <div><label>JWT Audience (aud)</label><input type="text" name="jwtAudience" value="{Enc(f.Authentication.JwtAudience ?? "")}" class="obsidian-input mt-1" /></div>
                            <div><label>JWT Key (Secret or PEM)</label><input type="text" name="jwtSigningKey" value="{Enc(f.Authentication.JwtSigningKey ?? "")}" class="obsidian-input mt-1" /></div>
                            <div><label>API Key Auth</label><select name="apiKeyEnabled" class="obsidian-input mt-1"><option value="false"{(!f.Authentication.ApiKeyEnabled ? " selected" : "")}>Disabled</option><option value="true"{(f.Authentication.ApiKeyEnabled ? " selected" : "")}>Enabled</option></select></div>
                            <div><label>API Key Header</label><input type="text" name="apiKeyHeaderName" value="{Enc(f.Authentication.ApiKeyHeaderName)}" class="obsidian-input mt-1" /></div>
                            <div><label>Allowed API Keys</label><input type="text" name="allowedApiKeys" value="{Enc(string.Join(", ", f.Authentication.AllowedApiKeys))}" class="obsidian-input mt-1" /></div>
                        </div>
                        <div class="grid grid-cols-2 gap-2">
                            <div><label>Basic Auth</label><select name="basicAuthEnabled" class="obsidian-input mt-1"><option value="false"{(!f.Authentication.BasicAuthEnabled ? " selected" : "")}>Disabled</option><option value="true"{(f.Authentication.BasicAuthEnabled ? " selected" : "")}>Enabled</option></select></div>
                            <div><label>Basic Users (user=sha256:hex or pass)</label><input type="text" name="basicAuthUsers" value="{Enc(string.Join(";", f.Authentication.BasicAuthUsers.Select(u => $"{u.Key}={u.Value}")))}" class="obsidian-input mt-1" /></div>
                        </div>
                        <div class="border-t border-surface-variant pt-2 grid grid-cols-3 gap-2">
                            <label class="flex items-center gap-1.5"><input type="checkbox" name="forwardAuthEnabled" value="true" {(f.ForwardAuth.Enabled ? "checked" : "")} /> ForwardAuth (ext_authz)</label>
                            <div><label>Verify Endpoint URL</label><input type="text" name="forwardAuthEndpointUrl" value="{Enc(f.ForwardAuth.EndpointUrl)}" placeholder="http://127.0.0.1:9001/verify" class="obsidian-input mt-1 font-mono" /></div>
                            <label class="flex items-center gap-1.5"><input type="checkbox" name="forwardAuthFailOpen" value="true" {(f.ForwardAuth.FailOpen ? "checked" : "")} /> Fail Open on Timeout</label>
                        </div>
                        <div><label>Copy Auth Response Headers</label><input type="text" name="forwardAuthCopyResponseHeaders" value="{Enc(string.Join(", ", f.ForwardAuth.CopyResponseHeaders))}" class="obsidian-input mt-1" /></div>
                        <div class="flex justify-end gap-2 pt-2">
                            <button type="button" onclick="toggleModal('modal-auth', false)" class="cyber-btn-secondary px-3 py-1.5 rounded">Cancel</button>
                            <button type="submit" class="cyber-btn-solid px-4 py-1.5 rounded">Save Auth Policy</button>
                        </div>
                    </form>
                </div>
            </div>
            """);

        // Modal 7: IP & Subnet Filters
        sb.Append($"""
            <div id="modal-ip" class="fixed inset-0 bg-surface-container-lowest/90 z-50 hidden items-center justify-center p-space-lg">
                <div class="obsidian-card p-space-lg rounded-xl shadow-2xl max-w-lg w-full flex flex-col gap-space-md">
                    <div class="flex items-center justify-between border-b border-surface-variant pb-2">
                        <span class="font-headline-lg text-tertiary-container">IP &amp; Subnet CIDR Restrictions</span>
                        <button type="button" onclick="toggleModal('modal-ip', false)" class="text-on-surface-variant hover:text-on-surface">✕</button>
                    </div>
                    <form id="form-feature-ip" method="post" action="/actions/feature-ip" class="flex flex-col gap-space-sm font-label-code-sm">
                        {targetInputHtml}
                        <div class="grid grid-cols-3 gap-2">
                            <div><label>IP Gate Enabled</label><select name="ipEnabled" class="obsidian-input mt-1"><option value="false"{(!f.IpRestrictions.Enabled ? " selected" : "")}>Disabled</option><option value="true"{(f.IpRestrictions.Enabled ? " selected" : "")}>Enabled</option></select></div>
                            <div><label>Unmatched IPs</label><select name="allowUnmatched" class="obsidian-input mt-1"><option value="true"{(f.IpRestrictions.AllowUnmatched ? " selected" : "")}>Allow</option><option value="false"{(!f.IpRestrictions.AllowUnmatched ? " selected" : "")}>Deny (Allowlist)</option></select></div>
                            <div><label>Deny Status</label><input type="number" name="denyStatusCode" value="{f.IpRestrictions.DenyStatusCode}" class="obsidian-input mt-1" /></div>
                        </div>
                        <div class="grid grid-cols-3 gap-2">
                            <div><label>Add CIDR Rule</label><input type="text" name="cidr" placeholder="10.240.0.0/16" class="obsidian-input mt-1" /></div>
                            <div><label>Action</label><select name="ruleAllow" class="obsidian-input mt-1"><option value="true">Allow</option><option value="false">Deny</option></select></div>
                            <div><label>Description</label><input type="text" name="description" value="Internal Subnet" class="obsidian-input mt-1" /></div>
                        </div>
                        <div class="flex justify-between pt-2">
                            <button type="submit" name="clearRules" value="true" class="cyber-btn-danger px-3 py-1.5 rounded">Clear Subnets</button>
                            <div class="flex gap-2">
                                <button type="button" onclick="toggleModal('modal-ip', false)" class="cyber-btn-secondary px-3 py-1.5 rounded">Cancel</button>
                                <button type="submit" class="cyber-btn-solid px-4 py-1.5 rounded">Save IP Restrictions</button>
                            </div>
                        </div>
                    </form>
                </div>
            </div>
            """);

        // Modal 8: Request Filtering
        sb.Append($"""
            <div id="modal-filtering" class="fixed inset-0 bg-surface-container-lowest/90 z-50 hidden items-center justify-center p-space-lg">
                <div class="obsidian-card p-space-lg rounded-xl shadow-2xl max-w-lg w-full flex flex-col gap-space-md">
                    <div class="flex items-center justify-between border-b border-surface-variant pb-2">
                        <span class="font-headline-lg text-primary">Request Filtering Gate</span>
                        <button type="button" onclick="toggleModal('modal-filtering', false)" class="text-on-surface-variant hover:text-on-surface">✕</button>
                    </div>
                    <form id="form-feature-filtering" method="post" action="/actions/feature-filtering" class="flex flex-col gap-space-sm font-label-code-sm">
                        {targetInputHtml}
                        <div class="grid grid-cols-2 gap-2">
                            <div><label>Filtering Enabled</label><select name="filteringEnabled" class="obsidian-input mt-1"><option value="true"{(f.RequestFiltering.Enabled ? " selected" : "")}>Enabled</option><option value="false"{(!f.RequestFiltering.Enabled ? " selected" : "")}>Disabled</option></select></div>
                            <div><label>Max Content Length (B)</label><input type="number" name="maxAllowedContentLength" value="{f.RequestFiltering.MaxAllowedContentLength}" class="obsidian-input mt-1" /></div>
                            <div><label>Max URL Length</label><input type="number" name="maxUrlLength" value="{f.RequestFiltering.MaxUrlLength}" class="obsidian-input mt-1" /></div>
                            <div><label>Max Query Length</label><input type="number" name="maxQueryStringLength" value="{f.RequestFiltering.MaxQueryStringLength}" class="obsidian-input mt-1" /></div>
                            <div><label>Double Escaping</label><select name="allowDoubleEscaping" class="obsidian-input mt-1"><option value="false"{(!f.RequestFiltering.AllowDoubleEscaping ? " selected" : "")}>Blocked (404.11)</option><option value="true"{(f.RequestFiltering.AllowDoubleEscaping ? " selected" : "")}>Allowed</option></select></div>
                            <div><label>High-Bit Characters</label><select name="allowHighBitCharacters" class="obsidian-input mt-1"><option value="true"{(f.RequestFiltering.AllowHighBitCharacters ? " selected" : "")}>Allowed</option><option value="false"{(!f.RequestFiltering.AllowHighBitCharacters ? " selected" : "")}>Blocked (400)</option></select></div>
                        </div>
                        <div><label>Allowed Verbs (comma-separated)</label><input type="text" name="allowedVerbs" value="{Enc(string.Join(", ", f.RequestFiltering.AllowedVerbs))}" class="obsidian-input mt-1" /></div>
                        <div><label>Blocked Extensions (comma-separated)</label><input type="text" name="blockedExtensions" value="{Enc(string.Join(", ", f.RequestFiltering.BlockedExtensions))}" class="obsidian-input mt-1" /></div>
                        <div class="flex justify-end gap-2 pt-2">
                            <button type="button" onclick="toggleModal('modal-filtering', false)" class="cyber-btn-secondary px-3 py-1.5 rounded">Cancel</button>
                            <button type="submit" class="cyber-btn-solid px-4 py-1.5 rounded">Save Filtering Gate</button>
                        </div>
                    </form>
                </div>
            </div>
            """);

        // Modal 9: Output Caching
        sb.Append($"""
            <div id="modal-cache" class="fixed inset-0 bg-surface-container-lowest/90 z-50 hidden items-center justify-center p-space-lg">
                <div class="obsidian-card p-space-lg rounded-xl shadow-2xl max-w-md w-full flex flex-col gap-space-md">
                    <div class="flex items-center justify-between border-b border-surface-variant pb-2">
                        <span class="font-headline-lg text-primary">Output Cache Policy</span>
                        <button type="button" onclick="toggleModal('modal-cache', false)" class="text-on-surface-variant hover:text-on-surface">✕</button>
                    </div>
                    <form method="post" action="/actions/feature-cache-flush" class="flex flex-col gap-space-sm font-label-code-sm">
                        {targetInputHtml}
                        <div><label>Cache Enabled</label><select name="cachingEnabled" class="obsidian-input mt-1"><option value="true"{(f.Caching.Enabled ? " selected" : "")}>Enabled</option><option value="false"{(!f.Caching.Enabled ? " selected" : "")}>Disabled</option></select></div>
                        <div class="grid grid-cols-2 gap-2">
                            <div><label>Default TTL (s)</label><input type="number" name="defaultMaxAgeSeconds" value="{f.Caching.DefaultMaxAgeSeconds}" class="obsidian-input mt-1" /></div>
                            <div><label>Max Total Size (MB)</label><input type="number" name="maxCacheSizeMegabytes" value="{f.Caching.MaxCacheSizeMegabytes}" class="obsidian-input mt-1" /></div>
                        </div>
                        <div><label>Max Cached Item (Bytes)</label><input type="number" name="maxCacheBodyBytes" value="{f.Caching.MaxCacheBodyBytes}" class="obsidian-input mt-1" /></div>
                        <div class="flex justify-end gap-2 pt-2">
                            <button type="button" onclick="toggleModal('modal-cache', false)" class="cyber-btn-secondary px-3 py-1.5 rounded">Cancel</button>
                            <button type="submit" class="cyber-btn-solid px-4 py-1.5 rounded">Save Cache Policy</button>
                        </div>
                    </form>
                </div>
            </div>
            """);

        // Modal 10: FREB
        sb.Append($"""
            <div id="modal-freb" class="fixed inset-0 bg-surface-container-lowest/90 z-50 hidden items-center justify-center p-space-lg">
                <div class="obsidian-card p-space-lg rounded-xl shadow-2xl max-w-md w-full flex flex-col gap-space-md">
                    <div class="flex items-center justify-between border-b border-surface-variant pb-2">
                        <span class="font-headline-lg text-secondary">FREB Tracing Triggers</span>
                        <button type="button" onclick="toggleModal('modal-freb', false)" class="text-on-surface-variant hover:text-on-surface">✕</button>
                    </div>
                    <form id="form-feature-freb" method="post" action="/actions/feature-freb" class="flex flex-col gap-space-sm font-label-code-sm">
                        {targetInputHtml}
                        <div><label>FREB Capture Enabled</label><select name="frebEnabled" class="obsidian-input mt-1"><option value="true"{(f.FailedRequestTracing.Enabled ? " selected" : "")}>Enabled</option><option value="false"{(!f.FailedRequestTracing.Enabled ? " selected" : "")}>Disabled</option></select></div>
                        <div><label>Status Code Trigger (e.g. 500-599 or 403,500)</label><input type="text" name="statusCodeTrigger" value="{Enc(f.FailedRequestTracing.StatusCodeTrigger)}" class="obsidian-input mt-1" /></div>
                        <div class="grid grid-cols-2 gap-2">
                            <div><label>Latency Cutoff (ms)</label><input type="number" name="latencyCutoffMilliseconds" value="{f.FailedRequestTracing.LatencyCutoffMilliseconds}" class="obsidian-input mt-1" /></div>
                            <div><label>Max Retained Traces</label><input type="number" name="maxRetainedTraces" value="{f.FailedRequestTracing.MaxRetainedTraces}" class="obsidian-input mt-1" /></div>
                        </div>
                        <div class="flex justify-end gap-2 pt-2">
                            <button type="button" onclick="toggleModal('modal-freb', false)" class="cyber-btn-secondary px-3 py-1.5 rounded">Cancel</button>
                            <button type="submit" class="cyber-btn-solid px-4 py-1.5 rounded">Save FREB Rules</button>
                        </div>
                    </form>
                </div>
            </div>
            """);

        // Modal 11: Compression
        sb.Append($"""
            <div id="modal-compression" class="fixed inset-0 bg-surface-container-lowest/90 z-50 hidden items-center justify-center p-space-lg">
                <div class="obsidian-card p-space-lg rounded-xl shadow-2xl max-w-md w-full flex flex-col gap-space-md">
                    <div class="flex items-center justify-between border-b border-surface-variant pb-2">
                        <span class="font-headline-lg text-primary">Content Compression Schemes</span>
                        <button type="button" onclick="toggleModal('modal-compression', false)" class="text-on-surface-variant hover:text-on-surface">✕</button>
                    </div>
                    <form method="post" action="/actions/feature-compression" class="flex flex-col gap-space-sm font-label-code-sm">
                        {targetInputHtml}
                        <div><label>Compression Enabled</label><select name="compressionEnabled" class="obsidian-input mt-1"><option value="true"{(f.Compression.Enabled ? " selected" : "")}>Enabled</option><option value="false"{(!f.Compression.Enabled ? " selected" : "")}>Disabled</option></select></div>
                        <div class="grid grid-cols-2 gap-2">
                            <div><label>Min Size (Bytes)</label><input type="number" name="minSizeBytes" value="{f.Compression.MinSizeBytes}" class="obsidian-input mt-1" /></div>
                            <div><label>Brotli Level (0-11)</label><input type="number" name="brotliLevel" value="{f.Compression.BrotliLevel}" min="0" max="11" class="obsidian-input mt-1" /></div>
                            <div><label>Gzip Level (0-9)</label><input type="number" name="gzipLevel" value="{f.Compression.GzipLevel}" min="0" max="9" class="obsidian-input mt-1" /></div>
                            <div><label>Zstd Level (0-19)</label><input type="number" name="zstdLevel" value="{f.Compression.ZstdLevel}" min="0" max="19" class="obsidian-input mt-1" /></div>
                        </div>
                        <div class="flex justify-end gap-2 pt-2">
                            <button type="button" onclick="toggleModal('modal-compression', false)" class="cyber-btn-secondary px-3 py-1.5 rounded">Cancel</button>
                            <button type="submit" class="cyber-btn-solid px-4 py-1.5 rounded">Save Compression</button>
                        </div>
                    </form>
                </div>
            </div>

            <!-- Modal 12: Edge Rate Limiting -->
            <div id="modal-ratelimit" class="fixed inset-0 bg-surface-container-lowest/90 z-50 hidden items-center justify-center p-space-lg">
                <div class="obsidian-card p-space-lg rounded-xl shadow-2xl max-w-lg w-full flex flex-col gap-space-md">
                    <div class="flex items-center justify-between border-b border-surface-variant pb-2">
                        <h3 class="font-headline-sm text-primary font-semibold">Edge Rate Limiting</h3>
                        <button type="button" onclick="toggleModal('modal-ratelimit', false)" class="text-on-surface-variant hover:text-on-surface">✕</button>
                    </div>
                    <form id="form-feature-ratelimit" method="post" action="/actions/feature-ratelimit" class="flex flex-col gap-space-sm font-label-code-sm">
                        {targetInputHtml}
                        <label class="flex items-center gap-2"><input type="checkbox" name="rateLimitEnabled" value="true" {(f.RateLimiting.Enabled ? "checked" : "")} /> Enable Partitioned Edge Rate Limiting</label>
                        <div>
                            <label>Algorithm</label>
                            <select name="algorithm" class="obsidian-input mt-1">
                                <option value="SlidingWindow" {(f.RateLimiting.Algorithm == RateLimitAlgorithm.SlidingWindow ? "selected" : "")}>SlidingWindow</option>
                                <option value="FixedWindow" {(f.RateLimiting.Algorithm == RateLimitAlgorithm.FixedWindow ? "selected" : "")}>FixedWindow</option>
                                <option value="TokenBucket" {(f.RateLimiting.Algorithm == RateLimitAlgorithm.TokenBucket ? "selected" : "")}>TokenBucket</option>
                                <option value="Concurrency" {(f.RateLimiting.Algorithm == RateLimitAlgorithm.Concurrency ? "selected" : "")}>Concurrency</option>
                            </select>
                        </div>
                        <div class="grid grid-cols-3 gap-2">
                            <div><label>Permit Limit</label><input type="number" name="permitLimit" value="{f.RateLimiting.PermitLimit}" min="1" class="obsidian-input mt-1" /></div>
                            <div><label>Window (s)</label><input type="number" name="windowSeconds" value="{f.RateLimiting.WindowSeconds}" min="1" class="obsidian-input mt-1" /></div>
                            <div><label>Retry-After (s)</label><input type="number" name="retryAfterSeconds" value="{f.RateLimiting.RetryAfterSeconds}" min="1" class="obsidian-input mt-1" /></div>
                        </div>
                        <div><label>Partition By Header (blank = Client IP)</label><input type="text" name="partitionByHeader" value="{Enc(f.RateLimiting.PartitionByHeader)}" placeholder="X-API-Key" class="obsidian-input mt-1" /></div>
                        <div class="flex justify-end gap-2 pt-2">
                            <button type="button" onclick="toggleModal('modal-ratelimit', false)" class="cyber-btn-secondary px-3 py-1.5 rounded">Cancel</button>
                            <button type="submit" class="cyber-btn-solid px-4 py-1.5 rounded">Save Rate Limit</button>
                        </div>
                    </form>
                </div>
            </div>

            <!-- Modal 13: ACME Auto-HTTPS, mTLS & Early Hints -->
            <div id="modal-acme-mtls" class="fixed inset-0 bg-surface-container-lowest/90 z-50 hidden items-center justify-center p-space-lg">
                <div class="obsidian-card p-space-lg rounded-xl shadow-2xl max-w-xl w-full flex flex-col gap-space-md">
                    <div class="flex items-center justify-between border-b border-surface-variant pb-2">
                        <h3 class="font-headline-sm text-primary font-semibold">Auto-HTTPS (ACME v2), mTLS &amp; 103 Early Hints</h3>
                        <button type="button" onclick="toggleModal('modal-acme-mtls', false)" class="text-on-surface-variant hover:text-on-surface">✕</button>
                    </div>
                    <form id="form-feature-acme-mtls" method="post" action="/actions/feature-acme-mtls" class="flex flex-col gap-space-sm font-label-code-sm">
                        {targetInputHtml}
                        <div class="grid grid-cols-2 gap-2">
                            <label class="flex items-center gap-2"><input type="checkbox" name="acmeEnabled" value="true" {(f.TlsPolicy.Acme.Enabled ? "checked" : "")} /> Enable Automatic ACME v2</label>
                            <label class="flex items-center gap-2"><input type="checkbox" name="acceptTermsOfService" value="true" {(f.TlsPolicy.Acme.AcceptTermsOfService ? "checked" : "")} /> Accept CA Terms of Service</label>
                        </div>
                        <div class="grid grid-cols-3 gap-2">
                            <label class="flex items-center gap-1.5"><input type="checkbox" name="redirectHttpToHttps" value="true" {(f.TlsPolicy.RedirectHttpToHttps ? "checked" : "")} /> Redirect HTTP → HTTPS</label>
                            <div><label>Redirect Code</label><select name="redirectHttpToHttpsStatusCode" class="obsidian-input mt-1"><option value="308" {(f.TlsPolicy.RedirectHttpToHttpsStatusCode == 308 ? "selected" : "")}>308 Permanent</option><option value="301" {(f.TlsPolicy.RedirectHttpToHttpsStatusCode == 301 ? "selected" : "")}>301 Moved</option></select></div>
                            <label class="flex items-center gap-1.5"><input type="checkbox" name="onDemandTlsEnabled" value="true" {(f.TlsPolicy.OnDemandTls.Enabled ? "checked" : "")} /> On-Demand TLS</label>
                        </div>
                        <div class="grid grid-cols-3 gap-2">
                            <div><label>Contact Email</label><input type="email" name="contactEmail" value="{Enc(f.TlsPolicy.Acme.ContactEmail)}" placeholder="ops@example.com" class="obsidian-input mt-1" /></div>
                            <div>
                                <label>ACME Challenge</label>
                                <select name="challengeType" class="obsidian-input mt-1">
                                    <option value="Http01" {(f.TlsPolicy.Acme.ChallengeType == AcmeChallengeType.Http01 ? "selected" : "")}>Http01 (Port 80)</option>
                                    <option value="TlsAlpn01" {(f.TlsPolicy.Acme.ChallengeType == AcmeChallengeType.TlsAlpn01 ? "selected" : "")}>TlsAlpn01 (acme-tls/1)</option>
                                    <option value="Dns01" {(f.TlsPolicy.Acme.ChallengeType == AcmeChallengeType.Dns01 ? "selected" : "")}>Dns01 (Webhook TXT)</option>
                                </select>
                            </div>
                            <div><label>Renew Before (Days)</label><input type="number" name="renewBeforeDays" value="{f.TlsPolicy.Acme.RenewBeforeDays}" min="1" max="89" class="obsidian-input mt-1" /></div>
                        </div>
                        <div class="grid grid-cols-2 gap-2">
                            <div><label>DNS-01 Webhook URL</label><input type="text" name="dnsWebhookUrl" value="{Enc(f.TlsPolicy.Acme.DnsWebhookUrl ?? "")}" placeholder="https://dns.internal/txt" class="obsidian-input mt-1 font-mono" /></div>
                            <div><label>DNS Webhook Bearer Token</label><input type="text" name="dnsWebhookAuthToken" value="{Enc(f.TlsPolicy.Acme.DnsWebhookAuthToken ?? "")}" class="obsidian-input mt-1 font-mono" /></div>
                        </div>
                        <div><label>ACME Directory URL</label><input type="text" name="directoryUrl" value="{Enc(f.TlsPolicy.Acme.DirectoryUrl)}" class="obsidian-input mt-1 font-mono" /></div>
                        <div class="grid grid-cols-2 gap-2">
                            <div><label>ACME Domains (comma separated)</label><input type="text" name="acmeDomains" value="{Enc(string.Join(", ", f.TlsPolicy.Acme.Domains))}" placeholder="example.com, www.example.com" class="obsidian-input mt-1" /></div>
                            <div><label>On-Demand Allowed Host Suffixes</label><input type="text" name="allowedHostSuffixes" value="{Enc(string.Join(", ", f.TlsPolicy.OnDemandTls.AllowedHostSuffixes))}" placeholder=".example.com" class="obsidian-input mt-1" /></div>
                        </div>
                        <div class="grid grid-cols-2 gap-2">
                            <div>
                                <label>mTLS Client Certificate Mode</label>
                                <select name="mtlsMode" class="obsidian-input mt-1">
                                    <option value="None" {(f.TlsPolicy.Mtls.ClientCertificateMode == MtlsClientCertificateMode.None ? "selected" : "")}>None</option>
                                    <option value="AllowCertificate" {(f.TlsPolicy.Mtls.ClientCertificateMode == MtlsClientCertificateMode.AllowCertificate ? "selected" : "")}>AllowCertificate</option>
                                    <option value="RequireCertificate" {(f.TlsPolicy.Mtls.ClientCertificateMode == MtlsClientCertificateMode.RequireCertificate ? "selected" : "")}>RequireCertificate</option>
                                </select>
                            </div>
                            <div><label>Trusted Thumbprints (comma sep.)</label><input type="text" name="trustedThumbprints" value="{Enc(string.Join(", ", f.TlsPolicy.Mtls.TrustedThumbprints))}" class="obsidian-input mt-1" /></div>
                        </div>
                        <div><label>103 Early Hints Preload Links (; separated)</label><input type="text" name="earlyHintsLinks" value="{Enc(string.Join("; ", f.EarlyHintsLinks))}" placeholder="&lt;/app.css&gt;; rel=preload; as=style" class="obsidian-input mt-1" /></div>
                        <div class="flex justify-end gap-2 pt-2">
                            <button type="button" onclick="toggleModal('modal-acme-mtls', false)" class="cyber-btn-secondary px-3 py-1.5 rounded">Cancel</button>
                            <button type="submit" class="cyber-btn-solid px-4 py-1.5 rounded">Save ACME &amp; mTLS</button>
                        </div>
                    </form>
                </div>
            </div>

            <!-- Modal 14: HTTP/3 Transport & Observability -->
            <div id="modal-transport-obs" class="fixed inset-0 bg-surface-container-lowest/90 z-50 hidden items-center justify-center p-space-lg">
                <div class="obsidian-card p-space-lg rounded-xl shadow-2xl max-w-lg w-full flex flex-col gap-space-md">
                    <div class="flex items-center justify-between border-b border-surface-variant pb-2">
                        <h3 class="font-headline-sm text-primary font-semibold">HTTP/3 Transport &amp; Cloud Observability</h3>
                        <button type="button" onclick="toggleModal('modal-transport-obs', false)" class="text-on-surface-variant hover:text-on-surface">✕</button>
                    </div>
                    <form id="form-feature-transport-obs" method="post" action="/actions/feature-transport-obs" class="flex flex-col gap-space-sm font-label-code-sm">
                        {targetInputHtml}
                        <div class="grid grid-cols-2 gap-2">
                            <label class="flex items-center gap-2"><input type="checkbox" name="enableHttp3" value="true" {(srv.Transport.EnableHttp3 ? "checked" : "")} /> Enable HTTP/3 (QUIC) + Alt-Svc</label>
                            <label class="flex items-center gap-2"><input type="checkbox" name="enableQuicZeroRtt" value="true" {(srv.Transport.EnableQuicZeroRtt ? "checked" : "")} /> Enable QUIC 0-RTT Early Data</label>
                        </div>
                        <div class="grid grid-cols-2 gap-2">
                            <div><label>Graceful Drain Timeout (s)</label><input type="number" name="gracefulDrainTimeoutSeconds" value="{srv.Transport.GracefulDrainTimeoutSeconds}" min="1" max="120" class="obsidian-input mt-1" /></div>
                            <div><label>Slowloris Min Data Rate (B/s)</label><input type="number" name="minRequestBodyDataRateBytesPerSecond" value="{srv.Transport.MinRequestBodyDataRateBytesPerSecond}" min="0" class="obsidian-input mt-1" /></div>
                        </div>
                        <label class="flex items-center gap-2"><input type="checkbox" name="enableOpenTelemetryTracing" value="true" {(srv.Observability.EnableOpenTelemetryTracing ? "checked" : "")} /> Enable W3C ActivitySource Distributed Tracing</label>
                        <label class="flex items-center gap-2"><input type="checkbox" name="enablePrometheusMetrics" value="true" {(srv.Observability.EnablePrometheusMetrics ? "checked" : "")} /> Enable Prometheus Endpoint ({Enc(srv.Observability.MetricsPath)})</label>
                        <label class="flex items-center gap-2"><input type="checkbox" name="enableHealthEndpoints" value="true" {(srv.Observability.EnableHealthEndpoints ? "checked" : "")} /> Enable Health Probes ({Enc(srv.Observability.HealthEndpointPath)} / {Enc(srv.Observability.ReadyEndpointPath)})</label>
                        <div class="border-t border-surface-variant pt-2 grid grid-cols-3 gap-2">
                            <label class="flex items-center gap-1.5"><input type="checkbox" name="otlpEnabled" value="true" {(srv.Observability.Otlp.Enabled ? "checked" : "")} /> OTLP HTTP/JSON</label>
                            <div><label>OTLP Endpoint URL</label><input type="text" name="otlpEndpointUrl" value="{Enc(srv.Observability.Otlp.EndpointUrl)}" class="obsidian-input mt-1 font-mono" /></div>
                            <div><label>Service Name</label><input type="text" name="otlpServiceName" value="{Enc(srv.Observability.Otlp.ServiceName)}" class="obsidian-input mt-1" /></div>
                        </div>
                        <div class="grid grid-cols-3 gap-2">
                            <label class="flex items-center gap-1.5"><input type="checkbox" name="redisEnabled" value="true" {(srv.DistributedState.Enabled ? "checked" : "")} /> Redis Shared State</label>
                            <div><label>Redis Host</label><input type="text" name="redisHost" value="{Enc(srv.DistributedState.RedisHost)}" class="obsidian-input mt-1 font-mono" /></div>
                            <div><label>Redis Port</label><input type="number" name="redisPort" value="{srv.DistributedState.RedisPort}" min="1" max="65535" class="obsidian-input mt-1" /></div>
                        </div>
                        <div>
                            <label>Access Log Format</label>
                            <select name="accessLogFormat" class="obsidian-input mt-1">
                                <option value="W3C" {(srv.Observability.AccessLogFormat == AccessLogFormat.W3C ? "selected" : "")}>W3C</option>
                                <option value="Json" {(srv.Observability.AccessLogFormat == AccessLogFormat.Json ? "selected" : "")}>Json (NDJSON)</option>
                                <option value="Both" {(srv.Observability.AccessLogFormat == AccessLogFormat.Both ? "selected" : "")}>Both</option>
                            </select>
                        </div>
                        <div class="flex justify-end gap-2 pt-2">
                            <button type="button" onclick="toggleModal('modal-transport-obs', false)" class="cyber-btn-secondary px-3 py-1.5 rounded">Cancel</button>
                            <button type="submit" class="cyber-btn-solid px-4 py-1.5 rounded">Save Transport &amp; OTel</button>
                        </div>
                    </form>
                </div>
            </div>

            <!-- Modal 15: Upstream Clusters -->
            <div id="modal-upstreams" class="fixed inset-0 bg-surface-container-lowest/90 z-50 hidden items-center justify-center p-space-lg">
                <div class="obsidian-card p-space-lg rounded-xl shadow-2xl max-w-xl w-full flex flex-col gap-space-md">
                    <div class="flex items-center justify-between border-b border-surface-variant pb-2">
                        <h3 class="font-headline-sm text-primary font-semibold">Upstream Reverse Proxy Clusters</h3>
                        <button type="button" onclick="toggleModal('modal-upstreams', false)" class="text-on-surface-variant hover:text-on-surface">✕</button>
                    </div>
                    {(srv.Upstreams.Count > 0 ? $"<div class=\"flex flex-wrap gap-1.5 font-label-code-sm\"><span class=\"text-on-surface-variant\">Configured:</span>{string.Join("", srv.Upstreams.Select(u => $"<button type=\"button\" onclick=\"document.querySelector('#form-feature-upstreams input[name=clusterId]').value='{EncJs(u.Id)}'\" class=\"px-2 py-0.5 rounded bg-surface-container-high text-primary hover:text-primary-container\">{Enc(u.Id)} ({u.Destinations.Count})</button>"))}</div>" : "")}
                    <form id="form-feature-upstreams" method="post" action="/actions/feature-upstreams" class="flex flex-col gap-space-sm font-label-code-sm">
                        {targetInputHtml}
                        <div class="grid grid-cols-2 gap-2">
                            <div><label>Cluster ID</label><input type="text" name="clusterId" value="{Enc(firstCluster?.Id ?? "api-cluster")}" class="obsidian-input mt-1" /></div>
                            <div>
                                <label>Load Balancing Policy</label>
                                <select name="loadBalancingPolicy" class="obsidian-input mt-1">
                                    <option value="PowerOfTwoChoices" {(firstCluster?.LoadBalancingPolicy == LoadBalancingPolicy.PowerOfTwoChoices ? "selected" : "")}>PowerOfTwoChoices</option>
                                    <option value="RoundRobin" {(firstCluster?.LoadBalancingPolicy == LoadBalancingPolicy.RoundRobin ? "selected" : "")}>RoundRobin</option>
                                    <option value="LeastRequests" {(firstCluster?.LoadBalancingPolicy == LoadBalancingPolicy.LeastRequests ? "selected" : "")}>LeastRequests</option>
                                    <option value="ConsistentHash" {(firstCluster?.LoadBalancingPolicy == LoadBalancingPolicy.ConsistentHash ? "selected" : "")}>ConsistentHash</option>
                                </select>
                            </div>
                        </div>
                        <div><label>Destination URLs (comma separated)</label><input type="text" name="destinationAddresses" value="{Enc(string.Join(", ", firstCluster?.Destinations.Select(d => d.Address) ?? []))}" placeholder="http://127.0.0.1:5001, http://127.0.0.1:5002" class="obsidian-input mt-1 font-mono" /></div>
                        <div class="grid grid-cols-3 gap-2">
                            <div><label>Retries</label><input type="number" name="retryCount" value="{firstCluster?.RetryCount ?? 2}" min="0" max="5" class="obsidian-input mt-1" /></div>
                            <div><label>Request Timeout (s)</label><input type="number" name="requestTimeoutSeconds" value="{firstCluster?.RequestTimeoutSeconds ?? 30}" min="1" max="600" class="obsidian-input mt-1" /></div>
                            <div><label>Sticky Header/Cookie</label><input type="text" name="stickyHeaderOrCookie" value="{Enc(firstCluster?.StickyHeaderOrCookie ?? "")}" placeholder="X-Session-Id" class="obsidian-input mt-1" /></div>
                        </div>
                        <div class="grid grid-cols-2 gap-2">
                            <div><label>Health Probe Path</label><input type="text" name="activeHealthCheckPath" value="{Enc(firstCluster?.ActiveHealthCheck.Path ?? "/healthz")}" class="obsidian-input mt-1 font-mono" /></div>
                            <div><label>Probe Interval (s)</label><input type="number" name="activeHealthCheckIntervalSeconds" value="{firstCluster?.ActiveHealthCheck.IntervalSeconds ?? 10}" min="2" max="300" class="obsidian-input mt-1" /></div>
                        </div>
                        <div class="grid grid-cols-2 gap-2">
                            <div><label>Circuit Breaker Failures</label><input type="number" name="passiveFailureThreshold" value="{firstCluster?.PassiveCircuitBreaker.FailureThreshold ?? 5}" min="1" max="100" class="obsidian-input mt-1" /></div>
                            <div><label>Ejection Window (s)</label><input type="number" name="passiveEjectionSeconds" value="{firstCluster?.PassiveCircuitBreaker.EjectionSeconds ?? 30}" min="5" max="600" class="obsidian-input mt-1" /></div>
                        </div>
                        <label class="flex items-center gap-2"><input type="checkbox" name="activeHealthCheckEnabled" value="true" {(firstCluster?.ActiveHealthCheck.Enabled ?? true ? "checked" : "")} /> Enable Active Health Probes</label>
                        <div class="flex justify-between gap-2 pt-2">
                            <button type="submit" name="removeCluster" value="true" class="px-3 py-1.5 rounded bg-error-container/20 hover:bg-error-container text-error">Remove Cluster</button>
                            <div class="flex gap-2">
                                <button type="button" onclick="toggleModal('modal-upstreams', false)" class="cyber-btn-secondary px-3 py-1.5 rounded">Cancel</button>
                                <button type="submit" class="cyber-btn-solid px-4 py-1.5 rounded">Save Cluster</button>
                            </div>
                        </div>
                    </form>
                </div>
            </div>

            <!-- Modal 16: SPA Fallback & Edge WAF -->
            <div id="modal-spawaf" class="fixed inset-0 bg-surface-container-lowest/90 z-50 hidden items-center justify-center p-space-lg">
                <div class="obsidian-card p-space-lg rounded-xl shadow-2xl max-w-lg w-full flex flex-col gap-space-md">
                    <div class="flex items-center justify-between border-b border-surface-variant pb-2">
                        <h3 class="font-headline-sm text-primary font-semibold">SPA History Fallback &amp; Edge WAF</h3>
                        <button type="button" onclick="toggleModal('modal-spawaf', false)" class="text-on-surface-variant hover:text-on-surface">✕</button>
                    </div>
                    <form id="form-feature-spawaf" method="post" action="/actions/feature-spawaf" class="flex flex-col gap-space-sm font-label-code-sm">
                        {targetInputHtml}
                        <label class="flex items-center gap-2"><input type="checkbox" name="spaEnabled" value="true" {(f.SpaFallback.Enabled ? "checked" : "")} /> Enable SPA Fallback Rewrite</label>
                        <div class="grid grid-cols-2 gap-2">
                            <div><label>Fallback Document</label><input type="text" name="fallbackDocument" value="{Enc(f.SpaFallback.FallbackDocument)}" class="obsidian-input mt-1" /></div>
                            <div><label>Exclude Prefixes (comma sep.)</label><input type="text" name="excludePrefixes" value="{Enc(string.Join(", ", f.SpaFallback.ExcludePrefixes))}" class="obsidian-input mt-1 font-mono" /></div>
                        </div>
                        <label class="flex items-center gap-2"><input type="checkbox" name="wafEnabled" value="true" {(f.EdgeWaf.Enabled ? "checked" : "")} /> Enable Zero-Alloc Edge WAF</label>
                        <div class="grid grid-cols-3 gap-2">
                            <label class="flex items-center gap-1"><input type="checkbox" name="blockSqlInjection" value="true" {(f.EdgeWaf.BlockSqlInjection ? "checked" : "")} /> Block SQLi</label>
                            <label class="flex items-center gap-1"><input type="checkbox" name="blockXss" value="true" {(f.EdgeWaf.BlockXss ? "checked" : "")} /> Block XSS</label>
                            <label class="flex items-center gap-1"><input type="checkbox" name="blockPathTraversal" value="true" {(f.EdgeWaf.BlockPathTraversal ? "checked" : "")} /> Block Traversal</label>
                        </div>
                        <div class="grid grid-cols-3 gap-2">
                            <label class="flex items-center gap-1"><input type="checkbox" name="inspectRequestBody" value="true" {(f.EdgeWaf.InspectRequestBody ? "checked" : "")} /> Inspect Request Body</label>
                            <div><label>Max Inspect (B)</label><input type="number" name="maxBodyInspectBytes" value="{f.EdgeWaf.MaxBodyInspectBytes}" min="1024" class="obsidian-input mt-1" /></div>
                            <div><label>Max JSON Depth</label><input type="number" name="maxJsonDepth" value="{f.EdgeWaf.MaxJsonDepth}" min="0" max="256" class="obsidian-input mt-1" /></div>
                        </div>
                        <div><label>Blocked User-Agents (comma separated)</label><input type="text" name="blockedUserAgents" value="{Enc(string.Join(", ", f.EdgeWaf.BlockedUserAgents))}" placeholder="sqlmap, nikto" class="obsidian-input mt-1" /></div>
                        <div><label>Custom Blocked URI/Query Patterns (comma sep.)</label><input type="text" name="customBlockedPatterns" value="{Enc(string.Join(", ", f.EdgeWaf.CustomBlockedPatterns))}" placeholder=".git/config, /wp-admin" class="obsidian-input mt-1 font-mono" /></div>
                        <div class="flex justify-end gap-2 pt-2">
                            <button type="button" onclick="toggleModal('modal-spawaf', false)" class="cyber-btn-secondary px-3 py-1.5 rounded">Cancel</button>
                            <button type="submit" class="cyber-btn-solid px-4 py-1.5 rounded">Save WAF &amp; SPA</button>
                        </div>
                    </form>
                </div>
            </div>

            <!-- Modal 17: L4 TCP & TLS SNI Passthrough Streams -->
            <div id="modal-l4streams" class="fixed inset-0 bg-surface-container-lowest/90 z-50 hidden items-center justify-center p-space-lg">
                <div class="obsidian-card p-space-lg rounded-xl shadow-2xl max-w-xl w-full flex flex-col gap-space-md">
                    <div class="flex items-center justify-between border-b border-surface-variant pb-2">
                        <h3 class="font-headline-sm text-primary font-semibold">L4 TCP &amp; TLS SNI Passthrough Streams</h3>
                        <button type="button" onclick="toggleModal('modal-l4streams', false)" class="text-on-surface-variant hover:text-on-surface">✕</button>
                    </div>
                    <form id="form-feature-l4streams" method="post" action="/actions/feature-l4streams" class="flex flex-col gap-space-sm font-label-code-sm">
                        {targetInputHtml}
                        <div class="grid grid-cols-3 gap-2">
                            <div><label>Stream ID</label><input type="text" name="streamId" value="{Enc(srv.TcpStreams.FirstOrDefault()?.Id ?? "tcp-stream-1")}" class="obsidian-input mt-1" /></div>
                            <div><label>Listen Port</label><input type="number" name="listenPort" value="{srv.TcpStreams.FirstOrDefault()?.ListenPort ?? 9443}" min="1" max="65535" class="obsidian-input mt-1" /></div>
                            <label class="flex items-center gap-1.5"><input type="checkbox" name="enableTlsSniRouting" value="true" {(srv.TcpStreams.FirstOrDefault()?.EnableTlsSniRouting ?? true ? "checked" : "")} /> TLS ClientHello SNI</label>
                        </div>
                        <div class="grid grid-cols-2 gap-2">
                            <div><label>Default Upstream Host</label><input type="text" name="defaultUpstreamHost" value="{Enc(srv.TcpStreams.FirstOrDefault()?.DefaultUpstreamHost ?? "127.0.0.1")}" class="obsidian-input mt-1 font-mono" /></div>
                            <div><label>Default Upstream Port</label><input type="number" name="defaultUpstreamPort" value="{srv.TcpStreams.FirstOrDefault()?.DefaultUpstreamPort ?? 5432}" min="1" max="65535" class="obsidian-input mt-1" /></div>
                        </div>
                        <div><label>SNI Routes (sni=host:port, comma/line sep.)</label><input type="text" name="sniRoutesRaw" value="{Enc(string.Join(", ", srv.TcpStreams.FirstOrDefault()?.SniRoutes.Select(r => $"{r.SniHostname}={r.UpstreamHost}:{r.UpstreamPort}") ?? []))}" placeholder="api.local=127.0.0.1:8444" class="obsidian-input mt-1 font-mono" /></div>
                        <div class="flex justify-between gap-2 pt-2">
                            <button type="submit" name="removeStream" value="true" class="px-3 py-1.5 rounded bg-error-container/20 hover:bg-error-container text-error">Remove Stream</button>
                            <div class="flex gap-2">
                                <button type="button" onclick="toggleModal('modal-l4streams', false)" class="cyber-btn-secondary px-3 py-1.5 rounded">Cancel</button>
                                <button type="submit" class="cyber-btn-solid px-4 py-1.5 rounded">Save L4 Stream</button>
                            </div>
                        </div>
                    </form>
                </div>
            </div>

            <!-- Modal 18: Config Snapshots History & Rollback -->
            <div id="modal-history" class="fixed inset-0 bg-surface-container-lowest/90 z-50 hidden items-center justify-center p-space-lg">
                <div class="obsidian-card p-space-lg rounded-xl shadow-2xl max-w-2xl w-full flex flex-col gap-space-md">
                    <div class="flex items-center justify-between border-b border-surface-variant pb-2">
                        <h3 class="font-headline-sm text-primary font-semibold">Configuration Snapshots &amp; Rollback</h3>
                        <button type="button" onclick="toggleModal('modal-history', false)" class="text-on-surface-variant hover:text-on-surface">✕</button>
                    </div>
                    <div id="history-list" class="max-h-72 overflow-y-auto flex flex-col gap-1.5 font-label-code-sm text-on-surface-variant">Loading snapshots from config/.history...</div>
                    <form id="form-rollback" method="post" action="/actions/config-rollback" class="hidden">
                        {targetInputHtml}
                        <input type="hidden" id="rollback-snapshot-id" name="snapshotId" value="" />
                    </form>
                    <div class="flex justify-end gap-2 pt-2">
                        <button type="button" onclick="loadConfigHistory()" class="cyber-btn-secondary px-3 py-1.5 rounded">↻ Refresh</button>
                        <button type="button" onclick="toggleModal('modal-history', false)" class="cyber-btn-secondary px-3 py-1.5 rounded">Close</button>
                    </div>
                </div>
            </div>

            <!-- Modal 19: Legacy Config Importer (Nginx / Caddy / IIS) -->
            <div id="modal-import" class="fixed inset-0 bg-surface-container-lowest/90 z-50 hidden items-center justify-center p-space-lg">
                <div class="obsidian-card p-space-lg rounded-xl shadow-2xl max-w-2xl w-full flex flex-col gap-space-md">
                    <div class="flex items-center justify-between border-b border-surface-variant pb-2">
                        <h3 class="font-headline-sm text-primary font-semibold">Import Nginx / Caddy / IIS Configuration</h3>
                        <button type="button" onclick="toggleModal('modal-import', false)" class="text-on-surface-variant hover:text-on-surface">✕</button>
                    </div>
                    <form id="form-config-import" method="post" action="/actions/config-import" class="flex flex-col gap-space-sm font-label-code-sm">
                        {targetInputHtml}
                        <div>
                            <label>Source Config Format</label>
                            <select name="format" class="obsidian-input mt-1">
                                <option value="nginx">Nginx (nginx.conf)</option>
                                <option value="caddy">Caddy (Caddyfile)</option>
                                <option value="iis">IIS (applicationHost.config)</option>
                            </select>
                        </div>
                        <div>
                            <label>Paste Configuration Content</label>
                            <textarea name="content" rows="9" placeholder="Paste nginx.conf, Caddyfile, or IIS applicationHost.config XML here..." class="obsidian-input mt-1 font-mono"></textarea>
                        </div>
                        <div class="flex justify-end gap-2 pt-2">
                            <button type="button" onclick="toggleModal('modal-import', false)" class="cyber-btn-secondary px-3 py-1.5 rounded">Cancel</button>
                            <button type="submit" class="cyber-btn-solid px-4 py-1.5 rounded">Import &amp; Apply</button>
                        </div>
                    </form>
                </div>
            </div>

            <!-- Modal 20: Interactive Pipeline Request Debugger -->
            <div id="modal-debugger" class="fixed inset-0 bg-surface-container-lowest/90 z-50 hidden items-center justify-center p-space-lg">
                <div class="obsidian-card p-space-lg rounded-xl shadow-2xl max-w-2xl w-full flex flex-col gap-space-md">
                    <div class="flex items-center justify-between border-b border-surface-variant pb-2">
                        <h3 class="font-headline-sm text-primary font-semibold">Interactive Pipeline Request Debugger</h3>
                        <button type="button" onclick="toggleModal('modal-debugger', false)" class="text-on-surface-variant hover:text-on-surface">✕</button>
                    </div>
                    <div class="grid grid-cols-5 gap-2 font-label-code-sm">
                        <div><label>Method</label><select id="dbg-method" class="obsidian-input mt-1"><option>GET</option><option>POST</option><option>PUT</option><option>DELETE</option></select></div>
                        <div><label>Scheme</label><select id="dbg-scheme" class="obsidian-input mt-1"><option>http</option><option>https</option></select></div>
                        <div class="col-span-2"><label>Host Header</label><input id="dbg-host" type="text" value="localhost" class="obsidian-input mt-1" /></div>
                        <div><label>Port</label><input id="dbg-port" type="number" value="8080" class="obsidian-input mt-1" /></div>
                    </div>
                    <div class="flex gap-2 font-label-code-sm">
                        <input id="dbg-path" type="text" value="/" class="obsidian-input flex-1 font-mono" />
                        <button type="button" id="btn-run-debugger" onclick="runPipelineSim()" class="cyber-btn-solid px-4 py-1.5 rounded shrink-0">▶ Simulate Request</button>
                    </div>
                    <div id="dbg-results" class="max-h-64 overflow-y-auto p-space-sm rounded bg-surface-container-lowest border border-surface-variant font-mono font-label-code-sm text-on-surface-variant">Click Simulate Request to dry-run host matching, WAF, routes, transforms, and upstream selection.</div>
                    <div class="flex justify-end pt-1">
                        <button type="button" onclick="toggleModal('modal-debugger', false)" class="cyber-btn-secondary px-3 py-1.5 rounded">Close</button>
                    </div>
                </div>
            </div>
            """);
    }

    // ================================================================================================
    // SCREEN 4: WORKER PROCESSES & TRACING (design/DesktopWinForms/worker_processes_tracing)
    // ================================================================================================
    private static void RenderWorkerProcessesAndTracingView(
        StringBuilder sb,
        ManagementWorkspaceSnapshot workspace,
        SiteCompositeView? selectedSite,
        IReadOnlyList<string> recentSiteLogs,
        IReadOnlyList<FrebTraceRecord> recentFrebTraces)
    {
        int totalWorkers = workspace.Pools.Sum(p => p.Status?.ActiveWorkers.Count ?? p.Config.ProcessModel.WorkerCount);
        long totalReqs = workspace.ServerStatus?.TotalRequestsProcessed ?? 0;
        var allWorkers = new List<WorkerProcessStatus>();
        foreach (PoolCompositeView p in workspace.Pools)
        {
            if (p.Status?.ActiveWorkers is { Count: > 0 } wList)
            {
                allWorkers.AddRange(wList);
            }
        }

        string workersJson = JsonSerializer.Serialize(allWorkers, KiteJsonContext.Default.ListWorkerProcessStatus);
        string frebJson = JsonSerializer.Serialize(recentFrebTraces.ToList(), KiteJsonContext.Default.ListFrebTraceRecord);

        sb.Append($"""
            <div class="flex flex-wrap items-center justify-between gap-space-md obsidian-card p-space-md rounded-xl shadow-lg">
                <div class="flex flex-col">
                    <span class="font-label-code-sm uppercase tracking-wider text-primary-fixed-dim">KITE // KERNEL &amp; WORKER RUNTIME • Real-Time Telemetry Provider</span>
                    <div class="flex items-baseline gap-space-sm">
                        <h1 class="font-headline-lg text-primary font-semibold">Active Worker Processes (<span class="font-mono text-primary-container">Kite.Worker</span>)</h1>
                        <span class="font-label-code-sm text-tertiary-container bg-surface-container px-space-sm py-0.5 rounded">{totalWorkers} Configured Instances</span>
                    </div>
                </div>
                <div class="flex items-center gap-space-md bg-surface-container px-space-md py-1.5 rounded-lg border border-surface-variant">
                    <div class="flex flex-col"><span class="font-badge-label text-on-surface-variant">Aggregate Requests</span><span class="font-label-code-lg text-primary-container font-semibold">{totalReqs:N0} reqs</span></div>
                    <div class="w-px h-6 bg-surface-variant"></div>
                    <div class="flex flex-col"><span class="font-badge-label text-on-surface-variant">FREB Captures</span><span class="font-label-code-lg text-error font-semibold">{recentFrebTraces.Count} traces</span></div>
                    <div class="w-px h-6 bg-surface-variant"></div>
                    <div class="flex flex-col"><span class="font-badge-label text-on-surface-variant">W3C Events</span><span class="font-label-code-lg text-secondary font-semibold">{recentSiteLogs.Count} lines</span></div>
                </div>
            </div>

            <div class="flex flex-col obsidian-card rounded-xl shadow-xl overflow-hidden">
                <div class="flex items-center justify-between px-space-md py-2 bg-surface-container border-b border-surface-variant">
                    <span class="font-headline-sm text-on-surface">Isolated Execution Engines (Application Pool Supervisors)</span>
                    <span class="font-label-code-sm text-on-surface-variant">DATAS Server GC • Thread &amp; MiniDump Diagnostics</span>
                </div>
                <div class="w-full overflow-x-auto">
                    <table class="w-full text-left">
                        <thead>
                            <tr class="bg-surface-container-high text-on-surface-variant font-badge-label uppercase">
                                <th class="py-2 px-space-md">Worker / App Pool</th>
                                <th class="py-2 px-space-md">CLR Engine Runtime</th>
                                <th class="py-2 px-space-md">CPU %</th>
                                <th class="py-2 px-space-md">Working Set</th>
                                <th class="py-2 px-space-md">Virtual Mem</th>
                                <th class="py-2 px-space-md">Requests</th>
                                <th class="py-2 px-space-md text-right">Diagnostics</th>
                            </tr>
                        </thead>
                        <tbody class="font-label-code-md">
            """);

        int workerIndex = 0;
        foreach (PoolCompositeView p in workspace.Pools)
        {
            IReadOnlyList<WorkerProcessStatus> workers = p.Status?.ActiveWorkers ?? [];
            if (workers.Count == 0)
            {
                sb.Append($"""
                            <tr class="hover:bg-surface-container" data-filterable>
                                <td class="py-2 px-space-md font-semibold text-primary">{Enc(p.Config.Name)} (Standby / {p.Config.ProcessModel.StartMode})</td>
                                <td class="py-2 px-space-md text-tertiary-container">.NET 10.0 Kestrel Pipeline</td>
                                <td class="py-2 px-space-md text-on-surface-variant">0.0%</td>
                                <td class="py-2 px-space-md text-on-surface">{((workspace.ServerStatus?.ManagedHeapBytes ?? 0) / (1024.0 * 1024.0)):F2} MB</td>
                                <td class="py-2 px-space-md text-on-surface-variant">-</td>
                                <td class="py-2 px-space-md text-primary-container">{p.Status?.TotalRequestsServed ?? 0} total</td>
                                <td class="py-2 px-space-md text-right">
                                    <a href="/?scope=pools&entity={Uri.EscapeDataString(p.Config.Id)}" class="cyber-btn-secondary px-2 py-1 rounded font-label-code-sm">Inspect Pool</a>
                                </td>
                            </tr>
                    """);
            }
            else
            {
                foreach (WorkerProcessStatus w in workers)
                {
                    int currentIdx = workerIndex++;
                    double memMb = w.MemoryBytes / (1024.0 * 1024.0);
                    double vmGb = w.VirtualMemoryBytes / (1024.0 * 1024.0 * 1024.0);
                    sb.Append($"""
                            <tr class="hover:bg-surface-container" data-filterable>
                                <td class="py-2 px-space-md">
                                    <span class="font-semibold text-primary">{Enc(w.WorkerId)} (PID {w.ProcessId})</span>
                                    <span class="text-on-surface-variant ml-2">{Enc(p.Config.Name)}</span>
                                </td>
                                <td class="py-2 px-space-md text-tertiary-container">{w.HostingMode} • {p.Config.ProcessModel.GcMode}</td>
                                <td class="py-2 px-space-md">
                                    <div class="flex flex-col gap-1">
                                        <span class="text-primary-container font-semibold">{w.CpuUsagePercent:F1}%</span>
                                        <div class="w-full h-1.5 bg-surface-container-highest rounded-full overflow-hidden">
                                            <div class="h-full bg-primary-container" style="width: {Math.Clamp(w.CpuUsagePercent, 3, 100):F0}%;"></div>
                                        </div>
                                    </div>
                                </td>
                                <td class="py-2 px-space-md font-semibold text-on-surface">{memMb:F1} MB</td>
                                <td class="py-2 px-space-md text-on-surface-variant">{vmGb:F2} GB</td>
                                <td class="py-2 px-space-md">
                                    <span class="px-2 py-0.5 rounded bg-surface-container-highest text-tertiary-container font-semibold">{w.ActiveRequestsCount} active</span>
                                    <span class="text-on-surface-variant ml-1">{w.RequestsServed} reqs</span>
                                </td>
                                <td class="py-2 px-space-md text-right whitespace-nowrap">
                                    <div class="inline-flex items-center gap-1">
                                        <form method="post" action="/actions/pool-command" style="display:inline;">
                                            <input type="hidden" name="poolId" value="{Enc(p.Config.Id)}" />
                                            <button type="submit" name="command" value="recycle" class="px-2 py-1 rounded bg-surface-container-high hover:bg-primary-container/25 text-primary-container font-label-code-sm" title="Recycle App Pool">↻ Recycle</button>
                                        </form>
                                        <form method="post" action="/actions/worker-dump" style="display:inline;">
                                            <input type="hidden" name="processId" value="{w.ProcessId}" />
                                            <input type="hidden" name="poolId" value="{Enc(p.Config.Id)}" />
                                            <button type="submit" class="px-2 py-1 rounded bg-surface-container-high text-on-surface font-label-code-sm" title="Create Crash Dump (.dmp)">⬇ Dump</button>
                                        </form>
                                        <button type="button" onclick="openThreadInspector({currentIdx}, '{EncJs(p.Config.Name)}')" class="px-2 py-1 rounded bg-surface-container-high text-secondary font-label-code-sm" title="Inspect Threads">🧬 Threads</button>
                                        <button type="button" onclick="openStdoutModal({currentIdx}, '{EncJs(p.Config.Name)}')" class="px-2 py-1 rounded bg-surface-container-high text-primary-container font-label-code-sm" title="View Live Stdout/Stderr">📜 Stdout</button>
                                        <form method="post" action="/actions/worker-kill" style="display:inline;">
                                            <input type="hidden" name="processId" value="{w.ProcessId}" />
                                            <input type="hidden" name="poolId" value="{Enc(p.Config.Id)}" />
                                            <button type="submit" class="px-2 py-1 rounded bg-error-container/20 hover:bg-error-container text-error font-label-code-sm" title="Kill Worker Process">✕ Kill</button>
                                        </form>
                                    </div>
                                </td>
                            </tr>
                        """);
                }
            }
        }

        // BOTTOM HALF: REAL-TIME REQUEST STREAM, FREB CONSOLE & SLIDE-OVER INSPECTOR
        sb.Append($"""
                        </tbody>
                    </table>
                </div>
            </div>

            <div class="flex flex-col obsidian-card rounded-xl shadow-2xl overflow-hidden">
                <div class="flex flex-wrap items-center justify-between gap-space-sm p-space-sm bg-surface-container border-b border-surface-variant">
                    <div class="flex flex-wrap items-center gap-space-xs">
                        <span class="px-2 py-1 rounded bg-surface-container-high text-primary font-label-code-md font-semibold">Live ETW / FREB Trace Stream</span>
                        <div class="flex items-center bg-surface-container-lowest rounded p-0.5 font-label-code-sm">
                            <button type="button" onclick="filterFrebLogs('ALL')" class="px-2 py-0.5 rounded bg-surface-container-high text-primary">ALL</button>
                            <button type="button" onclick="filterFrebLogs('2xx')" class="px-2 py-0.5 rounded text-on-surface-variant hover:text-tertiary-container">2xx</button>
                            <button type="button" onclick="filterFrebLogs('4xx')" class="px-2 py-0.5 rounded text-on-surface-variant hover:text-secondary">4xx</button>
                            <button type="button" onclick="filterFrebLogs('5xx')" class="px-2 py-0.5 rounded text-on-surface-variant hover:text-error">5xx (FREB)</button>
                        </div>
                        <button type="button" id="latency-toggle" onclick="toggleLatencyFilter()" class="px-2 py-1 rounded bg-surface-container-lowest text-on-surface-variant font-label-code-sm">Latency &gt; 500ms</button>
                    </div>
                    <div class="flex items-center gap-space-sm font-label-code-sm">
                        <button type="button" onclick="clearLiveConsole()" class="cyber-btn-secondary px-2 py-1 rounded">Clear Console</button>
                        <a href="/?scope=workers" class="cyber-btn-primary px-2.5 py-1 rounded">↻ Refresh Stream</a>
                    </div>
                </div>

                <div class="flex overflow-hidden" style="min-height: 360px;">
                    <div id="live-console-feed" class="flex-1 p-space-sm bg-surface-container-lowest font-mono font-label-code-md flex flex-col gap-1 max-h-96 overflow-y-auto select-text">
            """);

        if (recentFrebTraces.Count == 0 && recentSiteLogs.Count == 0)
        {
            sb.Append("""<div class="text-on-surface-variant p-space-sm">No FREB faults or W3C access log entries recorded yet. Send an HTTP request or trigger a 500/slow request to inspect pipeline stages.</div>""");
        }
        else
        {
            for (int i = 0; i < recentFrebTraces.Count; i++)
            {
                FrebTraceRecord t = recentFrebTraces[i];
                bool is5xx = t.StatusCode >= 500;
                string rowBg = is5xx ? "bg-error-container/10 hover:bg-error-container/20" : "bg-surface-container-high/40 hover:bg-surface-container-high";
                string statusCls = is5xx ? "bg-error-container text-error font-bold" : "bg-secondary-container/40 text-secondary font-bold";
                string cat = is5xx ? "5xx" : (t.StatusCode >= 400 ? "4xx" : "2xx");
                sb.Append($"""
                        <div class="request-item flex items-center justify-between p-1.5 rounded cursor-pointer {rowBg}" data-status-cat="{cat}" data-latency="{t.TotalDurationMs:F1}" data-filterable onclick="openFrebInspector({i})">
                            <div class="flex items-center gap-space-sm min-w-0">
                                <span class="font-label-code-sm {(is5xx ? "text-error" : "text-on-surface-variant")}">{t.TimestampUtc:HH:mm:ss.fff}</span>
                                <span class="px-1.5 py-0.5 rounded bg-primary-container/20 text-primary-container font-badge-label">{Enc(t.Method)}</span>
                                <span class="px-1.5 py-0.5 rounded {statusCls} font-badge-label">{t.StatusCode} FREB</span>
                                <span class="font-semibold {(is5xx ? "text-error" : "text-tertiary-container")}">{t.TotalDurationMs:F1} ms</span>
                                <span class="text-on-surface font-semibold truncate">{Enc(t.Path)} {(!string.IsNullOrEmpty(t.ExceptionMessage) ? $"[{Enc(t.ExceptionMessage)}]" : "")}</span>
                            </div>
                            <div class="flex items-center gap-space-md text-on-surface-variant font-label-code-sm shrink-0">
                                <span>{Enc(t.ClientIp)}</span>
                                <span class="px-1 rounded bg-surface-container">{Enc(t.SiteId)}</span>
                                <span class="text-primary">Inspect →</span>
                            </div>
                        </div>
                    """);
            }

            foreach (string line in recentSiteLogs)
            {
                string cat = line.Contains(" 500 ") || line.Contains(" 502 ") || line.Contains(" 503 ") ? "5xx"
                    : (line.Contains(" 401 ") || line.Contains(" 403 ") || line.Contains(" 404 ") || line.Contains(" 405 ") ? "4xx" : "2xx");
                sb.Append($"""
                        <div class="request-item flex items-center justify-between p-1.5 rounded hover:bg-surface-container-high/60 border-b border-surface-variant" data-status-cat="{cat}" data-latency="1.0" data-filterable>
                            <span class="text-on-surface truncate">{Enc(line)}</span>
                            <span class="px-1.5 py-0.5 rounded bg-tertiary/10 text-tertiary-container font-badge-label shrink-0">W3C</span>
                        </div>
                    """);
            }
        }

        sb.Append("""
                    </div>

                    <!-- Slide-Over FREB Inspector Drawer -->
                    <aside id="inspector-panel" class="hidden w-inspector bg-surface-container-low border-l border-surface-variant flex-col overflow-y-auto shrink-0 shadow-2xl">
                        <div class="flex items-center justify-between p-space-md bg-surface-container border-b border-surface-variant">
                            <div class="flex flex-col min-w-0">
                                <div class="flex items-center gap-space-xs">
                                    <span class="font-badge-label uppercase text-on-surface-variant">FREB TRACE EVENT</span>
                                    <span id="freb-status-badge" class="px-1.5 py-0.5 rounded bg-error-container text-error font-badge-label">500 Fault</span>
                                </div>
                                <span id="freb-trace-id" class="font-label-code-md font-semibold text-primary truncate">Select a trace</span>
                            </div>
                            <button type="button" onclick="closeFrebInspector()" class="p-1 rounded hover:bg-surface-container-high text-on-surface-variant">✕</button>
                        </div>
                        <div class="p-space-md flex flex-col gap-space-md">
                            <div class="flex flex-col gap-space-xs">
                                <span class="font-badge-label uppercase text-on-surface-variant">Kite Integrated Pipeline Stages</span>
                                <div id="freb-stages-list" class="flex flex-col gap-1 p-space-sm rounded-lg bg-surface-container-lowest font-label-code-sm"></div>
                            </div>
                            <div class="flex flex-col gap-space-xs">
                                <span class="font-badge-label uppercase text-on-surface-variant">HTTP Diagnostic Envelope</span>
                                <div id="freb-envelope-box" class="bg-surface-container-lowest rounded-lg p-space-sm font-label-code-sm flex flex-col gap-1 text-on-surface-variant select-text"></div>
                            </div>
                            <div class="flex flex-col gap-space-xs">
                                <div class="flex items-center justify-between">
                                    <span class="font-badge-label uppercase text-error">CLR Stack Exception Trace</span>
                                    <button type="button" onclick="copyToClipboard(document.getElementById('stack-trace-code').innerText)" class="font-label-code-sm text-primary hover:underline">Copy Trace</button>
                                </div>
                                <pre id="stack-trace-code" class="p-space-sm rounded-lg bg-surface-container-lowest font-label-code-sm text-error overflow-x-auto select-text whitespace-pre"></pre>
                            </div>
                        </div>
                    </aside>
                </div>
            </div>

            <!-- Thread Pool Diagnostics Modal -->
            <div id="thread-modal" class="fixed inset-0 z-50 bg-surface-container-lowest/90 hidden items-center justify-center p-space-lg">
                <div class="w-full max-w-3xl bg-surface-container-low rounded-xl shadow-2xl overflow-hidden flex flex-col border border-surface-variant">
                    <div class="flex items-center justify-between p-space-md bg-surface-container border-b border-surface-variant">
                        <div>
                            <h2 id="thread-modal-title" class="font-headline-sm text-on-surface font-semibold">Thread Pool Diagnostics</h2>
                            <p id="thread-modal-subtitle" class="font-label-code-sm text-on-surface-variant">OS Worker &amp; CLR ThreadPool Telemetry</p>
                        </div>
                        <button type="button" onclick="toggleModal('thread-modal', false)" class="p-1 rounded hover:bg-surface-container-high text-on-surface-variant">✕</button>
                    </div>
                    <div class="p-space-md flex flex-col gap-space-md">
                        <div class="grid grid-cols-4 gap-space-sm font-label-code-sm">
                            <div class="p-space-sm rounded bg-surface-container flex flex-col"><span class="font-badge-label text-on-surface-variant">Worker Threads</span><span id="tm-workers" class="font-label-code-lg text-primary-container font-semibold">0</span></div>
                            <div class="p-space-sm rounded bg-surface-container flex flex-col"><span class="font-badge-label text-on-surface-variant">I/O Completion</span><span id="tm-io" class="font-label-code-lg text-tertiary-container font-semibold">0</span></div>
                            <div class="p-space-sm rounded bg-surface-container flex flex-col"><span class="font-badge-label text-on-surface-variant">Lock Contention</span><span id="tm-locks" class="font-label-code-lg text-on-surface font-semibold">0</span></div>
                            <div class="p-space-sm rounded bg-surface-container flex flex-col"><span class="font-badge-label text-on-surface-variant">Working Set</span><span id="tm-mem" class="font-label-code-lg text-secondary font-semibold">0 MB</span></div>
                        </div>
                        <div class="w-full overflow-hidden rounded bg-surface-container-lowest max-h-64 overflow-y-auto">
                            <table class="w-full text-left font-label-code-sm">
                                <thead class="bg-surface-container-high text-on-surface-variant font-badge-label uppercase">
                                    <tr><th class="py-1.5 px-3">TID</th><th class="py-1.5 px-3">Thread State</th><th class="py-1.5 px-3">Priority</th><th class="py-1.5 px-3">Wait Reason</th><th class="py-1.5 px-3">User Time (ms)</th></tr>
                                </thead>
                                <tbody id="tm-thread-rows"></tbody>
                            </table>
                        </div>
                    </div>
                    <div class="p-space-md bg-surface-container flex items-center justify-between border-t border-surface-variant">
                        <span class="font-label-code-sm text-on-surface-variant">Live System.Diagnostics.ProcessThread Snapshot</span>
                        <button type="button" onclick="toggleModal('thread-modal', false)" class="cyber-btn-secondary px-space-md py-1 rounded font-label-code-sm">Close Inspector</button>
                    </div>
                </div>
            </div>

            <!-- Polyglot Out-Of-Process Stdout / Stderr Stream Modal -->
            <div id="stdout-modal" class="hidden fixed inset-0 z-50 bg-surface-container-lowest/85 items-center justify-center p-space-md">
                <div class="w-full max-w-3xl bg-surface-container-low rounded-xl shadow-2xl overflow-hidden flex flex-col border border-surface-variant">
                    <div class="flex items-center justify-between p-space-md bg-surface-container border-b border-surface-variant">
                        <div>
                            <h2 id="stdout-modal-title" class="font-headline-sm text-on-surface font-semibold">Worker Live Stdout / Stderr Ring Buffer</h2>
                            <p id="stdout-modal-subtitle" class="font-label-code-sm text-on-surface-variant">Recent 120 console output lines</p>
                        </div>
                        <button type="button" onclick="toggleModal('stdout-modal', false)" class="p-1 rounded hover:bg-surface-container-high text-on-surface-variant">✕</button>
                    </div>
                    <pre id="stdout-modal-body" class="p-space-md bg-surface-container-lowest font-mono font-label-code-sm text-on-surface max-h-80 overflow-y-auto whitespace-pre-wrap select-text"></pre>
                    <div class="p-space-md bg-surface-container flex items-center justify-between border-t border-surface-variant">
                        <span class="font-label-code-sm text-on-surface-variant">Captured from OutOfProcessProxy child process</span>
                        <button type="button" onclick="toggleModal('stdout-modal', false)" class="cyber-btn-secondary px-space-md py-1 rounded font-label-code-sm">Close Console</button>
                    </div>
                </div>
            </div>
            """);

        sb.Append($"""
            <script>
                const KITE_WORKERS = {workersJson};
                const KITE_FREB_TRACES = {frebJson};
            """);
        sb.Append("""
                let latencyFilterActive = false;

                function escHtml(s) {
                    return String(s ?? '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
                }

                function openStdoutModal(idx, poolName) {
                    const w = KITE_WORKERS[idx];
                    if (!w) return;
                    document.getElementById('stdout-modal-title').innerText = 'Worker Stdout/Stderr - ' + w.workerId + ' (PID ' + w.processId + ')';
                    document.getElementById('stdout-modal-subtitle').innerText = poolName + ' • Restarts: ' + (w.restartCount || 0);
                    const lines = w.recentOutputLines || [];
                    document.getElementById('stdout-modal-body').innerText = lines.length > 0
                        ? lines.join('\n')
                        : 'No stdout/stderr output lines captured (in-process worker or quiet child process).';
                    toggleModal('stdout-modal', true);
                }

                function openThreadInspector(idx, poolName) {
                    const w = KITE_WORKERS[idx];
                    if (!w) return;
                    document.getElementById('thread-modal-title').innerText = 'Thread Pool Diagnostics - PID ' + w.processId + ' (' + poolName + ')';
                    document.getElementById('thread-modal-subtitle').innerText = 'Worker ' + w.workerId + ' • Active Requests: ' + w.activeRequestsCount;
                    document.getElementById('tm-workers').innerText = w.workerThreadsActive + ' Active';
                    document.getElementById('tm-io').innerText = w.ioThreadsActive + ' Active';
                    document.getElementById('tm-locks').innerText = w.lockContentionCount;
                    document.getElementById('tm-mem').innerText = (w.memoryBytes / (1024 * 1024)).toFixed(1) + ' MB';
                    const tbody = document.getElementById('tm-thread-rows');
                    tbody.innerHTML = '';
                    (w.topThreads || []).forEach(t => {
                        const tr = document.createElement('tr');
                        tr.className = 'hover:bg-surface-container/50';
                        tr.innerHTML = '<td class="py-1.5 px-3 font-semibold text-primary">0x' + t.threadId.toString(16).toUpperCase() + '</td>' +
                            '<td class="py-1.5 px-3 text-tertiary-container">' + escHtml(t.state) + '</td>' +
                            '<td class="py-1.5 px-3 text-on-surface">' + escHtml(t.priority) + '</td>' +
                            '<td class="py-1.5 px-3 text-on-surface-variant">' + escHtml(t.waitReason) + '</td>' +
                            '<td class="py-1.5 px-3 text-on-surface">' + t.userTimeMs.toFixed(1) + '</td>';
                        tbody.appendChild(tr);
                    });
                    toggleModal('thread-modal', true);
                }

                function openFrebInspector(idx) {
                    const t = KITE_FREB_TRACES[idx];
                    if (!t) return;
                    document.getElementById('freb-trace-id').innerText = t.traceId + ' (' + t.totalDurationMs.toFixed(1) + ' ms)';
                    document.getElementById('freb-status-badge').innerText = t.statusCode + (t.subStatus ? ' (' + t.subStatus + ')' : '') + ' • ' + t.triggerReason;
                    const stagesEl = document.getElementById('freb-stages-list');
                    stagesEl.innerHTML = '';
                    (t.stages || []).forEach(st => {
                        const div = document.createElement('div');
                        const bad = (st.outcome || '').includes('CRASH') || (st.outcome || '').includes('Rejected') || (st.outcome || '').includes('401');
                        div.className = 'flex items-center justify-between p-1 rounded ' + (bad ? 'text-error bg-error-container/20 font-semibold' : 'text-tertiary-container');
                        div.innerHTML = '<span>' + escHtml(st.stageName) + ' (' + escHtml(st.outcome) + ')</span><span>' + st.durationMs.toFixed(2) + 'ms</span>';
                        stagesEl.appendChild(div);
                    });
                    document.getElementById('freb-envelope-box').innerHTML =
                        '<div><span class="text-primary-container">Request:</span> ' + escHtml(t.method) + ' ' + escHtml(t.path) + escHtml(t.queryString || '') + '</div>' +
                        '<div><span class="text-primary-container">Host / Site:</span> ' + escHtml(t.host || 'localhost') + ' (' + escHtml(t.siteId) + ')</div>' +
                        '<div><span class="text-primary-container">Correlation-ID:</span> ' + escHtml(t.correlationId) + '</div>' +
                        '<div><span class="text-primary-container">Client IP:</span> ' + escHtml(t.clientIp) + '</div>';
                    document.getElementById('stack-trace-code').innerText = t.exceptionStackTrace || t.exceptionMessage || 'No unhandled exception (trigger: ' + t.triggerReason + ')';
                    const panel = document.getElementById('inspector-panel');
                    panel.classList.remove('hidden');
                    panel.classList.add('flex');
                }

                function closeFrebInspector() {
                    const panel = document.getElementById('inspector-panel');
                    panel.classList.add('hidden');
                    panel.classList.remove('flex');
                }

                function filterFrebLogs(cat) {
                    document.querySelectorAll('.request-item').forEach(el => {
                        const c = el.getAttribute('data-status-cat');
                        el.style.display = (cat === 'ALL' || c === cat) ? 'flex' : 'none';
                    });
                }

                function toggleLatencyFilter() {
                    latencyFilterActive = !latencyFilterActive;
                    const btn = document.getElementById('latency-toggle');
                    btn.className = latencyFilterActive
                        ? 'px-2 py-1 rounded bg-primary-container text-on-primary-container font-label-code-sm font-semibold'
                        : 'px-2 py-1 rounded bg-surface-container-lowest text-on-surface-variant font-label-code-sm';
                    document.querySelectorAll('.request-item').forEach(el => {
                        const lat = parseFloat(el.getAttribute('data-latency') || '0');
                        el.style.display = (latencyFilterActive && lat <= 500) ? 'none' : 'flex';
                    });
                }

                function clearLiveConsole() {
                    document.getElementById('live-console-feed').innerHTML = '<div class="text-on-surface-variant p-space-sm">Console cleared.</div>';
                }
            </script>
            """);
    }

    private static List<string> SplitFormList(string raw)
    {
        return string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }
}
