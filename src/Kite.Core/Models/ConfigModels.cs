using System.Text.Json.Serialization;

namespace Kite.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<DesiredResourceState>))]
public enum DesiredResourceState
{
    Started,
    Stopped
}

[JsonConverter(typeof(JsonStringEnumConverter<WorkloadHostingMode>))]
public enum WorkloadHostingMode
{
    StaticOnly,
    OutOfProcessProxy,
    KiteWorkerIpc,
    ExternalReverseProxy
}

[JsonConverter(typeof(JsonStringEnumConverter<PoolGcMode>))]
public enum PoolGcMode
{
    DatasServer,
    Server,
    Workstation
}

[JsonConverter(typeof(JsonStringEnumConverter<PoolStartMode>))]
public enum PoolStartMode
{
    AlwaysRunning,
    OnDemand
}

[JsonConverter(typeof(JsonStringEnumConverter<UrlRewriteActionType>))]
public enum UrlRewriteActionType
{
    Redirect301,
    Redirect302,
    Rewrite,
    ReverseProxy,
    CustomResponse
}

[JsonConverter(typeof(JsonStringEnumConverter<CustomErrorMode>))]
public enum CustomErrorMode
{
    PassThrough,
    DetailedLocalOnly,
    CustomPages
}

[JsonConverter(typeof(JsonStringEnumConverter<AcmeChallengeType>))]
public enum AcmeChallengeType
{
    Http01,
    TlsAlpn01,
    Dns01
}

[JsonConverter(typeof(JsonStringEnumConverter<MtlsClientCertificateMode>))]
public enum MtlsClientCertificateMode
{
    None,
    AllowCertificate,
    RequireCertificate
}

[JsonConverter(typeof(JsonStringEnumConverter<LoadBalancingPolicy>))]
public enum LoadBalancingPolicy
{
    RoundRobin,
    LeastRequests,
    PowerOfTwoChoices,
    ConsistentHash
}

[JsonConverter(typeof(JsonStringEnumConverter<AccessLogFormat>))]
public enum AccessLogFormat
{
    W3C,
    Json,
    Both
}

public static class KiteConfigVersion
{
    public const string Current = "1.0";

    public static bool IsSupported(string? version) =>
        string.Equals(version?.Trim(), Current, StringComparison.Ordinal);

    public static void EnsureSupported(string? version, string resourceLabel)
    {
        if (!IsSupported(version))
        {
            throw new InvalidDataException(
                $"Unsupported configVersion '{version ?? "<missing>"}' in {resourceLabel}. Expected '{Current}'.");
        }
    }
}

public sealed class ServerConfig
{
    [JsonPropertyName("$schema")]
    public string Schema { get; set; } = "https://kite.local/schemas/server.v1.json";

    public string ConfigVersion { get; set; } = KiteConfigVersion.Current;

    public string ServerName { get; set; } = "Kite-Production-Node";
    public int MaxConcurrentConnections { get; set; } = 100_000;
    public long MaxRequestBodySize { get; set; } = 30_000_000;
    public int KeepAliveTimeoutSeconds { get; set; } = 120;
    public int HeaderTimeoutSeconds { get; set; } = 30;
    public bool EnableAltSvcHeader { get; set; } = true;
    public bool DefaultCompressionEnabled { get; set; } = true;
    public bool DefaultCachingEnabled { get; set; } = true;
    public int StatusPublishIntervalMilliseconds { get; set; } = 1000;
    public HttpTransportConfig Transport { get; set; } = new();
    public ObservabilityConfig Observability { get; set; } = new();
    public List<UpstreamClusterConfig> Upstreams { get; set; } = [];
    public List<L4StreamProxyConfig> TcpStreams { get; set; } = [];
    public DistributedStateConfig DistributedState { get; set; } = new();
    public MeshConfig Mesh { get; set; } = new();
    public List<EdgePluginConfig> EdgePlugins { get; set; } = [];
    public SiteFeaturesConfig Features { get; set; } = new() { InheritServerDefaults = false };
}

