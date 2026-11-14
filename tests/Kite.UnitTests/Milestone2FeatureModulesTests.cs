using System.Net;
using System.Security.Cryptography;
using System.Text;
using Kite.Core.Models;
using Kite.Core.Routing;
using Kite.Server.HttpModules;
using Kite.Server.Pipeline;
using Kite.Server.Pools;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Xunit;

namespace Kite.UnitTests;

public sealed class Milestone2FeatureModulesTests : IDisposable
{
    private readonly string _tempDir;

    public Milestone2FeatureModulesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"kite-m2-unit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "public"));
        Directory.CreateDirectory(Path.Combine(_tempDir, "errors"));

        File.WriteAllText(Path.Combine(_tempDir, "index.html"), "<html>Root Home</html>");
        File.WriteAllText(Path.Combine(_tempDir, "public", "guide.html"), "<html>Guide Page</html>");
        File.WriteAllText(Path.Combine(_tempDir, "errors", "404.html"), "<html>Custom 404 Page</html>");
        File.WriteAllBytes(Path.Combine(_tempDir, "blob.custombin"), [0xDE, 0xAD, 0xBE, 0xEF]);
    }

    [Fact]
    public async Task UrlRewrite_AndRedirects_HandleRegexRewrite_301Redirect_AndTrailingSlash()
    {
        var site = CreateBaseSite();
        site.Features.RedirectsAndErrors.EnforceTrailingSlash = true;
        site.Features.UrlRewrite.Enabled = true;
        site.Features.UrlRewrite.Rules =
        [
            new UrlRewriteRuleConfig
            {
                Name = "LegacyRedirect",
                Pattern = "^/legacy/(.*)$",
                ActionType = UrlRewriteActionType.Redirect301,
                Target = "/v2/{R:1}"
            },
            new UrlRewriteRuleConfig
            {
                Name = "DocsInternalRewrite",
                Pattern = "^/docs/(.*)$",
                ActionType = UrlRewriteActionType.Rewrite,
                Target = "/public/{R:1}"
            }
        ];

        var table = new HostRouteTable([site]);
        using var pipeline = new KiteRequestPipeline(() => table, _ => null);

        // 1. Trailing slash redirect when EnforceTrailingSlash = true (and no file extension)
        DefaultHttpContext slashCtx = CreateCtx("GET", "/about");
        await pipeline.ExecuteAsync(slashCtx);
        Assert.Equal(StatusCodes.Status301MovedPermanently, slashCtx.Response.StatusCode);
        Assert.Equal("/about/", slashCtx.Response.Headers["Location"].ToString());

        // 2. Regex 301 Redirect with {R:1}
        DefaultHttpContext redirCtx = CreateCtx("GET", "/legacy/catalog/items.html");
        await pipeline.ExecuteAsync(redirCtx);
        Assert.Equal(StatusCodes.Status301MovedPermanently, redirCtx.Response.StatusCode);
        Assert.Equal("/v2/catalog/items.html", redirCtx.Response.Headers["Location"].ToString());

        // 3. In-place Rewrite to static /public/guide.html
        DefaultHttpContext rewriteCtx = CreateCtx("GET", "/docs/guide.html");
        await pipeline.ExecuteAsync(rewriteCtx);
        Assert.Equal(StatusCodes.Status200OK, rewriteCtx.Response.StatusCode);
        Assert.Contains("Guide Page", ReadBody(rewriteCtx));
    }

    [Fact]
    public async Task IpRestrictions_AndRequestFiltering_EnforceSubnets_Verbs_Size_DoubleEscaping_AndExtensions()
    {
        var site = CreateBaseSite();
        site.Features.IpRestrictions = new IpRestrictionsConfig
        {
            Enabled = true,
            AllowUnmatched = false,
            DenyStatusCode = 403,
            Rules = [new IpSubnetRuleConfig { Cidr = "10.240.0.0/16", Allow = true, Description = "VPC" }]
        };
        site.Features.RequestFiltering = new RequestFilteringConfig
        {
            Enabled = true,
            AllowedVerbs = ["GET", "POST"],
            MaxAllowedContentLength = 512,
            AllowDoubleEscaping = false,
            BlockedExtensions = [".env", ".config"]
        };

        var table = new HostRouteTable([site]);
        using var pipeline = new KiteRequestPipeline(() => table, _ => null);

        // 1. Allowed subnet 10.240.12.5 passes
        DefaultHttpContext okCtx = CreateCtx("GET", "/index.html", "10.240.12.5");
        await pipeline.ExecuteAsync(okCtx);
        Assert.Equal(StatusCodes.Status200OK, okCtx.Response.StatusCode);

        // 2. Disallowed IP 192.168.1.9 gets 403.6
        DefaultHttpContext ipBlocked = CreateCtx("GET", "/index.html", "192.168.1.9");
        await pipeline.ExecuteAsync(ipBlocked);
        Assert.Equal(StatusCodes.Status403Forbidden, ipBlocked.Response.StatusCode);
        Assert.Equal("403.6", ipBlocked.Response.Headers["X-Kite-SubStatus"].ToString());

        // 3. Disallowed verb PUT gets 405
        DefaultHttpContext verbBlocked = CreateCtx("PUT", "/index.html", "10.240.1.1");
        await pipeline.ExecuteAsync(verbBlocked);
        Assert.Equal(StatusCodes.Status405MethodNotAllowed, verbBlocked.Response.StatusCode);

        // 4. Payload too large gets 413
        DefaultHttpContext tooBig = CreateCtx("POST", "/index.html", "10.240.1.1");
        tooBig.Request.ContentLength = 2048;
        await pipeline.ExecuteAsync(tooBig);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, tooBig.Response.StatusCode);

        // 5. Double escaping blocked with 404.11
        DefaultHttpContext doubleEsc = CreateCtx("GET", "/index.html", "10.240.1.1");
        doubleEsc.Features.Get<IHttpRequestFeature>()!.RawTarget = "/files/%252e%252e/secret";
        await pipeline.ExecuteAsync(doubleEsc);
        Assert.Equal(StatusCodes.Status404NotFound, doubleEsc.Response.StatusCode);
        Assert.Equal("404.11", doubleEsc.Response.Headers["X-Kite-SubStatus"].ToString());

        // 6. Blocked extension .env returns 404.7
        DefaultHttpContext extBlocked = CreateCtx("GET", "/secrets.env", "10.240.1.1");
        await pipeline.ExecuteAsync(extBlocked);
        Assert.Equal(StatusCodes.Status404NotFound, extBlocked.Response.StatusCode);
        Assert.Equal("404.7", extBlocked.Response.Headers["X-Kite-SubStatus"].ToString());
    }

    [Fact]
    public async Task EdgeAuthentication_EnforcesJwtHs256_ApiKey_AndBasicAuth()
    {
        string jwtSecret = "super-secret-kite-signing-key-256bit!!";
        string basicPassHash = Convert.ToHexString(SHA256.HashData("kite-admin-pass"u8));

        var site = CreateBaseSite();
        site.Features.Authentication = new AuthenticationConfig
        {
            AnonymousEnabled = false,
            JwtEnabled = true,
            JwtSigningAlgorithm = "HS256",
            JwtIssuer = "https://auth.kite.local",
            JwtAudience = "kite-api",
            JwtSigningKey = jwtSecret,
            ApiKeyEnabled = true,
            ApiKeyHeaderName = "X-API-Key",
            AllowedApiKeys = ["kite-key-999"],
            BasicAuthEnabled = true,
            BasicAuthRealm = "Kite Admin",
            BasicAuthUsers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["admin"] = $"sha256:{basicPassHash}"
            }
        };

        var fakeTime = new FakeClock();
        var table = new HostRouteTable([site]);
        using var pipeline = new KiteRequestPipeline(() => table, _ => null, fakeTime);

        // 1. Unauthenticated -> 401
        DefaultHttpContext anonCtx = CreateCtx("GET", "/index.html");
        await pipeline.ExecuteAsync(anonCtx);
        Assert.Equal(StatusCodes.Status401Unauthorized, anonCtx.Response.StatusCode);
        Assert.Contains("Bearer", anonCtx.Response.Headers["WWW-Authenticate"].ToString());

        // 2. Valid HS256 JWT -> 200 OK
        long exp = fakeTime.GetUtcNow().AddMinutes(10).ToUnixTimeSeconds();
        string validJwt = CreateHs256Jwt(jwtSecret, "https://auth.kite.local", "kite-api", "alice", exp);
        DefaultHttpContext jwtCtx = CreateCtx("GET", "/index.html");
        jwtCtx.Request.Headers["Authorization"] = $"Bearer {validJwt}";
        await pipeline.ExecuteAsync(jwtCtx);
        Assert.Equal(StatusCodes.Status200OK, jwtCtx.Response.StatusCode);
        Assert.Equal("alice", jwtCtx.Request.Headers["X-Kite-Auth-Principal"].ToString());

        // 3. Expired JWT -> 401
        fakeTime.Advance(TimeSpan.FromMinutes(15));
        DefaultHttpContext expiredCtx = CreateCtx("GET", "/index.html");
        expiredCtx.Request.Headers["Authorization"] = $"Bearer {validJwt}";
        await pipeline.ExecuteAsync(expiredCtx);
        Assert.Equal(StatusCodes.Status401Unauthorized, expiredCtx.Response.StatusCode);

        // 4. Valid API Key -> 200 OK
        DefaultHttpContext keyCtx = CreateCtx("GET", "/index.html");
        keyCtx.Request.Headers["X-API-Key"] = "kite-key-999";
        await pipeline.ExecuteAsync(keyCtx);
        Assert.Equal(StatusCodes.Status200OK, keyCtx.Response.StatusCode);

        // 5. Valid Basic Auth -> 200 OK
        DefaultHttpContext basicCtx = CreateCtx("GET", "/index.html");
        basicCtx.Request.Headers["Authorization"] = "Basic " + Convert.ToBase64String("admin:kite-admin-pass"u8);
        await pipeline.ExecuteAsync(basicCtx);
        Assert.Equal(StatusCodes.Status200OK, basicCtx.Response.StatusCode);
        Assert.Equal("admin", basicCtx.Request.Headers["X-Kite-Auth-Principal"].ToString());
    }

    [Fact]
    public async Task CustomHeaders_Cors_Mime_CustomErrorPages_CacheFlush_AndFrebTracing_WorkEndToEnd()
    {
        var capturedFreb = new List<FrebTraceRecord>();

        var serverCfg = new ServerConfig();
        serverCfg.Features.StripServerHeader = true;
        serverCfg.Features.CustomHeaders["X-Edge-Node"] = "Kite-M2";
        serverCfg.Features.Cors = new CorsPolicyConfig
        {
            Enabled = true,
            AllowedOrigins = ["https://studio.kite.local"],
            AllowedMethods = "GET, POST, OPTIONS",
            AllowedHeaders = "Content-Type, Authorization",
            AllowCredentials = true
        };
        serverCfg.Features.MimeMappings.CustomMappings[".custombin"] = "application/x-kite-binary";
        serverCfg.Features.RedirectsAndErrors.ErrorMode = CustomErrorMode.CustomPages;
        serverCfg.Features.RedirectsAndErrors.CustomErrorPages[404] = "/errors/404.html";
        serverCfg.Features.FailedRequestTracing = new FailedRequestTracingConfig
        {
            Enabled = true,
            StatusCodeTrigger = "500-599",
            LatencyCutoffMilliseconds = 3000
        };

        SiteConfig site = CreateBaseSite();
        site.NestedApplications.Add(new SiteApplicationConfig
        {
            Path = "/fault",
            PoolId = "throwing-pool",
            HostingMode = WorkloadHostingMode.OutOfProcessProxy
        });

        var throwingPoolCfg = new ApplicationPoolConfig { Id = "throwing-pool" };
        await using var supervisor = new ApplicationPoolSupervisor(throwingPoolCfg, new FailingWorkerLauncher());
        await supervisor.StartOrReconcileAsync(throwingPoolCfg, site.NestedApplications[0]);

        var table = new HostRouteTable([site], serverCfg);
        using var pipeline = new KiteRequestPipeline(
            () => table,
            id => id == "throwing-pool" ? supervisor : null,
            frebSink: rec => capturedFreb.Add(rec));

        // 1. CORS Preflight + Server Header Stripped + Custom Header
        DefaultHttpContext corsCtx = CreateCtx("OPTIONS", "/index.html");
        corsCtx.Request.Headers["Origin"] = "https://studio.kite.local";
        corsCtx.Request.Headers["Access-Control-Request-Method"] = "POST";
        await pipeline.ExecuteAsync(corsCtx);
        Assert.Equal(StatusCodes.Status204NoContent, corsCtx.Response.StatusCode);
        Assert.Equal("https://studio.kite.local", corsCtx.Response.Headers["Access-Control-Allow-Origin"].ToString());
        Assert.Equal("Kite-M2", corsCtx.Response.Headers["X-Edge-Node"].ToString());
        Assert.False(corsCtx.Response.Headers.ContainsKey("Server"));

        // 2. Custom MIME mapping
        DefaultHttpContext mimeCtx = CreateCtx("GET", "/blob.custombin");
        await pipeline.ExecuteAsync(mimeCtx);
        Assert.Equal(StatusCodes.Status200OK, mimeCtx.Response.StatusCode);
        Assert.Equal("application/x-kite-binary", mimeCtx.Response.ContentType);

        // 3. Custom Error Page 404
        DefaultHttpContext errCtx = CreateCtx("GET", "/does-not-exist.html");
        await pipeline.ExecuteAsync(errCtx);
        Assert.Equal(StatusCodes.Status404NotFound, errCtx.Response.StatusCode);
        Assert.Contains("Custom 404 Page", ReadBody(errCtx));

        // 4. Cache Hit then FlushGeneration bump invalidation
        DefaultHttpContext miss1 = CreateCtx("GET", "/index.html");
        await pipeline.ExecuteAsync(miss1);
        Assert.Equal(StatusCodes.Status200OK, miss1.Response.StatusCode);

        DefaultHttpContext hit1 = CreateCtx("GET", "/index.html");
        await pipeline.ExecuteAsync(hit1);
        Assert.Equal("HIT", hit1.Response.Headers["X-Kite-Cache"].ToString());

        serverCfg.Features.Caching.FlushGeneration = 2;
        var nextTable = new HostRouteTable([site], serverCfg);
        table = nextTable;

        DefaultHttpContext afterFlush = CreateCtx("GET", "/index.html");
        await pipeline.ExecuteAsync(afterFlush);
        Assert.False(afterFlush.Response.Headers.ContainsKey("X-Kite-Cache"));

        // 5. Failing upstream throws HttpRequestException, captures 500 FREB trace with full pipeline stages and CLR stack trace
        DefaultHttpContext faultCtx = CreateCtx("GET", "/fault/crash");
        await pipeline.ExecuteAsync(faultCtx);
        Assert.Equal(StatusCodes.Status500InternalServerError, faultCtx.Response.StatusCode);
        Assert.Single(capturedFreb);
        Assert.Equal(500, capturedFreb[0].StatusCode);
        Assert.Contains("HttpRequestException", capturedFreb[0].ExceptionStackTrace);
        Assert.Contains(capturedFreb[0].Stages, s => s.StageName == "ExecuteRequestHandler" && s.Outcome.Contains("CRASH"));
    }

    [Fact]
    public async Task ApplicationPoolSupervisor_SupportsOnDemandStartMode()
    {
        var fakeTime = new FakeClock();
        var launcher = new RealWorkerProcessLauncher();
        var poolCfg = new ApplicationPoolConfig
        {
            Id = "ondemand-pool",
            DesiredState = DesiredResourceState.Started,
            ProcessModel = new PoolProcessModelConfig
            {
                StartMode = PoolStartMode.OnDemand,
                WorkerCount = 1
            }
        };

        var appCfg = new SiteApplicationConfig
        {
            Path = "/api",
            PoolId = "ondemand-pool",
            HostingMode = WorkloadHostingMode.KiteWorkerIpc
        };

        await using var supervisor = new ApplicationPoolSupervisor(poolCfg, launcher, fakeTime);
        await supervisor.StartOrReconcileAsync(poolCfg, appCfg);

        // Should stay Idle with 0 workers until first incoming request
        Assert.Equal(RuntimeResourceState.Idle, supervisor.ActualState);
        Assert.Empty(supervisor.GetStatusSnapshot().ActiveWorkers);

        DefaultHttpContext ctx = CreateCtx("GET", "/api/ping");
        await supervisor.ForwardHttpAsync(ctx, appCfg, "/ping".AsMemory());
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Equal(RuntimeResourceState.Started, supervisor.ActualState);
        Assert.Single(supervisor.GetStatusSnapshot().ActiveWorkers);
    }

    private SiteConfig CreateBaseSite() => new()
    {
        Id = "m2-site",
        Name = "Milestone 2 Test Site",
        DesiredState = DesiredResourceState.Started,
        Bindings = [new SiteBindingConfig { Protocol = "http", IpAddress = "*", Port = 8080, Host = "*" }],
        RootApplication = new SiteApplicationConfig
        {
            Path = "/",
            PoolId = "default-pool",
            PhysicalPath = _tempDir,
            HostingMode = WorkloadHostingMode.StaticOnly
        }
    };

    private static DefaultHttpContext CreateCtx(string method, string path, string remoteIp = "127.0.0.1")
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Scheme = "http";
        ctx.Request.Headers["Host"] = "localhost:8080";
        ctx.Connection.LocalPort = 8080;
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string ReadBody(HttpContext ctx) =>
        Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray());

    private static string CreateHs256Jwt(string secret, string iss, string aud, string sub, long exp)
    {
        string h = EdgeAuthenticationModule.EncodeBase64Url("""{"alg":"HS256","typ":"JWT"}"""u8);
        string p = EdgeAuthenticationModule.EncodeBase64Url(Encoding.UTF8.GetBytes($$"""{"iss":"{{iss}}","aud":"{{aud}}","sub":"{{sub}}","exp":{{exp}}}"""));
        byte[] sigInput = Encoding.ASCII.GetBytes($"{h}.{p}");
        byte[] hmac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), sigInput);
        return $"{h}.{p}.{EdgeAuthenticationModule.EncodeBase64Url(hmac)}";
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }

    private sealed class FailingWorkerLauncher : IWorkerProcessLauncher
    {
        public ValueTask<IWorkerInstance> LaunchWorkerAsync(
            ApplicationPoolConfig pool,
            SiteApplicationConfig? appConfig,
            string workerId,
            DateTimeOffset startedAtUtc,
            CancellationToken ct) =>
            ValueTask.FromResult<IWorkerInstance>(new ThrowingPipeWorker(workerId, startedAtUtc));

        private sealed class ThrowingPipeWorker(string id, DateTimeOffset started) : IWorkerInstance
        {
            public string WorkerId => id;
            public int ProcessId => Environment.ProcessId;
            public string TransportAddress => "http://127.0.0.1:65432";
            public WorkloadHostingMode HostingMode => WorkloadHostingMode.OutOfProcessProxy;
            public DateTimeOffset StartedAtUtc => started;
            public bool HasExited => false;
            public IReadOnlyDictionary<string, string> InjectedGcEnvironment { get; } = new Dictionary<string, string>();
            public event Action<IWorkerInstance>? Exited { add { } remove { } }
            public long GetWorkingSetBytes() => 1024;
            public ValueTask<bool> ProbeReadinessAsync(CancellationToken ct) => ValueTask.FromResult(true);
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 11, 22, 0, 0, TimeSpan.Zero);
        public void Advance(TimeSpan delta) => _now += delta;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
