using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Kite.Cli;
using Kite.Core.Models;
using Kite.Core.Serialization;
using Kite.Core.Storage;
using Kite.Server.Hosting;
using Kite.Server.Tls;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kite.IntegrationTests;

public sealed class Milestone4IntegrationTests
{
    [Fact]
    public async Task GatewayRoutes_ForwardAuth_HeaderTransform_FaultInjection_AndShadowMirror_WorkEndToEnd()
    {
        string tempHome = Path.Combine(Path.GetTempPath(), $"kite-m4-gw-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);
        try
        {
            string? capturedAuthUserOnBackend = null;
            string? capturedForwardedPath = null;
            int shadowCount = 0;

            await using WebApplication primaryBackend = await StartBackendAsync(ctx =>
            {
                capturedAuthUserOnBackend = ctx.Request.Headers["X-Auth-User"].ToString();
                capturedForwardedPath = ctx.Request.Path.Value;
                ctx.Response.Headers["X-Backend-Raw"] = "true";
                return ctx.Response.WriteAsync($"PRIMARY:{ctx.Request.Path}:{capturedAuthUserOnBackend}");
            });

            await using WebApplication shadowBackend = await StartBackendAsync(ctx =>
            {
                Interlocked.Increment(ref shadowCount);
                return ctx.Response.WriteAsync("SHADOW_OK");
            });

            await using WebApplication authService = await StartBackendAsync(ctx =>
            {
                if (ctx.Request.Headers["Authorization"].ToString() == "Bearer valid-token")
                {
                    ctx.Response.Headers["X-Auth-User"] = "alice@kite.dev";
                    ctx.Response.Headers["X-Auth-Roles"] = "admin";
                    ctx.Response.StatusCode = 200;
                    return ctx.Response.WriteAsync("OK");
                }
                ctx.Response.StatusCode = 401;
                return ctx.Response.WriteAsync("UNAUTHORIZED");
            });

            int edgePort = GetFreeLoopbackPort();
            var layout = new KiteHomeLayout(tempHome);
            layout.EnsureDirectoriesCreated();

            var srv = new ServerConfig
            {
                ServerName = "M4-Gateway",
                Upstreams =
                [
                    new UpstreamClusterConfig
                    {
                        Id = "primary-api",
                        Destinations = [new UpstreamDestinationConfig { Id = "p1", Address = primaryBackend.Urls.First() }]
                    },
                    new UpstreamClusterConfig
                    {
                        Id = "shadow-api",
                        Destinations = [new UpstreamDestinationConfig { Id = "s1", Address = shadowBackend.Urls.First() }]
                    }
                ]
            };
            await AtomicJsonStore.WriteAtomicAsync(layout.ServerConfigPath, srv, KiteJsonContext.Default.ServerConfig);

            var pool = new ApplicationPoolConfig { Id = "gw-pool", Name = "gw-pool", DesiredState = DesiredResourceState.Started };
            await AtomicJsonStore.WriteAtomicAsync(layout.GetPoolConfigPath(pool.Id), pool, KiteJsonContext.Default.ApplicationPoolConfig);

            var site = new SiteConfig
            {
                Id = "gw-site",
                Name = "gw-site",
                DesiredState = DesiredResourceState.Started,
                Bindings = [new SiteBindingConfig { Protocol = "http", IpAddress = "*", Port = edgePort, Host = "*" }],
                RootApplication = new SiteApplicationConfig
                {
                    Path = "/",
                    PoolId = pool.Id,
                    PhysicalPath = tempHome,
                    HostingMode = WorkloadHostingMode.ExternalReverseProxy,
                    UpstreamClusterId = "primary-api"
                },
                Features = new SiteFeaturesConfig
                {
                    InheritServerDefaults = false,
                    ForwardAuth = new ForwardAuthConfig
                    {
                        Enabled = true,
                        EndpointUrl = $"{authService.Urls.First()}/verify",
                        CopyResponseHeaders = ["X-Auth-User", "X-Auth-Roles"]
                    },
                    Routes =
                    [
                        new SiteRouteRuleConfig
                        {
                            Id = "chaos-route",
                            PathPrefix = "/api/v1/chaos",
                            FaultInjection = new FaultInjectionConfig { Enabled = true, AbortPercent = 100, AbortStatusCode = 503 }
                        },
                        new SiteRouteRuleConfig
                        {
                            Id = "users-route",
                            PathPrefix = "/api/v1/users",
                            TargetClusterId = "primary-api",
                            ShadowClusterId = "shadow-api",
                            ShadowPercent = 100,
                            Transform = new RouteTransformConfig
                            {
                                StripPathPrefix = "/api/v1",
                                SetResponseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Gateway"] = "Kite-M4" },
                                RemoveResponseHeaders = ["X-Backend-Raw"]
                            }
                        }
                    ]
                }
            };
            await AtomicJsonStore.WriteAtomicAsync(layout.GetSiteConfigPath(site.Id), site, KiteJsonContext.Default.SiteConfig);

            await using var engine = new KiteServerEngine(layout, enableKestrelListeners: true);
            await engine.StartAsync(seedDefaultsIfEmpty: false);

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{edgePort}") };

            // 1. Unauthenticated request rejected by ForwardAuth (401)
            HttpResponseMessage unauthResp = await client.GetAsync("/api/v1/users/list");
            Assert.Equal(HttpStatusCode.Unauthorized, unauthResp.StatusCode);

            // 2. Authenticated request passes ForwardAuth, strips prefix, transforms headers, and triggers shadow mirror
            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/v1/users/list");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer valid-token");
            HttpResponseMessage okResp = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.OK, okResp.StatusCode);
            string body = await okResp.Content.ReadAsStringAsync();
            Assert.Contains("PRIMARY:/users/list:alice@kite.dev", body);
            Assert.Equal("/users/list", capturedForwardedPath);
            Assert.Equal("alice@kite.dev", capturedAuthUserOnBackend);
            Assert.Equal("Kite-M4", okResp.Headers.GetValues("X-Gateway").Single());
            Assert.False(okResp.Headers.Contains("X-Backend-Raw"));

            for (int i = 0; i < 40 && Volatile.Read(ref shadowCount) == 0; i++)
            {
                await Task.Delay(50);
            }
            Assert.True(Volatile.Read(ref shadowCount) >= 1, "Shadow mirror traffic was not delivered to shadow-api.");

            // 3. Chaos Fault Injection route returns 503 with ForwardAuth satisfied
            using var chaosReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/chaos/test");
            chaosReq.Headers.TryAddWithoutValidation("Authorization", "Bearer valid-token");
            HttpResponseMessage chaosResp = await client.SendAsync(chaosReq);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, chaosResp.StatusCode);
        }
        finally
        {
            try { Directory.Delete(tempHome, true); } catch { }
        }
    }

    [Fact]
    public async Task HttpToHttpsRedirect_OnDemandAskWebhook_AndLiveOtlpReceiver_WorkEndToEnd()
    {
        string tempHome = Path.Combine(Path.GetTempPath(), $"kite-m4-obs-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);
        try
        {
            string? otlpTracePayload = null;
            var askDomains = new List<string>();

            await using WebApplication receiver = await StartBackendAsync(async ctx =>
            {
                if (ctx.Request.Path == "/v1/traces")
                {
                    using var sr = new StreamReader(ctx.Request.Body, Encoding.UTF8);
                    otlpTracePayload = await sr.ReadToEndAsync();
                    ctx.Response.StatusCode = 200;
                    await ctx.Response.WriteAsync("{}");
                    return;
                }
                if (ctx.Request.Path == "/ask")
                {
                    string domain = ctx.Request.Query["domain"].ToString();
                    askDomains.Add(domain);
                    ctx.Response.StatusCode = domain.EndsWith(".tenant.kite", StringComparison.OrdinalIgnoreCase) ? 200 : 403;
                    await ctx.Response.WriteAsync(ctx.Response.StatusCode == 200 ? "allow" : "deny");
                    return;
                }
                ctx.Response.StatusCode = 404;
            });

            int edgePort = GetFreeLoopbackPort();
            var layout = new KiteHomeLayout(tempHome);
            layout.EnsureDirectoriesCreated();
            await File.WriteAllTextAsync(Path.Combine(tempHome, "index.html"), "<h1>OK</h1>");

            var srv = new ServerConfig
            {
                ServerName = "M4-Obs",
                Observability = new ObservabilityConfig
                {
                    EnableOpenTelemetryTracing = true,
                    ActivitySourceName = $"Kite.Server",
                    Otlp = new OtlpExportConfig
                    {
                        Enabled = true,
                        EndpointUrl = $"{receiver.Urls.First()}/v1/traces",
                        ExportIntervalMilliseconds = 80,
                        ServiceName = "Kite-M4-Test"
                    }
                }
            };
            await AtomicJsonStore.WriteAtomicAsync(layout.ServerConfigPath, srv, KiteJsonContext.Default.ServerConfig);

            var pool = new ApplicationPoolConfig { Id = "obs-pool", Name = "obs-pool", DesiredState = DesiredResourceState.Started };
            await AtomicJsonStore.WriteAtomicAsync(layout.GetPoolConfigPath(pool.Id), pool, KiteJsonContext.Default.ApplicationPoolConfig);

            var site = new SiteConfig
            {
                Id = "obs-site",
                Name = "obs-site",
                DesiredState = DesiredResourceState.Started,
                Bindings =
                [
                    new SiteBindingConfig { Protocol = "http", IpAddress = "*", Port = edgePort, Host = "*" },
                    new SiteBindingConfig { Protocol = "https", IpAddress = "*", Port = edgePort + 1, Host = "*" }
                ],
                RootApplication = new SiteApplicationConfig
                {
                    Path = "/",
                    PoolId = pool.Id,
                    PhysicalPath = tempHome,
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
            await AtomicJsonStore.WriteAtomicAsync(layout.GetSiteConfigPath(site.Id), site, KiteJsonContext.Default.SiteConfig);

            await using var engine = new KiteServerEngine(layout, enableKestrelListeners: true);
            await engine.StartAsync(seedDefaultsIfEmpty: false);

            // 1. Verify On-Demand TLS Ask Webhook & TTL cache
            using var certProvider = new CertificateProvider();
            using var acmeMgr = new AcmeCertificateManager(layout, certProvider);
            var onDemandPolicy = new OnDemandTlsConfig
            {
                Enabled = true,
                AskEndpointUrl = $"{receiver.Urls.First()}/ask",
                CacheTtlSeconds = 60
            };
            Assert.True(await acmeMgr.IsOnDemandSniAllowedAsync("shop1.tenant.kite", onDemandPolicy));
            Assert.False(await acmeMgr.IsOnDemandSniAllowedAsync("evil.other.com", onDemandPolicy));
            Assert.Contains("shop1.tenant.kite", askDomains);

            // 2. Verify HTTP -> HTTPS 308 Redirect and OTLP trace span flush
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{edgePort}") };
            HttpResponseMessage resp = await client.GetAsync("/checkout?step=2");
            Assert.Equal((HttpStatusCode)308, resp.StatusCode);
            Assert.Equal($"https://127.0.0.1:{edgePort + 1}/checkout?step=2", resp.Headers.Location?.ToString());

            for (int i = 0; i < 40 && otlpTracePayload is null; i++)
            {
                await Task.Delay(60);
            }
            Assert.NotNull(otlpTracePayload);
            Assert.Contains("resourceSpans", otlpTracePayload);
        }
        finally
        {
            try { Directory.Delete(tempHome, true); } catch { }
        }
    }

    [Fact]
    public async Task L4StreamProxy_RoutesRealTcpConnectionsByTlsClientHelloSni_WithoutTerminatingTls()
    {
        string tempHome = Path.Combine(Path.GetTempPath(), $"kite-m4-l4-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);
        try
        {
            // Two raw TCP echo servers standing in for TLS endpoints
            using var backendAlpha = new TcpListener(IPAddress.Loopback, 0);
            using var backendBeta = new TcpListener(IPAddress.Loopback, 0);
            backendAlpha.Start();
            backendBeta.Start();
            int alphaPort = ((IPEndPoint)backendAlpha.LocalEndpoint).Port;
            int betaPort = ((IPEndPoint)backendBeta.LocalEndpoint).Port;

            using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            Task alphaTask = AcceptOnceEchoWithTagAsync(backendAlpha, "ALPHA:", serverCts.Token);
            Task betaTask = AcceptOnceEchoWithTagAsync(backendBeta, "BETA:", serverCts.Token);

            int l4Port = GetFreeLoopbackPort();
            var layout = new KiteHomeLayout(tempHome);
            layout.EnsureDirectoriesCreated();

            var srv = new ServerConfig
            {
                ServerName = "M4-L4-Node",
                TcpStreams =
                [
                    new L4StreamProxyConfig
                    {
                        Id = "sni-router",
                        Enabled = true,
                        ListenPort = l4Port,
                        EnableTlsSniRouting = true,
                        DefaultUpstreamHost = "127.0.0.1",
                        DefaultUpstreamPort = alphaPort,
                        SniRoutes =
                        [
                            new L4SniRouteConfig { SniHostname = "beta.service.local", UpstreamHost = "127.0.0.1", UpstreamPort = betaPort }
                        ]
                    }
                ]
            };
            await AtomicJsonStore.WriteAtomicAsync(layout.ServerConfigPath, srv, KiteJsonContext.Default.ServerConfig);

            await using var engine = new KiteServerEngine(layout, enableKestrelListeners: true);
            await engine.StartAsync(seedDefaultsIfEmpty: false);

            // Send ClientHello for beta.service.local -> must land on backendBeta without decrypting bytes
            byte[] betaHello = BuildTlsClientHello("beta.service.local");
            using (var tcp = new TcpClient())
            {
                await tcp.ConnectAsync(IPAddress.Loopback, l4Port, serverCts.Token);
                using NetworkStream stream = tcp.GetStream();
                await stream.WriteAsync(betaHello, serverCts.Token);
                byte[] buf = new byte[betaHello.Length + 16];
                int n = await stream.ReadAsync(buf, serverCts.Token);
                Assert.StartsWith("BETA:", Encoding.ASCII.GetString(buf, 0, n));
            }

            // Send ClientHello for alpha.service.local -> falls back to DefaultUpstreamPort (backendAlpha)
            byte[] alphaHello = BuildTlsClientHello("alpha.service.local");
            using (var tcp2 = new TcpClient())
            {
                await tcp2.ConnectAsync(IPAddress.Loopback, l4Port, serverCts.Token);
                using NetworkStream stream2 = tcp2.GetStream();
                await stream2.WriteAsync(alphaHello, serverCts.Token);
                byte[] buf2 = new byte[alphaHello.Length + 16];
                int n2 = await stream2.ReadAsync(buf2, serverCts.Token);
                Assert.StartsWith("ALPHA:", Encoding.ASCII.GetString(buf2, 0, n2));
            }

            await Task.WhenAll(alphaTask, betaTask);
        }
        finally
        {
            try { Directory.Delete(tempHome, true); } catch { }
        }
    }

    [Fact]
    public async Task OutOfProcessPythonProxy_StreamsLiveStdoutIntoWorkerProcessStatus()
    {
        string pythonExe = ResolvePythonExecutable();
        string sampleDir = Path.Combine(ResolveRepoRoot(), "samples", "Sample.PythonApi");
        Assert.True(Directory.Exists(sampleDir));

        string tempHome = Path.Combine(Path.GetTempPath(), $"kite-m4-stdout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);
        try
        {
            int edgePort = GetFreeLoopbackPort();
            var layout = new KiteHomeLayout(tempHome);
            layout.EnsureDirectoriesCreated();

            await AtomicJsonStore.WriteAtomicAsync(layout.ServerConfigPath, new ServerConfig { ServerName = "M4-Stdout-Test" }, KiteJsonContext.Default.ServerConfig);
            await AtomicJsonStore.WriteAtomicAsync(
                layout.GetPoolConfigPath("py-pool"),
                new ApplicationPoolConfig
                {
                    Id = "py-pool",
                    Name = "py-pool",
                    DesiredState = DesiredResourceState.Started,
                    ProcessModel = new PoolProcessModelConfig
                    {
                        WorkerCount = 1,
                        StartMode = PoolStartMode.AlwaysRunning,
                        EnvironmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PYTHONUNBUFFERED"] = "1" }
                    }
                },
                KiteJsonContext.Default.ApplicationPoolConfig);

            await AtomicJsonStore.WriteAtomicAsync(
                layout.GetSiteConfigPath("py-site"),
                new SiteConfig
                {
                    Id = "py-site",
                    Name = "py-site",
                    DesiredState = DesiredResourceState.Started,
                    Bindings = [new SiteBindingConfig { Protocol = "http", IpAddress = "*", Port = edgePort, Host = "*" }],
                    RootApplication = new SiteApplicationConfig
                    {
                        Path = "/",
                        PoolId = "py-pool",
                        PhysicalPath = sampleDir,
                        HostingMode = WorkloadHostingMode.OutOfProcessProxy,
                        Executable = pythonExe,
                        Arguments = "app.py --port {port}"
                    }
                },
                KiteJsonContext.Default.SiteConfig);

            await using var engine = new KiteServerEngine(layout, enableKestrelListeners: true);
            await engine.StartAsync(seedDefaultsIfEmpty: false);

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{edgePort}") };
            string body = await client.GetStringAsync("/");
            Assert.Contains("Sample.PythonApi", body);

            await engine.PublishStateSnapshotsAsync();
            ApplicationPoolStatus? poolStatus = await AtomicJsonStore.TryReadAsync(
                layout.GetPoolStatusPath("py-pool"),
                KiteJsonContext.Default.ApplicationPoolStatus);

            Assert.NotNull(poolStatus);
            Assert.NotEmpty(poolStatus!.ActiveWorkers);
            Assert.NotEmpty(poolStatus.ActiveWorkers[0].RecentOutputLines);
        }
        finally
        {
            try { Directory.Delete(tempHome, true); } catch { }
        }
    }

    [Fact]
    public async Task CliCommands_ImportDeploySwapAndRollback_WorkEndToEnd()
    {
        string tempHome = Path.Combine(Path.GetTempPath(), $"kite-m4-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);
        try
        {
            var layout = new KiteHomeLayout(tempHome);
            layout.EnsureDirectoriesCreated();
            await using (var initMonitor = new FileSystemConfigMonitor(layout))
            {
                await initMonitor.InitializeAndStartAsync(seedDefaultsIfEmpty: true);
            }

            // 1. Write sample nginx.conf & run `kite import nginx <file> --apply`
            string nginxFile = Path.Combine(tempHome, "nginx.conf");
            await File.WriteAllTextAsync(nginxFile, """
                server {
                    listen 9091;
                    server_name cli-import.local;
                    location /api {
                        proxy_pass http://127.0.0.1:7100;
                    }
                }
                """);

            var outWriter = new StringWriter();
            var errWriter = new StringWriter();
            int importCode = await KiteCliRunner.ExecuteAsync(["import", "--format", "nginx", "--file", nginxFile, "--apply", "--home", tempHome], outWriter, errWriter);
            Assert.Equal(0, importCode);
            Assert.Contains("Applied imported configuration", outWriter.ToString());

            // 2. Stage & Swap Blue/Green on default-site
            string greenDir = Path.Combine(tempHome, "wwwroot-green");
            Directory.CreateDirectory(greenDir);
            outWriter.GetStringBuilder().Clear();
            int deployCode = await KiteCliRunner.ExecuteAsync(["deploy", "--site", "default-site", "--path", greenDir, "--swap", "--home", tempHome], outWriter, errWriter);
            Assert.Equal(0, deployCode);
            Assert.Contains("Swapped active slot", outWriter.ToString());

            (SiteConfig? swapped, _) = await AtomicJsonStore.ReadWithHashAsync(layout.GetSiteConfigPath("default-site"), KiteJsonContext.Default.SiteConfig);
            Assert.Equal("green", swapped?.RootApplication.ActiveSlot, ignoreCase: true);
            Assert.Equal(greenDir, swapped?.RootApplication.PhysicalPath);

            // 3. List history & rollback to first snapshot (before deploy/swap)
            outWriter.GetStringBuilder().Clear();
            int histCode = await KiteCliRunner.ExecuteAsync(["history", "--home", tempHome], outWriter, errWriter);
            Assert.Equal(0, histCode);
            string[] lines = outWriter.ToString().Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            Assert.NotEmpty(lines);
            string snapId = lines.First(l => l.Contains('|')).Split('|')[0].Trim();

            outWriter.GetStringBuilder().Clear();
            int rbCode = await KiteCliRunner.ExecuteAsync(["rollback", snapId, "--home", tempHome], outWriter, errWriter);
            Assert.Equal(0, rbCode);
            Assert.Contains("Rolled back", outWriter.ToString());
        }
        finally
        {
            try { Directory.Delete(tempHome, true); } catch { }
        }
    }

    private static async Task AcceptOnceEchoWithTagAsync(TcpListener listener, string tag, CancellationToken ct)
    {
        using TcpClient client = await listener.AcceptTcpClientAsync(ct);
        using NetworkStream ns = client.GetStream();
        byte[] buf = new byte[1024];
        int n = await ns.ReadAsync(buf, ct);
        byte[] tagBytes = Encoding.ASCII.GetBytes(tag);
        byte[] reply = new byte[tagBytes.Length + n];
        tagBytes.CopyTo(reply, 0);
        buf.AsSpan(0, n).CopyTo(reply.AsSpan(tagBytes.Length));
        await ns.WriteAsync(reply, ct);
    }

    private static byte[] BuildTlsClientHello(string hostname)
    {
        byte[] hostBytes = Encoding.ASCII.GetBytes(hostname);
        int sniListLen = 1 + 2 + hostBytes.Length;
        int sniExtDataLen = 2 + sniListLen;
        int extsTotalLen = 4 + sniExtDataLen;
        int chBodyLen = 2 + 32 + 1 + 4 + 2 + 2 + extsTotalLen;
        int hsLen = 4 + chBodyLen;
        byte[] buf = new byte[5 + hsLen];
        buf[0] = 0x16;
        buf[1] = 0x03; buf[2] = 0x01;
        buf[3] = (byte)(hsLen >> 8); buf[4] = (byte)hsLen;
        buf[5] = 0x01;
        buf[6] = 0; buf[7] = (byte)(chBodyLen >> 8); buf[8] = (byte)chBodyLen;
        int p = 9;
        buf[p++] = 0x03; buf[p++] = 0x03;
        p += 32;
        buf[p++] = 0;
        buf[p++] = 0; buf[p++] = 2; buf[p++] = 0x13; buf[p++] = 0x01;
        buf[p++] = 1; buf[p++] = 0;
        buf[p++] = (byte)(extsTotalLen >> 8); buf[p++] = (byte)extsTotalLen;
        buf[p++] = 0x00; buf[p++] = 0x00;
        buf[p++] = (byte)(sniExtDataLen >> 8); buf[p++] = (byte)sniExtDataLen;
        buf[p++] = (byte)(sniListLen >> 8); buf[p++] = (byte)sniListLen;
        buf[p++] = 0x00;
        buf[p++] = (byte)(hostBytes.Length >> 8); buf[p++] = (byte)hostBytes.Length;
        hostBytes.CopyTo(buf, p);
        return buf;
    }

    private static async Task<WebApplication> StartBackendAsync(RequestDelegate handler)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        WebApplication app = builder.Build();
        app.Run(handler);
        await app.StartAsync();
        return app;
    }

    private static string ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "samples", "Sample.PythonApi")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root.");
    }

    private static string ResolvePythonExecutable()
    {
        foreach (string c in new[] { @"C:\Python313\python.exe", @"C:\Python312\python.exe", @"C:\Python311\python.exe", @"C:\Python310\python.exe", @"C:\Python39\python.exe", @"C:\Python38\python.exe" })
        {
            if (File.Exists(c)) return c;
        }
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(pathEnv))
        {
            foreach (string entry in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = Path.Combine(entry.Trim(), "python.exe");
                if (File.Exists(candidate) && !candidate.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }
        return "python";
    }

    private static int GetFreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