[JsonConverter(typeof(JsonStringEnumConverter<MeshMode>))]
public enum MeshMode
{
    Auto,
    LeaderOnly,
    PeerOnly,
    Disabled
}

public sealed class MeshConfig
{
    public MeshMode Mode { get; set; } = MeshMode.Auto;
    public List<int> FrontDoorPorts { get; set; } = [80, 443, 8080, 8443];
    public string AutoDomainSuffix { get; set; } = ".localhost";
    public string? RegistryDirectoryOverride { get; set; }
}

public sealed class HttpTransportConfig
{
    public bool EnableHttp3 { get; set; } = false;
    public bool EnableQuicZeroRtt { get; set; } = false;
    public int AltSvcMaxAgeSeconds { get; set; } = 86400;
    public double MinRequestBodyDataRateBytesPerSecond { get; set; } = 240.0;
    public double MinResponseDataRateBytesPerSecond { get; set; } = 240.0;
    public int SlowlorisGracePeriodSeconds { get; set; } = 5;
    public int GracefulDrainTimeoutSeconds { get; set; } = 15;
    public bool EnableInlineSocketScheduling { get; set; } = false;
    public bool WaitForDataBeforeAllocatingBuffer { get; set; } = true;
}

public sealed class ObservabilityConfig
{
    public bool EnableOpenTelemetryTracing { get; set; } = true;
    public string ActivitySourceName { get; set; } = "Kite.Server";
    public OtlpExportConfig Otlp { get; set; } = new();
    public bool EnablePrometheusMetrics { get; set; } = true;
    public string MetricsPath { get; set; } = "/__kite/metrics";
    public bool EnableHealthEndpoints { get; set; } = true;
    public string HealthEndpointPath { get; set; } = "/__kite/healthz";
    public string ReadyEndpointPath { get; set; } = "/__kite/readyz";
    public AccessLogFormat AccessLogFormat { get; set; } = AccessLogFormat.W3C;
}

public sealed class ApplicationPoolConfig
{
    [JsonPropertyName("$schema")]
    public string Schema { get; set; } = "https://kite.local/schemas/pool.v1.json";

    public string ConfigVersion { get; set; } = KiteConfigVersion.Current;

    public string Id { get; set; } = "default-pool";
    public string Name { get; set; } = "Default Application Pool";
    public DesiredResourceState DesiredState { get; set; } = DesiredResourceState.Started;
    public long RecycleVersion { get; set; } = 1;
    public PoolProcessModelConfig ProcessModel { get; set; } = new();
    public PoolRecyclingConfig Recycling { get; set; } = new();
    public PoolRapidFailConfig RapidFailProtection { get; set; } = new();
}

public sealed class PoolProcessModelConfig
{
    public int WorkerCount { get; set; } = 1;
    public int MinWorkers { get; set; } = 1;
    public int MaxWorkers { get; set; } = 4;
    public int ScaleUpConcurrencyThreshold { get; set; } = 20;
    public int ScaleDownIdleSeconds { get; set; } = 120;
    public bool UseUnixDomainSocket { get; set; } = false;
    public int IdleTimeoutMinutes { get; set; } = 20;
    public string Identity { get; set; } = "ApplicationPoolIdentity";
    public PoolGcMode GcMode { get; set; } = PoolGcMode.DatasServer;
    public PoolStartMode StartMode { get; set; } = PoolStartMode.AlwaysRunning;
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<PoolSidecarConfig> Sidecars { get; set; } = [];
}

public sealed class PoolRecyclingConfig
{
    public int PeriodicRestartMinutes { get; set; } = 1740;
    public long MaxMemoryMegabytes { get; set; } = 1024;
    public long MaxRequests { get; set; } = 0;
    public string? ScheduledRestartUtcTime { get; set; } = "04:00";
    public bool DisallowOverlappingRotation { get; set; } = false;
}

