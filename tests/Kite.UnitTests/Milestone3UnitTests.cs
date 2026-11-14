using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Kite.Core.Models;
using Kite.Core.Routing;
using Kite.Core.Storage;
using Kite.Server.HttpModules;
using Kite.Server.Pipeline;
using Kite.Server.Pools;
using Kite.Server.Tls;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Kite.UnitTests;

public sealed class Milestone3UnitTests
{
    [Fact]
    public async Task EdgeRateLimiter_EnforcesSlidingWindowAndEmitsRetryAfterHeaders()
    {
        var site = new SiteConfig
        {
            Id = "rl-site",
            Features = new SiteFeaturesConfig
            {
                RateLimiting = new RateLimitingConfig
                {
                    Enabled = true,
                    Algorithm = RateLimitAlgorithm.FixedWindow,
                    PermitLimit = 3,
                    WindowSeconds = 30,
                    RetryAfterSeconds = 12,
                    PartitionByHeader = "X-Client-Key"
                }
            }
        };

        CompiledSiteFeatures compiled = CompiledSiteFeatures.Compile(site);

        for (int i = 0; i < 3; i++)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Headers["X-Client-Key"] = "tenant-alpha";
            (bool allowed, _) = await EdgeRateLimiterModule.TryAcquireAsync(ctx, compiled, CancellationToken.None);
            Assert.True(allowed);
            Assert.Equal("3", ctx.Response.Headers["RateLimit-Limit"].ToString());
        }

        // 4th request from tenant-alpha must be rejected (429 / Retry-After)
        var blockedCtx = new DefaultHttpContext();
        blockedCtx.Request.Headers["X-Client-Key"] = "tenant-alpha";
        (bool allowed4, int retryAfter) = await EdgeRateLimiterModule.TryAcquireAsync(blockedCtx, compiled, CancellationToken.None);
        Assert.False(allowed4);
        Assert.True(retryAfter >= 1);
        Assert.Equal("0", blockedCtx.Response.Headers["RateLimit-Remaining"].ToString());
        Assert.False(string.IsNullOrWhiteSpace(blockedCtx.Response.Headers["Retry-After"].ToString()));

