using System.IO.Compression;
using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Kite.Core.Models;
using Kite.Core.Routing;
using Kite.Server.HttpModules;
using Kite.Server.Pipeline;
using Kite.Server.Pools;
using Kite.Server.Tls;
using Kite.Server.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Kite.SpecTests;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class RfcFactAttribute(string rfc, string section, params string[] requirementIds) : FactAttribute
{
    public string Rfc { get; } = rfc;
    public string Section { get; } = section;
    public IReadOnlyList<string> RequirementIds { get; } = requirementIds;
}

public sealed record RfcRequirementCatalogItem(
    string Id,
    string Rfc,
    string Section,
    string Level,
    string Description);

public sealed class RfcComplianceSuiteTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SiteConfig _site;
    private readonly HostRouteTable _routeTable;
    private readonly FakeTimeProvider _fakeTime = new();
    private readonly KiteRequestPipeline _pipeline;

    public RfcComplianceSuiteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"kite-spec-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        File.WriteAllText(
            Path.Combine(_tempDir, "index.html"),
            "<html><body><h1>Hello RFC 9110 World! This payload is long enough to trigger compression.</h1><p>" +
            new string('A', 256) +
            "</p></body></html>");

        File.WriteAllBytes(
            Path.Combine(_tempDir, "range-test.txt"),
            Encoding.ASCII.GetBytes("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ"));

        _site = new SiteConfig
        {
            Id = "rfc-site",
            Name = "RFC Verification Site",
            DesiredState = DesiredResourceState.Started,
            Bindings =
            [
                new SiteBindingConfig { Protocol = "http", IpAddress = "*", Port = 8080, Host = "example.local" },
                new SiteBindingConfig
                {
                    Protocol = "https",
                    IpAddress = "*",
                    Port = 8443,
                    Host = "secure.example.local",
                    Tls = new SiteTlsConfig { UseSelfSignedDevelopmentCert = true, AllowHttp2 = true }
                }
            ],
            RootApplication = new SiteApplicationConfig
            {
                Path = "/",
                PoolId = "default-pool",
                PhysicalPath = _tempDir,
                HostingMode = WorkloadHostingMode.StaticOnly
            },
            Features = new SiteFeaturesConfig
            {
                Compression = new SiteCompressionConfig { Enabled = true, MinSizeBytes = 64, Encodings = ["zstd", "br", "gzip", "deflate"] },
                Caching = new SiteCachingConfig { Enabled = true, DefaultMaxAgeSeconds = 120 },
                Hsts = new SiteHstsConfig { Enabled = true, MaxAgeSeconds = 31536000, IncludeSubDomains = true, Preload = true }
            }
        };

        _routeTable = new HostRouteTable([_site]);
        _pipeline = new KiteRequestPipeline(() => _routeTable, _ => null, _fakeTime);
    }

    [Fact]
    public void MetaTest_EveryCatalogedRfcRequirement_HasPassingSpecTest()
    {
        string repoRoot = FindRepoRoot();
        string catalogPath = Path.Combine(repoRoot, "specs", "rfc-requirements.json");
        Assert.True(File.Exists(catalogPath), $"Catalog not found at {catalogPath}");

        var items = JsonSerializer.Deserialize<List<RfcRequirementCatalogItem>>(
            File.ReadAllText(catalogPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

        Assert.NotEmpty(items);

        HashSet<string> implementedIds = typeof(RfcComplianceSuiteTests).Assembly
            .GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .SelectMany(m => m.GetCustomAttributes<RfcFactAttribute>())
            .SelectMany(a => a.RequirementIds)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (RfcRequirementCatalogItem item in items)
        {
            Assert.True(
                implementedIds.Contains(item.Id),
                $"Missing [RfcFact] test for catalog requirement '{item.Id}' ({item.Rfc} Section {item.Section}: {item.Description})");
        }
    }

    [RfcFact("RFC 9110", "9.3.7", "RFC9110-9.3.7-MUST-1")]
    public async Task Options_Returns204WithAllowHeader()
    {
        DefaultHttpContext ctx = CreateContext("OPTIONS", "/index.html");
        await _pipeline.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status204NoContent, ctx.Response.StatusCode);
        Assert.Contains("GET", ctx.Response.Headers["Allow"].ToString());
        Assert.Contains("OPTIONS", ctx.Response.Headers["Allow"].ToString());
    }

    [RfcFact("RFC 9110", "9.3.8", "RFC9110-9.3.8-MUST-1")]
    public async Task Trace_IsRejectedWith405ProblemDetails()
    {
        DefaultHttpContext ctx = CreateContext("TRACE", "/index.html");
        await _pipeline.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status405MethodNotAllowed, ctx.Response.StatusCode);
        Assert.Contains("application/problem+json", ctx.Response.ContentType);
    }

    [RfcFact("RFC 9110", "12.5.3", "RFC9110-12.5.3-MUST-1")]
    public void AcceptEncoding_HonorsQualityValuesAndRejectsZeroQuality()
    {
        var headers = new HeaderDictionary
        {
            ["Accept-Encoding"] = "zstd;q=0, br;q=0.4, gzip;q=0.9"
        };

        string selected = KiteCompressionModule.NegotiateEncoding(headers, _site.Features.Compression);
        Assert.Equal("gzip", selected);
    }

    [RfcFact("RFC 9110", "13.1.1", "RFC9110-13.1.1-MUST-1")]
    public async Task IfMatch_Mismatch_Returns412PreconditionFailed()
    {
        DefaultHttpContext ctx = CreateContext("GET", "/range-test.txt");
        ctx.Request.Headers["If-Match"] = "\"non-matching-etag\"";
        await _pipeline.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status412PreconditionFailed, ctx.Response.StatusCode);
    }

    [RfcFact("RFC 9110", "13.1.2", "RFC9110-13.1.2-MUST-1")]
    public async Task IfNoneMatch_WeakOrStrongMatch_Returns304NotModified()
    {
        DefaultHttpContext first = CreateContext("GET", "/range-test.txt");
        await _pipeline.ExecuteAsync(first);
        string etag = first.Response.Headers["ETag"].ToString();
        Assert.NotEmpty(etag);

        DefaultHttpContext second = CreateContext("GET", "/range-test.txt");
        second.Request.Headers["If-None-Match"] = $"W/{etag}";
        await _pipeline.ExecuteAsync(second);

        Assert.Equal(StatusCodes.Status304NotModified, second.Response.StatusCode);
    }

    [RfcFact("RFC 9110", "13.1.3", "RFC9110-13.1.3-MUST-1", "RFC9110-13.2.2-MUST-1")]
    public void PreconditionPrecedence_IfNoneMatchOverridesIfModifiedSince()
    {
        DateTimeOffset lastMod = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        string etag = "\"abc-123\"";

        var headers = new HeaderDictionary
        {
            // If-None-Match does NOT match current ETag, while If-Modified-Since is newer than lastMod.
            // Per RFC 9110 Section 13.1.3, If-Modified-Since MUST be ignored because If-None-Match is present!
            ["If-None-Match"] = "\"different-etag\"",
            ["If-Modified-Since"] = lastMod.AddHours(5).ToString("R")
        };

        PreconditionOutcome outcome = ConditionalAndRangeEvaluator.EvaluatePreconditions(headers, "GET", etag, lastMod);
        Assert.Equal(PreconditionOutcome.Proceed, outcome);
    }

    [RfcFact("RFC 9110", "14.1.2", "RFC9110-14.1.2-MUST-1")]
    public async Task RangeRequest_SatisfiableSliceAndSuffix_Returns206PartialContent()
    {
        // Bytes 10..15 of "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ" -> "ABCDEF"
        DefaultHttpContext ctx = CreateContext("GET", "/range-test.txt");
        ctx.Request.Headers["Range"] = "bytes=10-15";
        await _pipeline.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status206PartialContent, ctx.Response.StatusCode);
        Assert.Equal("bytes 10-15/36", ctx.Response.Headers["Content-Range"].ToString());
        Assert.Equal("ABCDEF", ReadResponseBody(ctx));

        // Suffix range: last 4 bytes -> "WXYZ"
        DefaultHttpContext suffixCtx = CreateContext("GET", "/range-test.txt");
        suffixCtx.Request.Headers["Range"] = "bytes=-4";
        await _pipeline.ExecuteAsync(suffixCtx);

        Assert.Equal(StatusCodes.Status206PartialContent, suffixCtx.Response.StatusCode);
        Assert.Equal("bytes 32-35/36", suffixCtx.Response.Headers["Content-Range"].ToString());
        Assert.Equal("WXYZ", ReadResponseBody(suffixCtx));
    }

    [RfcFact("RFC 9110", "14.4", "RFC9110-14.4-MUST-1")]
    public async Task RangeRequest_OutOfBounds_Returns416WithContentRangeStar()
    {
        DefaultHttpContext ctx = CreateContext("GET", "/range-test.txt");
        ctx.Request.Headers["Range"] = "bytes=500-600";
        await _pipeline.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status416RangeNotSatisfiable, ctx.Response.StatusCode);
        Assert.Equal("bytes */36", ctx.Response.Headers["Content-Range"].ToString());
    }

    [RfcFact("RFC 9110", "13.1.5", "RFC9110-13.1.5-MUST-1")]
    public async Task IfRange_MatchingETagServes206_MismatchedETagServesFull200()
    {
        DefaultHttpContext initial = CreateContext("GET", "/range-test.txt");
        initial.Request.Headers["Cache-Control"] = "no-cache";
        await _pipeline.ExecuteAsync(initial);
        string etag = initial.Response.Headers["ETag"].ToString();

        // Matching If-Range -> 206 Partial Content
        DefaultHttpContext matchCtx = CreateContext("GET", "/range-test.txt");
        matchCtx.Request.Headers["Range"] = "bytes=0-3";
        matchCtx.Request.Headers["If-Range"] = etag;
        await _pipeline.ExecuteAsync(matchCtx);
        Assert.Equal(StatusCodes.Status206PartialContent, matchCtx.Response.StatusCode);
        Assert.Equal("0123", ReadResponseBody(matchCtx));

        // Mismatched If-Range -> 200 OK full representation
        DefaultHttpContext mismatchCtx = CreateContext("GET", "/range-test.txt");
        mismatchCtx.Request.Headers["Range"] = "bytes=0-3";
        mismatchCtx.Request.Headers["If-Range"] = "\"stale-etag\"";
        await _pipeline.ExecuteAsync(mismatchCtx);
        Assert.Equal(StatusCodes.Status200OK, mismatchCtx.Response.StatusCode);
        Assert.Equal(36, ReadResponseBody(mismatchCtx).Length);
    }

    [RfcFact("RFC 9111", "4.1", "RFC9111-4.1-MUST-1", "RFC9111-5.2.1-MUST-1", "RFC9111-4.4-MUST-1")]
    public async Task OutputCache_StoresHitsCalculatesAge_HonorsNoCache_AndInvalidatesOnUnsafeMethod()
    {
        // 1. Initial GET populates cache
        DefaultHttpContext req1 = CreateContext("GET", "/index.html");
        await _pipeline.ExecuteAsync(req1);
        Assert.Equal(StatusCodes.Status200OK, req1.Response.StatusCode);

        // Advance virtual clock by 35 seconds
        _fakeTime.Advance(TimeSpan.FromSeconds(35));

        // 2. Second GET hits cache and emits Age: 35
        DefaultHttpContext req2 = CreateContext("GET", "/index.html");
        await _pipeline.ExecuteAsync(req2);
        Assert.Equal("HIT", req2.Response.Headers["X-Kite-Cache"].ToString());
        Assert.Equal("35", req2.Response.Headers["Age"].ToString());

        // 3. Request with Cache-Control: no-cache bypasses cache
        DefaultHttpContext reqNoCache = CreateContext("GET", "/index.html");
        reqNoCache.Request.Headers["Cache-Control"] = "no-cache";
        await _pipeline.ExecuteAsync(reqNoCache);
        Assert.NotEqual("HIT", reqNoCache.Response.Headers["X-Kite-Cache"].ToString());

        // 4. Unsafe method (DELETE) invalidates cache for /index.html
        DefaultHttpContext reqDelete = CreateContext("DELETE", "/index.html");
        await _pipeline.ExecuteAsync(reqDelete);
        Assert.Equal(0, _pipeline.OutputCache.Count);
    }

    [RfcFact("RFC 9111", "4.1", "RFC9111-4.1-VARY-1", "RFC7932-BROTLI-1", "RFC8878-ZSTD-1", "RFC1952-GZIP-1")]
    public async Task CompressionAndVary_SupportsZstdBrotliAndGzipWithoutCacheCollision()
    {
        // 1. Request Zstandard (RFC 8878)
        DefaultHttpContext zstdCtx = CreateContext("GET", "/index.html");
        zstdCtx.Request.Headers["Accept-Encoding"] = "zstd";
        await _pipeline.ExecuteAsync(zstdCtx);
        Assert.Equal("zstd", zstdCtx.Response.Headers["Content-Encoding"].ToString());
        Assert.Contains("Accept-Encoding", zstdCtx.Response.Headers["Vary"].ToString());

        byte[] zstdBytes = ReadResponseBytes(zstdCtx);
        // Verify RFC 8878 Magic_Number (0xFD2FB528 little-endian: 0x28, 0xB5, 0x2F, 0xFD)
        Assert.True(zstdBytes.Length > 12);
        Assert.Equal(0x28, zstdBytes[0]);
        Assert.Equal(0xB5, zstdBytes[1]);
        Assert.Equal(0x2F, zstdBytes[2]);
        Assert.Equal(0xFD, zstdBytes[3]);

        // 2. Request Brotli (RFC 7932)
        DefaultHttpContext brCtx = CreateContext("GET", "/index.html");
        brCtx.Request.Headers["Accept-Encoding"] = "br";
        await _pipeline.ExecuteAsync(brCtx);
        Assert.Equal("br", brCtx.Response.Headers["Content-Encoding"].ToString());

        using var brDecompressor = new BrotliStream(new MemoryStream(ReadResponseBytes(brCtx)), CompressionMode.Decompress);
        using var brReader = new StreamReader(brDecompressor);
        Assert.Contains("Hello RFC 9110 World!", await brReader.ReadToEndAsync());

        // 3. Request Gzip (RFC 1952)
        DefaultHttpContext gzipCtx = CreateContext("GET", "/index.html");
        gzipCtx.Request.Headers["Accept-Encoding"] = "gzip";
        await _pipeline.ExecuteAsync(gzipCtx);
        Assert.Equal("gzip", gzipCtx.Response.Headers["Content-Encoding"].ToString());

        byte[] gzBytes = ReadResponseBytes(gzipCtx);
        Assert.Equal(0x1F, gzBytes[0]);
        Assert.Equal(0x8B, gzBytes[1]);
    }

    [RfcFact("RFC 9112", "5.4", "RFC9112-5.4-MUST-1", "RFC9457-PROBLEM-1")]
    public async Task MissingOrMultipleHostHeaders_RejectedWith400ProblemDetails()
    {
        // Missing Host header
        var missingHostCtx = new DefaultHttpContext();
        missingHostCtx.Request.Method = "GET";
        missingHostCtx.Request.Path = "/index.html";
        missingHostCtx.Response.Body = new MemoryStream();
        await _pipeline.ExecuteAsync(missingHostCtx);

        Assert.Equal(StatusCodes.Status400BadRequest, missingHostCtx.Response.StatusCode);
        Assert.Contains("application/problem+json", missingHostCtx.Response.ContentType);
        string problemJson = ReadResponseBody(missingHostCtx);
        Assert.Contains("\"status\":400", problemJson);
        Assert.Contains("RFC 9112", problemJson);

        // Multiple Host headers
        var multiHostCtx = new DefaultHttpContext();
        multiHostCtx.Request.Method = "GET";
        multiHostCtx.Request.Path = "/index.html";
        multiHostCtx.Request.Headers["Host"] = new StringValues(["example.local", "evil.local"]);
        multiHostCtx.Response.Body = new MemoryStream();
        await _pipeline.ExecuteAsync(multiHostCtx);

        Assert.Equal(StatusCodes.Status400BadRequest, multiHostCtx.Response.StatusCode);
    }

    [RfcFact("RFC 9113", "3.2", "RFC9113-3.2-ALPN-1", "RFC6797-HSTS-1")]
    public async Task Http2AuthorityRouting_AndHstsHeader_AreEnforced()
    {
        var h2Ctx = new DefaultHttpContext();
        h2Ctx.Request.Protocol = "HTTP/2";
        h2Ctx.Request.Scheme = "https";
        h2Ctx.Request.Method = "GET";
        h2Ctx.Request.Host = new HostString("secure.example.local", 8443);
        h2Ctx.Connection.LocalPort = 8443;
        h2Ctx.Request.Path = "/index.html";
        h2Ctx.Response.Body = new MemoryStream();

        await _pipeline.ExecuteAsync(h2Ctx);

        Assert.Equal(StatusCodes.Status200OK, h2Ctx.Response.StatusCode);
        Assert.Equal(
            "max-age=31536000; includeSubDomains; preload",
            h2Ctx.Response.Headers["Strict-Transport-Security"].ToString());
    }

    [RfcFact("RFC 6066", "3", "RFC6066-SNI-1")]
    public void SniSelector_MatchesExactHostnameAndGeneratesSanCertificate()
    {
        Assert.True(_routeTable.TryMatchSni("secure.example.local".AsSpan(), out CompiledSiteRoute? route));
        Assert.NotNull(route);
        Assert.Equal("rfc-site", route.Site.Id);

        using var certProvider = new CertificateProvider();
        using var cert = certProvider.GetOrCreateCertificate(route.Binding);
        Assert.Contains("secure.example.local", cert.Subject);
    }

    [RfcFact("RFC 7239", "4", "RFC7239-FWD-1")]
    public async Task WorkloadDispatcher_InjectsRfc7239ForwardedHeader()
    {
        string? capturedForwardedHeader = null;
        var workerLauncher = new RealWorkerProcessLauncher();
        var poolCfg = new ApplicationPoolConfig
        {
            Id = "fwd-pool",
            DesiredState = DesiredResourceState.Started
        };

        await using var supervisor = new ApplicationPoolSupervisor(poolCfg, workerLauncher, _fakeTime);

        string upstreamPipe = $"kite-spec-fwd-{Guid.NewGuid():N}";
        WebApplicationBuilder upstreamBuilder = WebApplication.CreateSlimBuilder();
        upstreamBuilder.Logging.ClearProviders();
        if (OperatingSystem.IsWindows())
        {
            upstreamBuilder.WebHost.UseNamedPipes(options =>
            {
                options.CreateNamedPipeServerStream = static ctx => new System.IO.Pipes.NamedPipeServerStream(
                    ctx.NamedPipeEndPoint.PipeName,
                    System.IO.Pipes.PipeDirection.InOut,
                    System.IO.Pipes.NamedPipeServerStream.MaxAllowedServerInstances,
                    System.IO.Pipes.PipeTransmissionMode.Byte,
                    System.IO.Pipes.PipeOptions.Asynchronous);
            });
        }
        upstreamBuilder.WebHost.ConfigureKestrel(k => k.ListenNamedPipe(upstreamPipe));
        await using WebApplication upstream = upstreamBuilder.Build();
        upstream.Run(async ctx =>
        {
            capturedForwardedHeader = ctx.Request.Headers["Forwarded"].ToString();
            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsync("ok");
        });
        await upstream.StartAsync();

        var appCfg = new SiteApplicationConfig
        {
            Path = "/api",
            PoolId = "fwd-pool",
            HostingMode = WorkloadHostingMode.ExternalReverseProxy,
            UpstreamUrl = $"http://{upstreamPipe}.pipe.local"
        };

        await supervisor.StartOrReconcileAsync(poolCfg, appCfg);

        DefaultHttpContext ctx = CreateContext("GET", "/api/test");
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.42");
        await supervisor.ForwardHttpAsync(ctx, appCfg, "/test".AsMemory());

        Assert.NotNull(capturedForwardedHeader);
        Assert.Contains("for=\"203.0.113.42\"", capturedForwardedHeader);
        Assert.Contains("proto=http", capturedForwardedHeader);
        Assert.Contains("host=\"example.local:8080\"", capturedForwardedHeader);

        await upstream.StopAsync();
    }

    [RfcFact("RFC 6455", "4.2", "RFC6455-4.2-MUST-1")]
    public async Task WebSocketUpgrade_CompletesHandshakeAndEchoesFrames()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        await using WebApplication app = builder.Build();
        app.UseWebSockets();
        app.Run(ctx => _pipeline.ExecuteAsync(ctx));
        await app.StartAsync();

        TestServer server = app.GetTestServer();
        WebSocketClient wsClient = server.CreateWebSocketClient();
        wsClient.ConfigureRequest = req =>
        {
            req.Headers["Host"] = "example.local:8080";
        };

        using WebSocket socket = await wsClient.ConnectAsync(new Uri("ws://example.local:8080/ws"), CancellationToken.None);
        Assert.Equal(WebSocketState.Open, socket.State);

        byte[] outbound = "Hello RFC 6455 WebSocket!"u8.ToArray();
        await socket.SendAsync(outbound, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        var receiveBuf = new byte[128];
        WebSocketReceiveResult received = await socket.ReceiveAsync(receiveBuf, CancellationToken.None);
        Assert.Equal(WebSocketMessageType.Text, received.MessageType);
        Assert.Equal("Hello RFC 6455 WebSocket!", Encoding.UTF8.GetString(receiveBuf, 0, received.Count));

        await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Done", CancellationToken.None);
    }

    [RfcFact("RFC 7838", "3", "RFC7838-ALTSVC-1")]
    public async Task HttpsWithHttp3Enabled_EmitsAltSvcHeader()
    {
        var serverCfg = new ServerConfig
        {
            EnableAltSvcHeader = true,
            Transport = new HttpTransportConfig { EnableHttp3 = true, AltSvcMaxAgeSeconds = 3600 }
        };
        var routeTable = new HostRouteTable([_site], serverCfg);
        using var h3Pipeline = new KiteRequestPipeline(() => routeTable, _ => null, _fakeTime);

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Scheme = "https";
        ctx.Request.IsHttps = true;
        ctx.Request.Protocol = "HTTP/2";
        ctx.Request.Headers["Host"] = "secure.example.local:8443";
        ctx.Connection.LocalPort = 8443;
        ctx.Request.Path = "/index.html";
        ctx.Response.Body = new MemoryStream();

        await h3Pipeline.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Equal("h3=\":8443\"; ma=3600", ctx.Response.Headers["Alt-Svc"].ToString());
    }

    [RfcFact("RFC 8297", "2", "RFC8297-EARLYHINTS-1")]
    public async Task EarlyHintsConfigured_InvokesEarlyHintsFeatureAndEmitsPreloadLinkHeader()
    {
        WebApplicationBuilder kestrelBuilder = WebApplication.CreateSlimBuilder();
        kestrelBuilder.Logging.ClearProviders();
        kestrelBuilder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        await using WebApplication backend = kestrelBuilder.Build();
        backend.MapGet("/app/page", () => Results.Text("<html><body>OK</body></html>", "text/html"));
        await backend.StartAsync();
        string backendUrl = backend.Urls.First();

        var cluster = new UpstreamClusterConfig
        {
            Id = "hints-cluster",
            Destinations = [new UpstreamDestinationConfig { Id = "d1", Address = backendUrl }]
        };
        using var clusterManager = new UpstreamClusterManager();
        clusterManager.ReconcileClusters([cluster]);

        var hintsSite = new SiteConfig
        {
            Id = "hints-site",
            DesiredState = DesiredResourceState.Started,
            Bindings = [new SiteBindingConfig { Protocol = "http", Port = 8080, Host = "hints.local" }],
            RootApplication = new SiteApplicationConfig
            {
                Path = "/",
                HostingMode = WorkloadHostingMode.ExternalReverseProxy,
                UpstreamClusterId = "hints-cluster"
            },
            Features = new SiteFeaturesConfig
            {
                Compression = new SiteCompressionConfig { Enabled = false },
                Caching = new SiteCachingConfig { Enabled = false },
                EarlyHintsLinks = ["</styles/main.css>; rel=preload; as=style"]
            }
        };

        var rt = new HostRouteTable([hintsSite], new ServerConfig { Upstreams = [cluster] });

        using var hintsPipeline = new KiteRequestPipeline(() => rt, _ => null, _fakeTime, upstreamManager: clusterManager);
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Scheme = "http";
        ctx.Request.Headers["Host"] = "hints.local:8080";
        ctx.Connection.LocalPort = 8080;
        ctx.Request.Path = "/app/page";
        ctx.Response.Body = new MemoryStream();

        var sentHints = new List<string>();
        ctx.Features.Set<IKiteEarlyHintsFeature>(new TestEarlyHintsFeature(sentHints));

        await hintsPipeline.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Contains("</styles/main.css>; rel=preload; as=style", sentHints);
        Assert.Contains("</styles/main.css>; rel=preload; as=style", ctx.Response.Headers["Link"].ToString());
    }

    [RfcFact("RFC 9110", "15.5.29", "RFC9110-429-RETRY-1")]
    public async Task RateLimiterExhausted_Returns429WithRetryAfterHeader()
    {
        var rlSite = new SiteConfig
        {
            Id = "rl-spec",
            DesiredState = DesiredResourceState.Started,
            Bindings = [new SiteBindingConfig { Protocol = "http", Port = 8080, Host = "rl.local" }],
            RootApplication = new SiteApplicationConfig { Path = "/", PhysicalPath = _tempDir, HostingMode = WorkloadHostingMode.StaticOnly },
            Features = new SiteFeaturesConfig
            {
                RateLimiting = new RateLimitingConfig
                {
                    Enabled = true,
                    Algorithm = RateLimitAlgorithm.FixedWindow,
                    PermitLimit = 1,
                    WindowSeconds = 60,
                    RetryAfterSeconds = 15
                }
            }
        };

        var rt = new HostRouteTable([rlSite]);
        using var rlPipeline = new KiteRequestPipeline(() => rt, _ => null, _fakeTime);

        DefaultHttpContext first = CreateContext("GET", "/index.html");
        first.Request.Headers["Host"] = "rl.local:8080";
        await rlPipeline.ExecuteAsync(first);
        Assert.Equal(StatusCodes.Status200OK, first.Response.StatusCode);

        DefaultHttpContext second = CreateContext("GET", "/index.html");
        second.Request.Headers["Host"] = "rl.local:8080";
        await rlPipeline.ExecuteAsync(second);
        Assert.Equal(StatusCodes.Status429TooManyRequests, second.Response.StatusCode);
        Assert.False(string.IsNullOrWhiteSpace(second.Response.Headers["Retry-After"].ToString()));
    }

    [RfcFact("RFC 8555", "8.3", "RFC8555-HTTP01-1")]
    public async Task AcmeHttp01ChallengeEndpoint_RespondsWithKeyAuthorization()
    {
        using var acmePipeline = new KiteRequestPipeline(
            () => _routeTable,
            _ => null,
            _fakeTime,
            acmeChallengeResolver: tok => tok == "acme-token-xyz" ? "acme-token-xyz.thumbprint123" : null);

        DefaultHttpContext ctx = CreateContext("GET", "/.well-known/acme-challenge/acme-token-xyz");
        await acmePipeline.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.StartsWith("text/plain", ctx.Response.ContentType);
        Assert.Equal("acme-token-xyz.thumbprint123", ReadResponseBody(ctx));
    }

    [RfcFact("RFC 9110", "15.4.9", "RFC9110-15.4.9-REDIR-1")]
    public async Task RedirectHttpToHttps_Returns308AndPreservesPathAndQuery()
    {
        var redirSite = new SiteConfig
        {
            Id = "redir-site",
            DesiredState = DesiredResourceState.Started,
            Bindings =
            [
                new SiteBindingConfig { Protocol = "http", IpAddress = "*", Port = 8080, Host = "redir.local" },
                new SiteBindingConfig { Protocol = "https", IpAddress = "*", Port = 8443, Host = "redir.local" }
            ],
            RootApplication = new SiteApplicationConfig
            {
                Path = "/",
                PhysicalPath = _tempDir,
                HostingMode = WorkloadHostingMode.StaticOnly
            },
            Features = new SiteFeaturesConfig
            {
                InheritServerDefaults = false,
                TlsPolicy = new TlsPolicyConfig
                {
                    RedirectHttpToHttps = true,
                    RedirectHttpToHttpsStatusCode = 308
                }
            }
        };

        var rt = new HostRouteTable([redirSite]);
        using var pipe = new KiteRequestPipeline(() => rt, _ => null, _fakeTime);
        DefaultHttpContext ctx = CreateContext("GET", "/orders/42");
        ctx.Request.Headers["Host"] = "redir.local:8080";
        ctx.Request.QueryString = new QueryString("?tab=items");

        await pipe.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status308PermanentRedirect, ctx.Response.StatusCode);
        Assert.Equal("https://redir.local:8443/orders/42?tab=items", ctx.Response.Headers["Location"].ToString());
    }

    [RfcFact("gRPC-Web Protocol", "Framing", "GRPC-WEB-TRAILERS-1")]
    public void GrpcWebBridge_EncodesUncompressed0x80TrailerFrame()
    {
        using var resp = new HttpResponseMessage(HttpStatusCode.OK);
        resp.TrailingHeaders.Add("grpc-status", "0");
        resp.TrailingHeaders.Add("grpc-message", "OK");

        byte[] frame = GrpcWebBridgeMiddleware.BuildTrailerFrame(resp.TrailingHeaders);
        Assert.True(frame.Length > 5);
        Assert.Equal(0x80, frame[0]);
        uint len = (uint)((frame[1] << 24) | (frame[2] << 16) | (frame[3] << 8) | frame[4]);
        Assert.Equal((uint)(frame.Length - 5), len);
        string text = Encoding.ASCII.GetString(frame, 5, frame.Length - 5);
        Assert.Contains("grpc-status: 0\r\n", text);
        Assert.Contains("grpc-message: OK\r\n", text);
    }

    [RfcFact("RFC 6066", "3.0", "RFC6066-L4-SNI-1")]
    public void L4StreamListener_ParsesTlsClientHelloSniWithoutTermination()
    {
        byte[] packet = BuildSampleTlsClientHello("db.internal.kite");
        bool ok = L4StreamListenerManager.TryParseTlsClientHelloSni(packet, out string? sni);
        Assert.True(ok);
        Assert.Equal("db.internal.kite", sni);
    }

    [RfcFact("RFC 8259", "9", "RFC8259-JSONDEPTH-1")]
    public async Task EdgeWafBodyInspector_RejectsDeepJsonAndRewindsAllowedBodies()
    {
        var wafSite = new SiteConfig
        {
            Id = "waf-body-site",
            Features = new SiteFeaturesConfig
            {
                EdgeWaf = new EdgeWafConfig
                {
                    Enabled = true,
                    InspectRequestBody = true,
                    MaxBodyInspectBytes = 8192,
                    MaxJsonDepth = 3
                }
            }
        };
        CompiledSiteFeatures compiled = CompiledSiteFeatures.Compile(wafSite);

        // Deeply nested JSON exceeding depth 3 -> 400
        var badCtx = new DefaultHttpContext();
        badCtx.Request.Method = "POST";
        badCtx.Request.ContentType = "application/json";
        badCtx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"a\":{\"b\":{\"c\":{\"d\":1}}}}"));
        (bool allowedBad, int statusBad, _, _, _) = await RequestFilteringAndIpGateModule.EvaluateBodyAsync(badCtx, compiled, CancellationToken.None);
        Assert.False(allowedBad);
        Assert.Equal(StatusCodes.Status400BadRequest, statusBad);

        // Shallow JSON within limit -> allowed and stream rewound to 0
        string payload = "{\"ok\":true}";
        var goodCtx = new DefaultHttpContext();
        goodCtx.Request.Method = "POST";
        goodCtx.Request.ContentType = "application/json";
        goodCtx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(payload));
        (bool allowedGood, _, _, _, _) = await RequestFilteringAndIpGateModule.EvaluateBodyAsync(goodCtx, compiled, CancellationToken.None);
        Assert.True(allowedGood);
        Assert.Equal(0, goodCtx.Request.Body.Position);
    }

    [RfcFact("RFC 9110", "15.6.4", "RFC9110-FAULT-503-1")]
    public async Task DeclarativeGatewayRoute_FaultInjectionReturns503AndAppliesHeaderTransform()
    {
        var faultSite = new SiteConfig
        {
            Id = "fault-site",
            DesiredState = DesiredResourceState.Started,
            Bindings = [new SiteBindingConfig { Protocol = "http", IpAddress = "*", Port = 8080, Host = "fault.local" }],
            RootApplication = new SiteApplicationConfig { Path = "/", PhysicalPath = _tempDir, HostingMode = WorkloadHostingMode.StaticOnly },
            Features = new SiteFeaturesConfig
            {
                InheritServerDefaults = false,
                Routes =
                [
                    new SiteRouteRuleConfig
                    {
                        Id = "chaos-503",
                        PathPrefix = "/chaos",
                        Transform = new RouteTransformConfig
                        {
                            SetResponseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Chaos-Rule"] = "chaos-503" }
                        },
                        FaultInjection = new FaultInjectionConfig
                        {
                            Enabled = true,
                            AbortPercent = 100,
                            AbortStatusCode = 503
                        }
                    }
                ]
            }
        };

        var rt = new HostRouteTable([faultSite]);
        using var pipe = new KiteRequestPipeline(() => rt, _ => null, _fakeTime);
        DefaultHttpContext ctx = CreateContext("GET", "/chaos/orders");
        ctx.Request.Headers["Host"] = "fault.local:8080";
        await pipe.ExecuteAsync(ctx);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, ctx.Response.StatusCode);
        Assert.Contains("application/problem+json", ctx.Response.ContentType);
        Assert.Equal("chaos-503", ctx.Response.Headers["X-Chaos-Rule"].ToString());
    }

    private static byte[] BuildSampleTlsClientHello(string hostname)
    {
        byte[] hostBytes = Encoding.ASCII.GetBytes(hostname);
        int sniListLen = 1 + 2 + hostBytes.Length;
        int sniExtDataLen = 2 + sniListLen;
        int extsTotalLen = 4 + sniExtDataLen;
        // ClientHello body: version(2) + random(32) + sidLen(1) + csLen(2)+cs(2) + compLen(1)+comp(1) + extsLen(2) + exts
        int chBodyLen = 2 + 32 + 1 + 4 + 2 + 2 + extsTotalLen;
        int hsLen = 4 + chBodyLen;
        byte[] buf = new byte[5 + hsLen];
        buf[0] = 0x16; // Handshake record
        buf[1] = 0x03; buf[2] = 0x01;
        buf[3] = (byte)(hsLen >> 8); buf[4] = (byte)hsLen;
        buf[5] = 0x01; // ClientHello
        buf[6] = 0; buf[7] = (byte)(chBodyLen >> 8); buf[8] = (byte)chBodyLen;
        int p = 9;
        buf[p++] = 0x03; buf[p++] = 0x03; // TLS 1.2
        p += 32; // Random
        buf[p++] = 0; // Session ID length
        buf[p++] = 0; buf[p++] = 2; buf[p++] = 0x13; buf[p++] = 0x01; // Cipher suites
        buf[p++] = 1; buf[p++] = 0; // Compression methods
        buf[p++] = (byte)(extsTotalLen >> 8); buf[p++] = (byte)extsTotalLen;
        buf[p++] = 0x00; buf[p++] = 0x00; // Extension SNI (0x0000)
        buf[p++] = (byte)(sniExtDataLen >> 8); buf[p++] = (byte)sniExtDataLen;
        buf[p++] = (byte)(sniListLen >> 8); buf[p++] = (byte)sniListLen;
        buf[p++] = 0x00; // host_name
        buf[p++] = (byte)(hostBytes.Length >> 8); buf[p++] = (byte)hostBytes.Length;
        hostBytes.CopyTo(buf, p);
        return buf;
    }

    private sealed class TestEarlyHintsFeature(List<string> sink) : IKiteEarlyHintsFeature
    {
        public ValueTask SendEarlyHintsAsync(IReadOnlyList<string> linkHeaderValues, CancellationToken ct)
        {
            sink.AddRange(linkHeaderValues);
            return ValueTask.CompletedTask;
        }
    }

    private static DefaultHttpContext CreateContext(string method, string path)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Scheme = "http";
        ctx.Request.Headers["Host"] = "example.local:8080";
        ctx.Connection.LocalPort = 8080;
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string ReadResponseBody(HttpContext ctx) =>
        Encoding.UTF8.GetString(ReadResponseBytes(ctx));

    private static byte[] ReadResponseBytes(HttpContext ctx)
    {
        var ms = (MemoryStream)ctx.Response.Body;
        return ms.ToArray();
    }

    private static string FindRepoRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Kite.slnx")))
            {
                return dir;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }
        throw new DirectoryNotFoundException("Could not locate Kite.slnx repository root.");
    }

    public void Dispose()
    {
        _pipeline.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { }
        }
    }
}

public sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _utcNow = new(2026, 11, 11, 18, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
}
