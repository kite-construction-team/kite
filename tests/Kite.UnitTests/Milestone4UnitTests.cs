using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Kite.Core.Models;
using Kite.Core.Routing;
using Kite.Core.Serialization;
using Kite.Core.Storage;
using Kite.Management.Core;
using Kite.Management.Core.Importers;
using Kite.Server.Observability;
using Kite.Server.Pipeline;
using Kite.Server.State;
using Kite.Server.Transport;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Kite.UnitTests;

public sealed class Milestone4UnitTests
{
    [Fact]
    public void RouteRuleMatcher_MatchesPathMethodHeaderQuery_AndAppliesStripOrRewrite()
    {
        var rules = new List<SiteRouteRuleConfig>
        {
            new()
            {
                Id = "users-v1",
                PathPrefix = "/api/v1/users",
                Methods = ["GET", "POST"],
                MatchHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["X-Tenant"] = "acme" },
                MatchQueryParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["version"] = "2" },
                Transform = new RouteTransformConfig { StripPathPrefix = "/api/v1" }
            },
            new()
            {
                Id = "orders-exact",
                PathPrefix = "/orders",
                ExactPathOnly = true,
                Transform = new RouteTransformConfig { StripPathPrefix = "/orders", RewritePathPrefix = "/v2/orders" }
            }
        };

        var ctx1 = new DefaultHttpContext();
        ctx1.Request.Method = "GET";
        ctx1.Request.Path = "/api/v1/users/42";
        ctx1.Request.Headers["X-Tenant"] = "acme";
        ctx1.Request.QueryString = new QueryString("?version=2");

        RouteMatchResult m1 = RouteRuleMatcher.Match(rules, ctx1.Request);
        Assert.NotNull(m1.Rule);
        Assert.Equal("users-v1", m1.Rule!.Id);
        Assert.Equal("/users/42", m1.TransformedPath);

        // Missing required header -> does not match users-v1
        var ctxNoHdr = new DefaultHttpContext();
        ctxNoHdr.Request.Method = "GET";
        ctxNoHdr.Request.Path = "/api/v1/users/42";
        ctxNoHdr.Request.QueryString = new QueryString("?version=2");
        RouteMatchResult mMiss = RouteRuleMatcher.Match(rules, ctxNoHdr.Request);
        Assert.Null(mMiss.Rule);

        // Exact path rewrite
        var ctxExact = new DefaultHttpContext();
        ctxExact.Request.Method = "GET";
        ctxExact.Request.Path = "/orders";
        RouteMatchResult mExact = RouteRuleMatcher.Match(rules, ctxExact.Request);
        Assert.Equal("orders-exact", mExact.Rule?.Id);
        Assert.Equal("/v2/orders", mExact.TransformedPath);
    }

    [Fact]
    public void TlsSniParser_ExtractsSniHostname_AndRejectsNonTlsPackets()
    {
        Assert.False(L4StreamListenerManager.TryParseTlsClientHelloSni(Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\n\r\n"), out _));
        Assert.False(L4StreamListenerManager.TryParseTlsClientHelloSni([0x16, 0x03, 0x01, 0x00, 0x05], out _));

        byte[] hostBytes = Encoding.ASCII.GetBytes("grpc.internal.kite.dev");
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

        Assert.True(L4StreamListenerManager.TryParseTlsClientHelloSni(buf, out string? sni));
        Assert.Equal("grpc.internal.kite.dev", sni);
    }

    [Fact]
    public async Task GrpcWebBridge_DecodesBase64TextPayload_AndDetectsContentType()
    {
        var req = new DefaultHttpContext().Request;
        req.ContentType = "application/grpc-web-text+proto";
        Assert.True(GrpcWebBridgeMiddleware.IsGrpcWebRequest(req));

        byte[] rawProto = [0x00, 0x00, 0x00, 0x00, 0x03, 0x08, 0x96, 0x01];
        string b64 = Convert.ToBase64String(rawProto);
        using var stream = new MemoryStream(Encoding.ASCII.GetBytes(b64));
        byte[] decoded = await GrpcWebBridgeMiddleware.ReadRequestPayloadAsync(stream, isBase64Text: true, CancellationToken.None);
        Assert.Equal(rawProto, decoded);
    }

    [Fact]
    public void LegacyConfigImporter_TranslatesNginxCaddyAndIisConfigurations()
    {
        string nginx = """
            upstream billing_pool {
                server 127.0.0.1:7001;
                server 127.0.0.1:7002;
            }
            server {
                listen 8081;
                server_name billing.local;
                location /api {
                    proxy_pass http://billing_pool;
                }
            }
            """;

        LegacyImportResult nginxRes = LegacyConfigImporter.Import("nginx", nginx);
        Assert.Single(nginxRes.Sites);
        Assert.Equal("billing.local", nginxRes.Sites[0].Bindings[0].Host);
        Assert.Equal(8081, nginxRes.Sites[0].Bindings[0].Port);
        Assert.Single(nginxRes.Upstreams);
        Assert.Equal(2, nginxRes.Upstreams[0].Destinations.Count);
        Assert.Single(nginxRes.Sites[0].Features.Routes);

        string caddy = """
            app.example.com {
                reverse_proxy 127.0.0.1:6001
            }
            """;

        LegacyImportResult caddyRes = LegacyConfigImporter.Import("caddy", caddy);
        Assert.Single(caddyRes.Sites);
        Assert.Equal("app.example.com", caddyRes.Sites[0].Bindings[0].Host);
        Assert.Single(caddyRes.Upstreams);
        Assert.Equal("http://127.0.0.1:6001", caddyRes.Upstreams[0].Destinations[0].Address);
        Assert.Equal(caddyRes.Upstreams[0].Id, caddyRes.Sites[0].RootApplication.UpstreamClusterId);

        string iis = """
            <configuration>
              <system.applicationHost>
                <applicationPools>
                  <add name="BillingAppPool" managedPipelineMode="Integrated" />
                </applicationPools>
                <sites>
                  <site name="ContosoWeb" id="1">
                    <application path="/" applicationPool="BillingAppPool">
                      <virtualDirectory path="/" physicalPath="C:\inetpub\contoso" />
                    </application>
                    <bindings>
                      <binding protocol="http" bindingInformation="*:8088:contoso.local" />
                    </bindings>
                  </site>
                </sites>
              </system.applicationHost>
            </configuration>
            """;

        LegacyImportResult iisRes = LegacyConfigImporter.Import("iis", iis);
        Assert.Single(iisRes.Pools);
        Assert.Equal("BillingAppPool", iisRes.Pools[0].Name);
        Assert.Single(iisRes.Sites);
        Assert.Equal("ContosoWeb", iisRes.Sites[0].Name);
        Assert.Equal(8088, iisRes.Sites[0].Bindings[0].Port);
    }

    [Fact]
    public async Task ConfigSnapshotHistoryService_CapturesListsDiffs_AndRollsBackChanges()
    {
        string tempHome = Path.Combine(Path.GetTempPath(), $"kite-hist-{Guid.NewGuid():N}");
        try
        {
            var layout = new KiteHomeLayout(tempHome);
            layout.EnsureDirectoriesCreated();

            await AtomicJsonStore.WriteAtomicAsync(
                layout.ServerConfigPath,
                new ServerConfig { ServerName = "Node-V1" },
                KiteJsonContext.Default.ServerConfig);

            var history = new ConfigSnapshotHistoryService(layout);
            ConfigSnapshotSummary snap1 = await history.CaptureSnapshotAsync("Initial baseline", "server", "global");

            await AtomicJsonStore.WriteAtomicAsync(
                layout.ServerConfigPath,
                new ServerConfig { ServerName = "Node-V2-Modified" },
                KiteJsonContext.Default.ServerConfig);

            IReadOnlyList<ConfigSnapshotSummary> snaps = await history.ListSnapshotsAsync();
            Assert.Single(snaps);
            Assert.Equal(snap1.SnapshotId, snaps[0].SnapshotId);

            string diff = await history.ComputeDiffAsync(snap1.SnapshotId);
            Assert.Contains("Node-V2-Modified", diff);

            bool restored = await history.RollbackToSnapshotAsync(snap1.SnapshotId);
            Assert.True(restored);

            (ServerConfig? after, _) = await AtomicJsonStore.ReadWithHashAsync(layout.ServerConfigPath, KiteJsonContext.Default.ServerConfig);
            Assert.Equal("Node-V1", after?.ServerName);
        }
        finally
        {
            try { Directory.Delete(tempHome, true); } catch { }
        }
    }

    [Fact]
    public async Task OtlpHttpJsonExporter_And_RedisRespEncoder_ProduceValidWirePayloads()
    {
        byte[] respBytes = RedisRespClient.EncodeRespArray(["INCR", "kite:rl:key1"]);
        string respStr = Encoding.UTF8.GetString(respBytes);
        Assert.Equal("*2\r\n$4\r\nINCR\r\n$12\r\nkite:rl:key1\r\n", respStr);

        string? capturedBody = null;
        var handler = new CaptureHandler(body => capturedBody = body);
        using var httpClient = new HttpClient(handler);
        await using var exporter = new OtlpHttpJsonExporter(httpClient);

        string srcName = $"Kite.Test.{Guid.NewGuid():N}";
        exporter.Reconcile(new ObservabilityConfig
        {
            EnableOpenTelemetryTracing = true,
            ActivitySourceName = srcName,
            Otlp = new OtlpExportConfig
            {
                Enabled = true,
                EndpointUrl = "http://127.0.0.1:4318/v1/traces",
                ExportIntervalMilliseconds = 50,
                ServiceName = "Kite-Unit-Node"
            }
        });

        using (var source = new ActivitySource(srcName))
        {
            using Activity? act = source.StartActivity("GET /unit-trace");
            act?.SetTag("http.status_code", 200);
        }

        for (int i = 0; i < 30 && capturedBody is null; i++)
        {
            await Task.Delay(40);
        }

        Assert.NotNull(capturedBody);
        using JsonDocument doc = JsonDocument.Parse(capturedBody!);
        Assert.True(doc.RootElement.TryGetProperty("resourceSpans", out _));
        Assert.Contains("GET /unit-trace", capturedBody);
    }

    [Fact]
    public async Task KiteConfigVersion_IsLockedTo1_0_OnModelsSavesAndFileReload()
    {
        var server = new ServerConfig();
        var pool = new ApplicationPoolConfig();
        var site = new SiteConfig();
        Assert.Equal("1.0", server.ConfigVersion);
        Assert.Equal("1.0", pool.ConfigVersion);
        Assert.Equal("1.0", site.ConfigVersion);

        string serverJson = JsonSerializer.Serialize(server, KiteJsonContext.Default.ServerConfig);
        string poolJson = JsonSerializer.Serialize(pool, KiteJsonContext.Default.ApplicationPoolConfig);
        string siteJson = JsonSerializer.Serialize(site, KiteJsonContext.Default.SiteConfig);
        Assert.Contains("\"configVersion\": \"1.0\"", serverJson);
        Assert.Contains("\"configVersion\": \"1.0\"", poolJson);
        Assert.Contains("\"configVersion\": \"1.0\"", siteJson);

        string tempHome = Path.Combine(Path.GetTempPath(), "kite_ver_" + Guid.NewGuid().ToString("N"));
        try
        {
            var layout = new KiteHomeLayout(tempHome);
            await using var monitor = new FileSystemConfigMonitor(layout);
            await monitor.InitializeAndStartAsync(seedDefaultsIfEmpty: true);
            string originalName = monitor.CurrentSnapshot.Server.ServerName;

            var mgmt = new KiteManagementService(layout);
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await mgmt.SaveServerConfigAsync(new ServerConfig { ConfigVersion = "2.0" }));
            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await mgmt.SaveRawConfigJsonAsync(null, "{\"configVersion\":\"2.0\",\"serverName\":\"Bad\"}"));

            string badDiskServer = serverJson.Replace("\"configVersion\": \"1.0\"", "\"configVersion\": \"2.0\"")
                .Replace(originalName, "Rejected-V2");
            await File.WriteAllTextAsync(layout.ServerConfigPath, badDiskServer);
            await monitor.ReloadIfChangedAsync();

            Assert.Equal("1.0", monitor.CurrentSnapshot.Server.ConfigVersion);
            Assert.Equal(originalName, monitor.CurrentSnapshot.Server.ServerName);
        }
        finally
        {
            try { Directory.Delete(tempHome, recursive: true); } catch { }
        }
    }

    private sealed class CaptureHandler(Action<string> onPost) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                string body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                onPost(body);
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