public sealed class PoolRapidFailConfig
{
    public bool Enabled { get; set; } = true;
    public int MaxCrashes { get; set; } = 5;
    public int IntervalMinutes { get; set; } = 5;
}

public sealed class SiteConfig
{
    [JsonPropertyName("$schema")]
    public string Schema { get; set; } = "https://kite.local/schemas/site.v1.json";

    public string ConfigVersion { get; set; } = KiteConfigVersion.Current;

    public string Id { get; set; } = "default-site";
    public string Name { get; set; } = "Default Web Site";
    public DesiredResourceState DesiredState { get; set; } = DesiredResourceState.Started;
    public List<SiteBindingConfig> Bindings { get; set; } = [];
    public SiteApplicationConfig RootApplication { get; set; } = new();
    public List<SiteApplicationConfig> NestedApplications { get; set; } = [];
    public SiteFeaturesConfig Features { get; set; } = new();
}

public sealed class SiteBindingConfig
{
    public string Protocol { get; set; } = "http";
    public string IpAddress { get; set; } = "*";
    public int Port { get; set; } = 80;
    public string Host { get; set; } = "*";
    public SiteTlsConfig? Tls { get; set; }

    public string ToDisplayEndpoint() => $"{Protocol}://{IpAddress}:{Port} ({(string.IsNullOrEmpty(Host) ? "*" : Host)})";
}

public sealed class SiteTlsConfig
{
    public string? CertificatePath { get; set; }
    public string? CertificatePassword { get; set; }
    public bool UseSelfSignedDevelopmentCert { get; set; } = true;
    public bool AllowHttp2 { get; set; } = true;
    public bool AllowHttp3 { get; set; } = true;
    public bool AllowTls12 { get; set; } = true;
    public bool AllowTls13 { get; set; } = true;
}

public sealed class SiteApplicationConfig
{
    public string Path { get; set; } = "/";
    public string PoolId { get; set; } = "default-pool";
    public string PhysicalPath { get; set; } = "";
    public WorkloadHostingMode HostingMode { get; set; } = WorkloadHostingMode.StaticOnly;
    public string? Executable { get; set; }
    public string? Arguments { get; set; }
    public string? EntryPoint { get; set; }
    public string? UpstreamUrl { get; set; }
    public string? UpstreamClusterId { get; set; }
    public string? CanaryClusterId { get; set; }
    public int CanaryWeightPercent { get; set; } = 0;
    public bool EnableStreamingPassThrough { get; set; } = false;
    public bool EnableGrpcWebBridge { get; set; } = false;
    public bool ServePrecompressedSidecars { get; set; } = true;
    public string ActiveSlot { get; set; } = "blue";
    public string? StagingPhysicalPath { get; set; }
    public List<VirtualDirectoryConfig> VirtualDirectories { get; set; } = [];
}

public sealed class VirtualDirectoryConfig
{
    public string VirtualPath { get; set; } = "/assets";
    public string PhysicalPath { get; set; } = "";
}