        // Independent partition (tenant-beta) still allowed
        var betaCtx = new DefaultHttpContext();
        betaCtx.Request.Headers["X-Client-Key"] = "tenant-beta";
        (bool betaAllowed, _) = await EdgeRateLimiterModule.TryAcquireAsync(betaCtx, compiled, CancellationToken.None);
        Assert.True(betaAllowed);
    }

    [Theory]
    [InlineData("/search", "?q=1' or '1'='1", null, false)]
    [InlineData("/comment", "?text=<script>alert(1)</script>", null, false)]
    [InlineData("/files", "?p=../../etc/passwd", null, false)]
    [InlineData("/normal", "?q=hello", "sqlmap/1.7", false)]
    [InlineData("/api/products", "?page=2&sort=price", "Mozilla/5.0", true)]
    public void EdgeWaf_BlocksSqliXssTraversalAndBadUserAgents(
        string path,
        string query,
        string? userAgent,
        bool expectedAllowed)
    {
        var site = new SiteConfig
        {
            Id = "waf-site",
            Features = new SiteFeaturesConfig
            {
                EdgeWaf = new EdgeWafConfig
                {
                    Enabled = true,
                    BlockSqlInjection = true,
                    BlockXss = true,
                    BlockPathTraversal = true,
                    BlockedUserAgents = ["sqlmap", "nikto"]
                }
            }
        };

        CompiledSiteFeatures compiled = CompiledSiteFeatures.Compile(site);
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = path;
        ctx.Request.QueryString = new QueryString(query);
        if (userAgent is not null)
        {
            ctx.Request.Headers["User-Agent"] = userAgent;
        }

        bool allowed = RequestFilteringAndIpGateModule.Evaluate(
            ctx,
            compiled,
            out int statusCode,
            out string subStatus,
            out _,
            out _);

        Assert.Equal(expectedAllowed, allowed);
        if (!expectedAllowed)
        {
            Assert.Equal(StatusCodes.Status403Forbidden, statusCode);
            Assert.Equal("403.501", subStatus);
        }
    }

    [Fact]
    public async Task SpaFallback_RewritesExtensionlessRoutes_WhilePreservingApiAndMissingAssetsAs404()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"kite-spa-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(tempDir, "index.html"), "<html>SPA_SHELL</html>");

            var site = new SiteConfig
            {
                Id = "spa-site",
                DesiredState = DesiredResourceState.Started,
                Bindings = [new SiteBindingConfig { Protocol = "http", Port = 8080, Host = "spa.local" }],
                RootApplication = new SiteApplicationConfig { Path = "/", PhysicalPath = tempDir, HostingMode = WorkloadHostingMode.StaticOnly },
                Features = new SiteFeaturesConfig
                {
                    Compression = new SiteCompressionConfig { Enabled = false },
                    SpaFallback = new SpaFallbackConfig
                    {
                        Enabled = true,
                        FallbackDocument = "/index.html",
                        ExcludePrefixes = ["/api", "/__kite"]
                    }
                }
            };

            var routeTable = new HostRouteTable([site]);
            using var pipeline = new KiteRequestPipeline(() => routeTable, _ => null);

            // 1. Extensionless client route -> SPA index.html (200)
            var spaCtx = CreateRequestContext("spa.local:8080", 8080, "/dashboard/users/42");
            await pipeline.ExecuteAsync(spaCtx);
            Assert.Equal(200, spaCtx.Response.StatusCode);
            Assert.Contains("SPA_SHELL", ReadBody(spaCtx));

            // 2. Excluded prefix (/api/users) -> 404
            var apiCtx = CreateRequestContext("spa.local:8080", 8080, "/api/users");
            await pipeline.ExecuteAsync(apiCtx);
            Assert.Equal(404, apiCtx.Response.StatusCode);

            // 3. Missing static file extension (.js) -> 404
            var jsCtx = CreateRequestContext("spa.local:8080", 8080, "/missing.bundle.js");
            await pipeline.ExecuteAsync(jsCtx);
            Assert.Equal(404, jsCtx.Response.StatusCode);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void StructuredJsonLogFormatter_ProducesValidNdjsonWithTraceId()
    {
        string line = StructuredJsonLogFormatter.FormatLinePooled(
            new DateTimeOffset(2026, 11, 13, 19, 30, 0, TimeSpan.Zero),
            "site-1",
            "10.0.0.5".AsSpan(),
            "POST".AsSpan(),
            "/api/orders".AsSpan(),
            "?limit=10".AsSpan(),
            8443,
            "api.kite.local".AsSpan(),
            "HTTP/2".AsSpan(),
            201,
            512,
            14,
            "0af7651916cd43dd8448eb211c80319c".AsSpan());

        Assert.EndsWith("\n", line);
        using JsonDocument doc = JsonDocument.Parse(line.TrimEnd());
        Assert.Equal("site-1", doc.RootElement.GetProperty("siteId").GetString());
        Assert.Equal("POST", doc.RootElement.GetProperty("method").GetString());
        Assert.Equal(201, doc.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal("0af7651916cd43dd8448eb211c80319c", doc.RootElement.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task AcmeCertificateManager_ProvisionsAndHotRotatesCertificateEndToEndWithFakeCa()
    {
        string tempHome = Path.Combine(Path.GetTempPath(), $"kite-acme-{Guid.NewGuid():N}");
        try
        {
            var layout = new KiteHomeLayout(tempHome);
            layout.EnsureDirectoriesCreated();

            using var certProvider = new CertificateProvider();
            using var caKey = RSA.Create(2048);
            string? capturedToken = null;
            string? capturedKeyAuthFromServer = null;

            AcmeCertificateManager? manager = null;
            var handler = new FakeAcmeCaHandler(token =>
            {
                capturedToken = token;
                if (manager is not null && manager.TryResolveHttp01Challenge(token, out string? ka))
                {
                    capturedKeyAuthFromServer = ka;
                }
            }, caKey);

            using var fakeClient = new HttpClient(handler);
            manager = new AcmeCertificateManager(layout, certProvider, fakeClient);

            var policy = new AcmePolicyConfig
            {
                Enabled = true,
                AcceptTermsOfService = true,
                ContactEmail = "ops@kite.test",
                DirectoryUrl = "https://acme.fake.local/directory",
                ChallengeType = AcmeChallengeType.Http01,
                Domains = ["edge.kite.test"]
            };

            X509Certificate2 issued = await manager.ProvisionDomainCertificateAsync("edge.kite.test", policy);

            Assert.NotNull(issued);
            Assert.Contains("edge.kite.test", issued.Subject, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(capturedToken);
            Assert.StartsWith(capturedToken + ".", capturedKeyAuthFromServer);
            Assert.True(File.Exists(layout.GetDomainPfxPath("edge.kite.test")));
            Assert.False(certProvider.NeedsAcmeProvisionOrRenewal("edge.kite.test", 30, DateTimeOffset.UtcNow, layout.CertsConfigDirectory));

            var snapshot = certProvider.TryInspectCertificateStatus(
                new SiteBindingConfig { Protocol = "https", Host = "edge.kite.test", Port = 443 },
                allowGenerateSelfSigned: false,
                layout.CertsConfigDirectory,
                mtlsEnabled: true);
            Assert.NotNull(snapshot);
            Assert.Equal("AcmeAuto", snapshot!.ProvisioningMode);
            Assert.True(snapshot.MtlsEnabled);

            manager.Dispose();
        }
        finally
        {
            try { Directory.Delete(tempHome, true); } catch { }
        }
    }

    private static DefaultHttpContext CreateRequestContext(string host, int port, string path)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Scheme = "http";
        ctx.Request.Headers["Host"] = host;
        ctx.Connection.LocalPort = port;
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string ReadBody(HttpContext ctx)
    {
        var ms = (MemoryStream)ctx.Response.Body;
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private sealed class FakeAcmeCaHandler(Action<string> onHttp01Poll, RSA caKey) : HttpMessageHandler
    {
        private X509Certificate2? _issuedCert;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string url = request.RequestUri!.ToString();
            string reqBody = request.Content is not null ? await request.Content.ReadAsStringAsync(ct) : "";
            var resp = new HttpResponseMessage(HttpStatusCode.OK);
            resp.Headers.TryAddWithoutValidation("Replay-Nonce", Guid.NewGuid().ToString("N"));

            if (url.EndsWith("/directory"))
            {
                resp.Content = new StringContent("""{"newNonce":"https://acme.fake.local/nonce","newAccount":"https://acme.fake.local/account","newOrder":"https://acme.fake.local/order"}""", Encoding.UTF8, "application/json");
                return resp;
            }

            if (url.EndsWith("/nonce"))
            {
                return resp;
            }

            if (url.EndsWith("/account"))
            {
                resp.StatusCode = HttpStatusCode.Created;
                resp.Headers.Location = new Uri("https://acme.fake.local/acct/42");
                resp.Content = new StringContent("""{"status":"valid"}""", Encoding.UTF8, "application/json");
                return resp;
            }

            if (url.EndsWith("/order"))
            {
                resp.StatusCode = HttpStatusCode.Created;
                resp.Headers.Location = new Uri("https://acme.fake.local/order/99");
                resp.Content = new StringContent("""{"status":"pending","authorizations":["https://acme.fake.local/authz/1"],"finalize":"https://acme.fake.local/order/99/finalize"}""", Encoding.UTF8, "application/json");
                return resp;
            }

            if (url.EndsWith("/authz/1"))
            {
                resp.Content = new StringContent("""{"status":"pending","challenges":[{"type":"http-01","url":"https://acme.fake.local/chal/1","token":"tok123456"}]}""", Encoding.UTF8, "application/json");
                return resp;
            }

            if (url.EndsWith("/chal/1"))
            {
                onHttp01Poll("tok123456");
                resp.Content = new StringContent("""{"status":"valid"}""", Encoding.UTF8, "application/json");
                return resp;
            }

            if (url.EndsWith("/order/99/finalize"))
            {
                using JsonDocument jwsDoc = JsonDocument.Parse(reqBody);
                string payloadB64 = jwsDoc.RootElement.GetProperty("payload").GetString()!;
                string payloadJson = Encoding.UTF8.GetString(DecodeBase64Url(payloadB64));
                using JsonDocument payloadDoc = JsonDocument.Parse(payloadJson);
                byte[] csrDer = DecodeBase64Url(payloadDoc.RootElement.GetProperty("csr").GetString()!);

                CertificateRequest csr = CertificateRequest.LoadSigningRequest(
                    csrDer,
                    HashAlgorithmName.SHA256,
                    CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions,
                    RSASignaturePadding.Pkcs1);

                var issuerReq = new CertificateRequest("CN=Fake ACME Root CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                issuerReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                using X509Certificate2 issuerCert = issuerReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));

                byte[] serial = new byte[12];
                RandomNumberGenerator.Fill(serial);
                serial[0] &= 0x7F;
                _issuedCert = csr.Create(issuerCert, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(90), serial);

                resp.Content = new StringContent("""{"status":"valid","certificate":"https://acme.fake.local/cert/99"}""", Encoding.UTF8, "application/json");
                return resp;
            }

            if (url.EndsWith("/cert/99"))
            {
                string pem = _issuedCert!.ExportCertificatePem();
                resp.Content = new StringContent(pem, Encoding.ASCII, "application/pem-certificate-chain");
                return resp;
            }

            if (url.EndsWith("/order/99"))
            {
                resp.Content = new StringContent("""{"status":"valid","certificate":"https://acme.fake.local/cert/99"}""", Encoding.UTF8, "application/json");
                return resp;
            }

            resp.StatusCode = HttpStatusCode.NotFound;
            return resp;
        }

        private static byte[] DecodeBase64Url(string input)
        {
            string s = input.Replace('-', '+').Replace('_', '/');
            int pad = (4 - (s.Length % 4)) % 4;
            if (pad > 0) s += new string('=', pad);
            return Convert.FromBase64String(s);
        }
    }
}
