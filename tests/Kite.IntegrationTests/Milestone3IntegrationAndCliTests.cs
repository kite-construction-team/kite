using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Kite.Cli;
using Kite.Core.Models;
using Kite.Core.Serialization;
using Kite.Core.Storage;
using Kite.Management.Web;
using Kite.Server.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kite.IntegrationTests;

public sealed class Milestone3IntegrationAndCliTests
{
    [Fact]
    public async Task UpstreamCluster_FailoverAndActiveHealthRecovery_PlusMetricsAndHealthz()
    {
        string tempHome = Path.Combine(Path.GetTempPath(), $"kite-m3-int-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);
        try
        {
            // 1. Start two real loopback backend servers
            bool backend1Healthy = true;
            await using WebApplication b1 = await StartBackendAsync(ctx =>
            {
                if (ctx.Request.Path == "/healthz")
                {
                    ctx.Response.StatusCode = backend1Healthy ? 200 : 503;
                    return ctx.Response.WriteAsync(backend1Healthy ? "ok" : "sick");
                }
                return ctx.Response.WriteAsync("FROM_BACKEND_1");
            });

            await using WebApplication b2 = await StartBackendAsync(ctx =>
            {
                if (ctx.Request.Path == "/healthz")
                {
                    return ctx.Response.WriteAsync("ok");
                }
                return ctx.Response.WriteAsync("FROM_BACKEND_2");
            });

            string u1 = b1.Urls.First();
            string u2 = b2.Urls.First();

            var layout = new KiteHomeLayout(tempHome);
            layout.EnsureDirectoriesCreated();

            // Allocate ephemeral port for KiteServerEngine Kestrel listener
            int edgePort = GetFreeLoopbackPort();
            var serverCfg = new ServerConfig
            {
                ServerName = "M3-Edge-Node",
                Observability = new ObservabilityConfig
                {
                    EnablePrometheusMetrics = true,
                    MetricsPath = "/__kite/metrics",
                    EnableHealthEndpoints = true,
                    HealthEndpointPath = "/__kite/healthz",
                    ReadyEndpointPath = "/__kite/readyz",
                    AccessLogFormat = AccessLogFormat.Both
                },
                Upstreams =
                [
                    new UpstreamClusterConfig
                    {
                        Id = "api-cluster",
                        LoadBalancingPolicy = LoadBalancingPolicy.RoundRobin,
                        RetryCount = 1,
                        ActiveHealthCheck = new ActiveHealthCheckConfig
                        {
                            Enabled = true,
                            IntervalSeconds = 1,
                            Path = "/healthz",
                            ExpectedStatusCode = 200
                        },
                        PassiveCircuitBreaker = new PassiveCircuitBreakerConfig
                        {
                            Enabled = true,
                            FailureThreshold = 1,
                            EjectionSeconds = 2
                        },
                        Destinations =
                        [
                            new UpstreamDestinationConfig { Id = "b1", Address = u1 },
                            new UpstreamDestinationConfig { Id = "b2", Address = u2 }
                        ]
                    }
                ]
            };

            var siteCfg = new SiteConfig
            {
                Id = "edge-api",
                Name = "Edge API Gateway",
                DesiredState = DesiredResourceState.Started,
                Bindings = [new SiteBindingConfig { Protocol = "http", IpAddress = "127.0.0.1", Port = edgePort, Host = "localhost" }],
                RootApplication = new SiteApplicationConfig
                {
                    Path = "/",
                    PoolId = "default-pool",
                    HostingMode = WorkloadHostingMode.ExternalReverseProxy,
                    UpstreamClusterId = "api-cluster"
                },
                Features = new SiteFeaturesConfig
                {
                    Caching = new SiteCachingConfig { Enabled = false },
                    Compression = new SiteCompressionConfig { Enabled = false }
                }
            };

            await AtomicJsonStore.WriteAtomicAsync(layout.ServerConfigPath, serverCfg, KiteJsonContext.Default.ServerConfig);
            await AtomicJsonStore.WriteAtomicAsync(layout.GetSiteConfigPath(siteCfg.Id), siteCfg, KiteJsonContext.Default.SiteConfig);

            await using var engine = new KiteServerEngine(layout, enableKestrelListeners: true);
            await engine.StartAsync(seedDefaultsIfEmpty: false);

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{edgePort}") };
            client.DefaultRequestHeaders.Host = $"localhost:{edgePort}";

            // Round-robin distributes across b1 and b2
            string r1 = await client.GetStringAsync("/hello");
            string r2 = await client.GetStringAsync("/hello");
            Assert.True((r1 == "FROM_BACKEND_1" && r2 == "FROM_BACKEND_2") || (r1 == "FROM_BACKEND_2" && r2 == "FROM_BACKEND_1"));

            // Take backend 1 unhealthy -> active health probe + failover routes all traffic to backend 2
            backend1Healthy = false;
            string promText = "";
            for (int waitIdx = 0; waitIdx < 20; waitIdx++)
            {
                promText = await client.GetStringAsync("/__kite/metrics");
                if (promText.Contains("kite_upstream_destination_healthy{cluster=\"api-cluster\",destination=\"b1\"} 0"))
                {
                    break;
                }
                await Task.Delay(250);
            }

            for (int i = 0; i < 4; i++)
            {
                string res = await client.GetStringAsync("/hello");
                Assert.Equal("FROM_BACKEND_2", res);
            }

            // Check /__kite/healthz, /__kite/readyz, and Prometheus /__kite/metrics
            string healthJson = await client.GetStringAsync("/__kite/healthz");
            Assert.Contains("\"status\":\"healthy\"", healthJson);

            string readyJson = await client.GetStringAsync("/__kite/readyz");
            Assert.Contains("\"status\":\"ready\"", readyJson);

            promText = await client.GetStringAsync("/__kite/metrics");
            Assert.Contains("kite_requests_total", promText);
            Assert.Contains("kite_upstream_destination_healthy{cluster=\"api-cluster\",destination=\"b1\"} 0", promText);
            Assert.Contains("kite_upstream_destination_healthy{cluster=\"api-cluster\",destination=\"b2\"} 1", promText);
        }
        finally
        {
            try { Directory.Delete(tempHome, true); } catch { }
        }
    }

    [Fact]
    public async Task KiteCli_ValidateRouteTestCertsAndSchemaExport_WorkEndToEnd()
    {
        string tempHome = Path.Combine(Path.GetTempPath(), $"kite-cli-{Guid.NewGuid():N}");
        string schemaOut = Path.Combine(tempHome, "schemas");
        try
        {
            var layout = new KiteHomeLayout(tempHome);
            await using (var host = new KiteInMemoryHost(layout))
            {
                await host.StartAsync(seedDefaults: true);
            }

            var outWriter = new StringWriter();
            var errWriter = new StringWriter();

            int valCode = await KiteCliRunner.ExecuteAsync(["validate", "--home", tempHome], outWriter, errWriter);
            Assert.Equal(0, valCode);
            Assert.Contains("VALID (0 conflicts)", outWriter.ToString());

            outWriter.GetStringBuilder().Clear();
            int routeCode = await KiteCliRunner.ExecuteAsync(["route-test", "http://localhost/index.html", "--home", tempHome], outWriter, errWriter);
            Assert.Equal(0, routeCode);
            Assert.Contains("Matched Site", outWriter.ToString());

            outWriter.GetStringBuilder().Clear();
            int certCode = await KiteCliRunner.ExecuteAsync(["certs", "status", "--home", tempHome], outWriter, errWriter);
            Assert.Equal(0, certCode);
            Assert.Contains("Certificate Store:", outWriter.ToString());

            outWriter.GetStringBuilder().Clear();
            int schemaCode = await KiteCliRunner.ExecuteAsync(["schema", "all", "--out", schemaOut, "--home", tempHome], outWriter, errWriter);
            Assert.Equal(0, schemaCode);
            Assert.True(File.Exists(Path.Combine(schemaOut, "server.v1.schema.json")));
            Assert.True(File.Exists(Path.Combine(schemaOut, "pool.v1.schema.json")));
            Assert.True(File.Exists(Path.Combine(schemaOut, "site.v1.schema.json")));

            using JsonDocument doc = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(schemaOut, "server.v1.schema.json")));
            Assert.Equal("object", doc.RootElement.GetProperty("type").GetString());
            Assert.Equal(
                "1.0",
                doc.RootElement.GetProperty("properties").GetProperty("configVersion").GetProperty("const").GetString());

            // Modifying configVersion away from "1.0" must fail CLI validation with exit code 2.
            string siteFile = Path.Combine(tempHome, "config", "sites", "default-site.json");
            string badSiteJson = (await File.ReadAllTextAsync(siteFile)).Replace("\"configVersion\": \"1.0\"", "\"configVersion\": \"2.0\"");
            await File.WriteAllTextAsync(siteFile, badSiteJson);

            outWriter.GetStringBuilder().Clear();
            errWriter.GetStringBuilder().Clear();
            int badValCode = await KiteCliRunner.ExecuteAsync(["validate", "--home", tempHome], outWriter, errWriter);
            Assert.Equal(2, badValCode);
            Assert.Contains("Version Error", errWriter.ToString());
        }
        finally
        {
            try { Directory.Delete(tempHome, true); } catch { }
        }
    }

    [Fact]
    public async Task HeadlessAdminHost_EnforcesBearerTokenAndServesObsidianUi()
    {
        string tempHome = Path.Combine(Path.GetTempPath(), $"kite-headless-{Guid.NewGuid():N}");
        try
        {
            var layout = new KiteHomeLayout(tempHome);
            await using var adminHost = new HeadlessAdminHost(
                layout,
                new HeadlessAdminOptions(Port: 0, BearerToken: "secret-m3-token"));
            await adminHost.StartAsync();

            using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{adminHost.ListeningPort}") };

            HttpResponseMessage unauth = await client.GetAsync("/?scope=server");
            Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "secret-m3-token");
            HttpResponseMessage authed = await client.GetAsync("/?scope=server");
            Assert.Equal(HttpStatusCode.OK, authed.StatusCode);
            string html = await authed.Content.ReadAsStringAsync();
            Assert.Contains("Kite-Production-Node", html);
        }
        finally
        {
            try { Directory.Delete(tempHome, true); } catch { }
        }
    }

    [Fact]
    public async Task KiteInMemoryHost_SiteRoutingUpstreamsAcmeAndWafForms_PersistExtendedConfig()
    {
        string tempHome = Path.Combine(Path.GetTempPath(), $"kite-ui-m3-cfg-{Guid.NewGuid():N}");
        try
        {
            var layout = new KiteHomeLayout(tempHome);
            await using var host = new KiteInMemoryHost(layout);
            await host.StartAsync(seedDefaults: true);

            // 1. Save expanded Upstream Cluster (retries, timeout, sticky, health interval, passive CB)
            await PostFormAsync(host, "/actions/feature-upstreams",
                "clusterId=api-cluster&loadBalancingPolicy=LeastRequests&destinationAddresses=http%3A%2F%2F127.0.0.1%3A5101%2Chttp%3A%2F%2F127.0.0.1%3A5102&retryCount=3&requestTimeoutSeconds=45&stickyHeaderOrCookie=X-Affinity&activeHealthCheckPath=%2Flive&activeHealthCheckIntervalSeconds=15&passiveFailureThreshold=4&passiveEjectionSeconds=25&activeHealthCheckEnabled=true");

            // 2. Save expanded ACME ToS, Staging Directory URL, RenewBeforeDays
            await PostFormAsync(host, "/actions/feature-acme-mtls",
                "acmeEnabled=true&acceptTermsOfService=true&directoryUrl=https%3A%2F%2Facme-staging-v02.api.letsencrypt.org%2Fdirectory&renewBeforeDays=21&contactEmail=ops%40kite.dev&challengeType=TlsAlpn01&acmeDomains=kite.dev");

            // 3. Save expanded SPA ExcludePrefixes & Edge WAF CustomBlockedPatterns
            await PostFormAsync(host, "/actions/feature-spawaf",
                "spaEnabled=true&fallbackDocument=%2Fapp.html&excludePrefixes=%2Fapi%2C%2F__kite&wafEnabled=true&blockSqlInjection=true&blockXss=true&blockPathTraversal=true&customBlockedPatterns=.git%2Fconfig%2C%2Fwp-admin");

            // 4. Save Site Routing Inspector (#form-save-site payload) including Canary + Streaming + AllowHttp3=false
            await PostFormAsync(host, "/actions/site-save",
                "id=default-site&name=edge-gateway&poolId=default-pool&hostingMode=ExternalReverseProxy&httpPort=8080&httpsPort=8443&hostHeader=api.kite.dev&routingFieldsPresent=true&upstreamClusterId=api-cluster&canaryClusterId=api-cluster&canaryWeightPercent=25&upstreamUrl=http%3A%2F%2F127.0.0.1%3A5101&enableStreamingPassThrough=true");

            // 5. Verify inline Server Directives preserve Modern Config (doesn't drop Upstreams/Transport)
            await PostFormAsync(host, "/actions/server-settings",
                "serverName=Kite-Modern-Edge&maxConcurrentConnections=15000&maxRequestBodySize=31457280&keepAliveTimeoutSeconds=120&headerTimeoutSeconds=30&statusPublishIntervalMilliseconds=1000&inlineTransportFieldsPresent=true&enableHttp3=true&enablePrometheusMetrics=true&accessLogFormat=Both");

            (ServerConfig? srv, _) = await AtomicJsonStore.ReadWithHashAsync(layout.ServerConfigPath, KiteJsonContext.Default.ServerConfig);
            Assert.NotNull(srv);
            Assert.Equal("Kite-Modern-Edge", srv!.ServerName);
            Assert.True(srv.Transport.EnableHttp3);
            Assert.Equal(AccessLogFormat.Both, srv.Observability.AccessLogFormat);
            Assert.Single(srv.Upstreams);
            Assert.Equal("api-cluster", srv.Upstreams[0].Id);
            Assert.Equal(3, srv.Upstreams[0].RetryCount);
            Assert.Equal(45, srv.Upstreams[0].RequestTimeoutSeconds);
            Assert.Equal("X-Affinity", srv.Upstreams[0].StickyHeaderOrCookie);
            Assert.Equal("/live", srv.Upstreams[0].ActiveHealthCheck.Path);
            Assert.Equal(15, srv.Upstreams[0].ActiveHealthCheck.IntervalSeconds);
            Assert.Equal(4, srv.Upstreams[0].PassiveCircuitBreaker.FailureThreshold);
            Assert.Equal(25, srv.Upstreams[0].PassiveCircuitBreaker.EjectionSeconds);

            Assert.True(srv.Features.TlsPolicy.Acme.Enabled);
            Assert.True(srv.Features.TlsPolicy.Acme.AcceptTermsOfService);
            Assert.Equal("https://acme-staging-v02.api.letsencrypt.org/directory", srv.Features.TlsPolicy.Acme.DirectoryUrl);
            Assert.Equal(21, srv.Features.TlsPolicy.Acme.RenewBeforeDays);

            Assert.Contains("/api", srv.Features.SpaFallback.ExcludePrefixes);
            Assert.Contains(".git/config", srv.Features.EdgeWaf.CustomBlockedPatterns);

            (SiteConfig? site, _) = await AtomicJsonStore.ReadWithHashAsync(layout.GetSiteConfigPath("default-site"), KiteJsonContext.Default.SiteConfig);
            Assert.NotNull(site);
            Assert.Equal(WorkloadHostingMode.ExternalReverseProxy, site!.RootApplication.HostingMode);
            Assert.Equal("api-cluster", site.RootApplication.UpstreamClusterId);
            Assert.Equal(25, site.RootApplication.CanaryWeightPercent);
            Assert.True(site.RootApplication.EnableStreamingPassThrough);
            SiteBindingConfig httpsBinding = Assert.Single(site.Bindings, b => b.Protocol == "https");
            Assert.False(httpsBinding.Tls!.AllowHttp3);

            // Verify Dashboard renders Upstream Cluster & Modern Edge Telemetry section
            using InMemoryUiResponse dashResp = await host.SendAsync("/?scope=server");
            string dashHtml = await dashResp.ReadBodyAsStringAsync();
            Assert.Contains("Upstream Reverse Proxy Clusters", dashHtml);
            Assert.Contains("api-cluster", dashHtml);
            Assert.Contains("HTTP/3 (QUIC)", dashHtml);
        }
        finally
        {
            try { Directory.Delete(tempHome, true); } catch { }
        }
    }

    private static async Task PostFormAsync(KiteInMemoryHost host, string path, string formUrlEncoded)
    {
        using var ms = new MemoryStream(Encoding.UTF8.GetBytes(formUrlEncoded));
        using InMemoryUiResponse resp = await host.SendAsync("POST", path, ms, "application/x-www-form-urlencoded");
        Assert.Equal(200, resp.StatusCode);
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

    private static int GetFreeLoopbackPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