public sealed class SiteFeaturesConfig
{
    public bool InheritServerDefaults { get; set; } = true;
    public List<string> DefaultDocuments { get; set; } = ["index.html", "default.html", "index.htm"];
    public bool DirectoryBrowsing { get; set; } = false;
    public SiteCompressionConfig Compression { get; set; } = new();
    public SiteCachingConfig Caching { get; set; } = new();
    public SiteHstsConfig Hsts { get; set; } = new();
    public Dictionary<string, string> CustomHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> RemoveHeaders { get; set; } = ["X-Powered-By"];
    public bool StripServerHeader { get; set; } = true;
    public bool W3CLoggingEnabled { get; set; } = true;
    public UrlRewriteConfig UrlRewrite { get; set; } = new();
    public RedirectAndErrorsConfig RedirectsAndErrors { get; set; } = new();
    public MimeMappingsConfig MimeMappings { get; set; } = new();
    public CorsPolicyConfig Cors { get; set; } = new();
    public TlsPolicyConfig TlsPolicy { get; set; } = new();
    public AuthenticationConfig Authentication { get; set; } = new();
    public IpRestrictionsConfig IpRestrictions { get; set; } = new();
    public RequestFilteringConfig RequestFiltering { get; set; } = new();
    public FailedRequestTracingConfig FailedRequestTracing { get; set; } = new();
    public List<string> EarlyHintsLinks { get; set; } = [];
    public RateLimitingConfig RateLimiting { get; set; } = new();
    public SpaFallbackConfig SpaFallback { get; set; } = new();
    public EdgeWafConfig EdgeWaf { get; set; } = new();
    public List<SiteRouteRuleConfig> Routes { get; set; } = [];
    public ForwardAuthConfig ForwardAuth { get; set; } = new();
}

public sealed class SiteCompressionConfig
{
    public bool Enabled { get; set; } = true;
    public int MinSizeBytes { get; set; } = 128;
    public List<string> Encodings { get; set; } = ["zstd", "br", "gzip", "deflate"];
    public int BrotliLevel { get; set; } = 5;
    public int ZstdLevel { get; set; } = 3;
    public int GzipLevel { get; set; } = 7;
}

public sealed class SiteCachingConfig
{
    public bool Enabled { get; set; } = true;
    public int DefaultMaxAgeSeconds { get; set; } = 3600;
    public int MaxCacheBodyBytes { get; set; } = 4 * 1024 * 1024;
    public int MaxCacheSizeMegabytes { get; set; } = 500;
    public long FlushGeneration { get; set; } = 1;
}

public sealed class SiteHstsConfig
{
    public bool Enabled { get; set; } = false;
    public int MaxAgeSeconds { get; set; } = 31_536_000;
    public bool IncludeSubDomains { get; set; } = true;
    public bool Preload { get; set; } = false;
}

public sealed class UrlRewriteConfig
{
    public bool Enabled { get; set; } = true;
    public List<UrlRewriteRuleConfig> Rules { get; set; } = [];
}

public sealed class UrlRewriteRuleConfig
{
    public string Name { get; set; } = "Rule-1";
    public bool Enabled { get; set; } = true;
    public string Pattern { get; set; } = "^(.*)$";
    public bool NegatePattern { get; set; } = false;
    public bool RequireHttps { get; set; } = false;
    public UrlRewriteActionType ActionType { get; set; } = UrlRewriteActionType.Rewrite;
    public string Target { get; set; } = "/{R:1}";
    public int CustomStatusCode { get; set; } = 403;
    public bool StopProcessing { get; set; } = true;
}

public sealed class RedirectAndErrorsConfig
{
    public bool EnforceTrailingSlash { get; set; } = false;
    public string? GlobalRedirectUrl { get; set; }
    public bool PermanentRedirect { get; set; } = true;
    public CustomErrorMode ErrorMode { get; set; } = CustomErrorMode.PassThrough;
    public Dictionary<int, string> CustomErrorPages { get; set; } = [];
}

public sealed class MimeMappingsConfig
{
    public string FallbackContentType { get; set; } = "application/octet-stream";
    public Dictionary<string, string> CustomMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        [".wasm"] = "application/wasm",
        [".json"] = "application/json; charset=utf-8",
        [".br"] = "application/brotli",
        [".zst"] = "application/zstd",
        [".svg"] = "image/svg+xml"
    };
}

public sealed class CorsPolicyConfig
{
    public bool Enabled { get; set; } = false;
    public List<string> AllowedOrigins { get; set; } = [];
    public string AllowedMethods { get; set; } = "GET, POST, PUT, DELETE, OPTIONS";
    public string AllowedHeaders { get; set; } = "*";
    public bool AllowCredentials { get; set; } = false;
}

public sealed class TlsPolicyConfig
{
    public bool EnforceTls13 { get; set; } = false;
    public bool StrictSni { get; set; } = true;
    public bool RedirectHttpToHttps { get; set; } = false;
    public int RedirectHttpToHttpsStatusCode { get; set; } = 308;
    public bool EnableAcmeHttp01PassThrough { get; set; } = true;
    public AcmePolicyConfig Acme { get; set; } = new();
    public OnDemandTlsConfig OnDemandTls { get; set; } = new();
    public MtlsPolicyConfig Mtls { get; set; } = new();
}

public sealed class AcmePolicyConfig
{
    public bool Enabled { get; set; } = false;
    public string DirectoryUrl { get; set; } = "https://acme-v02.api.letsencrypt.org/directory";
    public string? ContactEmail { get; set; }
    public bool AcceptTermsOfService { get; set; } = false;
    public AcmeChallengeType ChallengeType { get; set; } = AcmeChallengeType.Http01;
    public int RenewBeforeDays { get; set; } = 30;
    public int AutoRenewCheckIntervalMinutes { get; set; } = 360;
    public string? DnsWebhookUrl { get; set; }
    public string? DnsWebhookAuthToken { get; set; }
    public List<string> Domains { get; set; } = [];
}

public sealed class OnDemandTlsConfig
{
    public bool Enabled { get; set; } = false;
    public string? AskEndpointUrl { get; set; }
    public List<string> AllowedHostSuffixes { get; set; } = [];
    public int CacheTtlSeconds { get; set; } = 300;
}

public sealed class MtlsPolicyConfig
{
    public MtlsClientCertificateMode ClientCertificateMode { get; set; } = MtlsClientCertificateMode.None;
    public List<string> TrustedThumbprints { get; set; } = [];
}

public sealed class AcmeAccountRecord
{
    public string? AccountUri { get; set; }
    public string PrivateKeyPkcs8Base64 { get; set; } = "";
}

public sealed class UpstreamClusterConfig
{
    public string Id { get; set; } = "default-cluster";
    public LoadBalancingPolicy LoadBalancingPolicy { get; set; } = LoadBalancingPolicy.PowerOfTwoChoices;
    public int RequestTimeoutSeconds { get; set; } = 30;
    public int RetryCount { get; set; } = 1;
    public string? StickyHeaderOrCookie { get; set; }
    public ActiveHealthCheckConfig ActiveHealthCheck { get; set; } = new();
    public PassiveCircuitBreakerConfig PassiveCircuitBreaker { get; set; } = new();
    public List<UpstreamDestinationConfig> Destinations { get; set; } = [];
}

public sealed class UpstreamDestinationConfig
{
    public string Id { get; set; } = "dest-1";
    public string Address { get; set; } = "http://127.0.0.1:5001";
    public int Weight { get; set; } = 1;
    public string? HealthCheckPath { get; set; }
}

public sealed class ActiveHealthCheckConfig
{
    public bool Enabled { get; set; } = false;
    public int IntervalSeconds { get; set; } = 10;
    public int TimeoutSeconds { get; set; } = 3;
    public string Path { get; set; } = "/healthz";
    public int ExpectedStatusCode { get; set; } = 200;
}

public sealed class PassiveCircuitBreakerConfig
{
    public bool Enabled { get; set; } = true;
    public int FailureThreshold { get; set; } = 3;
    public int EjectionSeconds { get; set; } = 30;
}

public sealed class AuthenticationConfig
{
    public bool AnonymousEnabled { get; set; } = true;
    public bool JwtEnabled { get; set; } = false;
    public string JwtSigningAlgorithm { get; set; } = "HS256";
    public string? JwtIssuer { get; set; }
    public string? JwtAudience { get; set; }
    public string? JwtSigningKey { get; set; }
    public bool ApiKeyEnabled { get; set; } = false;
    public string ApiKeyHeaderName { get; set; } = "X-API-Key";
    public List<string> AllowedApiKeys { get; set; } = [];
    public bool BasicAuthEnabled { get; set; } = false;
    public string BasicAuthRealm { get; set; } = "Kite Restricted";
    public Dictionary<string, string> BasicAuthUsers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class IpRestrictionsConfig
{
    public bool Enabled { get; set; } = false;
    public bool AllowUnmatched { get; set; } = true;
    public int DenyStatusCode { get; set; } = 403;
    public List<IpSubnetRuleConfig> Rules { get; set; } = [];
}

public sealed class IpSubnetRuleConfig
{
    public string Cidr { get; set; } = "127.0.0.1/32";
    public bool Allow { get; set; } = true;
    public string Description { get; set; } = "Loopback";
}

public sealed class RequestFilteringConfig
{
    public bool Enabled { get; set; } = true;
    public long MaxAllowedContentLength { get; set; } = 31_457_280;
    public int MaxUrlLength { get; set; } = 4096;
    public int MaxQueryStringLength { get; set; } = 2048;
    public bool AllowHighBitCharacters { get; set; } = true;
    public bool AllowDoubleEscaping { get; set; } = false;
    public List<string> AllowedVerbs { get; set; } = ["GET", "POST", "PUT", "DELETE", "HEAD", "OPTIONS", "PATCH"];
    public List<string> BlockedExtensions { get; set; } = [".config", ".env", ".pfx", ".key", ".pdb"];
}

public sealed class FailedRequestTracingConfig
{
    public bool Enabled { get; set; } = true;
    public string StatusCodeTrigger { get; set; } = "500-599";
    public int LatencyCutoffMilliseconds { get; set; } = 3000;
    public int MaxRetainedTraces { get; set; } = 50;
}

[JsonConverter(typeof(JsonStringEnumConverter<RateLimitAlgorithm>))]
public enum RateLimitAlgorithm
{
    SlidingWindow = 0,
    FixedWindow = 1,
    TokenBucket = 2,
    Concurrency = 3
}

public sealed class RateLimitingConfig
{
    public bool Enabled { get; set; } = false;
    public RateLimitAlgorithm Algorithm { get; set; } = RateLimitAlgorithm.SlidingWindow;
    public int PermitLimit { get; set; } = 100;
    public int WindowSeconds { get; set; } = 10;
    public int SegmentsPerWindow { get; set; } = 2;
    public int QueueLimit { get; set; } = 0;
    public string PartitionByHeader { get; set; } = "";
    public int RetryAfterSeconds { get; set; } = 10;
}

public sealed class SpaFallbackConfig
{
    public bool Enabled { get; set; } = false;
    public string FallbackDocument { get; set; } = "/index.html";
    public List<string> ExcludePrefixes { get; set; } = ["/api", "/assets", "/.well-known", "/__kite"];
}

public sealed class EdgeWafConfig
{
    public bool Enabled { get; set; } = false;
    public bool BlockSqlInjection { get; set; } = true;
    public bool BlockXss { get; set; } = true;
    public bool BlockPathTraversal { get; set; } = true;
    public bool InspectRequestBody { get; set; } = false;
    public int MaxBodyInspectBytes { get; set; } = 65536;
    public int MaxJsonDepth { get; set; } = 32;
    public List<string> BlockedUserAgents { get; set; } = [];
    public List<string> CustomBlockedPatterns { get; set; } = [];
}

/// <summary>Matches an incoming HTTP request to a gateway target with optional transforms, shadowing, and fault injection.</summary>
public sealed class SiteRouteRuleConfig
{
    public string Id { get; set; } = "route-1";
    public bool Enabled { get; set; } = true;
    public string PathPrefix { get; set; } = "/";
    public bool ExactPathOnly { get; set; } = false;
    public List<string> Methods { get; set; } = [];
    public Dictionary<string, string> MatchHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> MatchQueryParams { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? TargetClusterId { get; set; }
    public string? TargetPoolId { get; set; }
    public string? CanaryClusterId { get; set; }
    public int CanaryWeightPercent { get; set; } = 0;
    public string? ShadowClusterId { get; set; }
    public int ShadowPercent { get; set; } = 0;
    public RouteTransformConfig Transform { get; set; } = new();
    public FaultInjectionConfig FaultInjection { get; set; } = new();
}

public sealed class RouteTransformConfig
{
    public string? StripPathPrefix { get; set; }
    public string? RewritePathPrefix { get; set; }
    public Dictionary<string, string> SetRequestHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> RemoveRequestHeaders { get; set; } = [];
    public Dictionary<string, string> SetResponseHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> RemoveResponseHeaders { get; set; } = [];
}

public sealed class FaultInjectionConfig
{
    public bool Enabled { get; set; } = false;
    public int AbortPercent { get; set; } = 0;
    public int AbortStatusCode { get; set; } = 503;
    public string? AbortMessage { get; set; }
    public int DelayPercent { get; set; } = 0;
    public int DelayMilliseconds { get; set; } = 0;
}

public sealed class ForwardAuthConfig
{
    public bool Enabled { get; set; } = false;
    public string EndpointUrl { get; set; } = "";
    public int TimeoutMilliseconds { get; set; } = 2000;
    public List<string> CopyRequestHeaders { get; set; } = ["Authorization", "Cookie", "X-Forwarded-For", "X-Forwarded-Host"];
    public List<string> CopyResponseHeaders { get; set; } = ["X-Auth-User", "X-Auth-Roles", "X-Auth-Tenant"];
    public bool FailOpen { get; set; } = false;
}

public sealed class DistributedStateConfig
{
    public bool Enabled { get; set; } = false;
    public string RedisHost { get; set; } = "127.0.0.1";
    public int RedisPort { get; set; } = 6379;
    public string? Password { get; set; }
    public string KeyPrefix { get; set; } = "kite:";
    public int ConnectTimeoutMilliseconds { get; set; } = 1500;
}

public sealed class L4StreamProxyConfig
{
    public string Id { get; set; } = "tcp-stream-1";
    public bool Enabled { get; set; } = true;
    public int ListenPort { get; set; } = 9443;
    public bool EnableTlsSniRouting { get; set; } = true;
    public List<L4SniRouteConfig> SniRoutes { get; set; } = [];
    public string DefaultUpstreamHost { get; set; } = "127.0.0.1";
    public int DefaultUpstreamPort { get; set; } = 5432;
    public int ConnectTimeoutMilliseconds { get; set; } = 3000;
}

public sealed class L4SniRouteConfig
{
    public string SniHostname { get; set; } = "*";
    public string UpstreamHost { get; set; } = "127.0.0.1";
    public int UpstreamPort { get; set; } = 8443;
}

public sealed class PoolSidecarConfig
{
    public string Id { get; set; } = "sidecar-1";
    public bool Enabled { get; set; } = true;
    public string Executable { get; set; } = "";
    public string Arguments { get; set; } = "";
    public Dictionary<string, string> EnvironmentVariables { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class OtlpExportConfig
{
    public bool Enabled { get; set; } = false;
    public string EndpointUrl { get; set; } = "http://127.0.0.1:4318/v1/traces";
    public int ExportIntervalMilliseconds { get; set; } = 5000;
    public int MaxBatchSize { get; set; } = 128;
    public string ServiceName { get; set; } = "Kite.Server";
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class EdgePluginConfig
{
    public string Id { get; set; } = "plugin-1";
    public bool Enabled { get; set; } = true;
    public string AssemblyPath { get; set; } = "";
    public string? TypeName { get; set; }
    public int Order { get; set; } = 100;
    public Dictionary<string, string> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
