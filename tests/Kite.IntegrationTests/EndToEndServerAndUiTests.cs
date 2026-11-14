using System.Text;
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

public sealed class EndToEndServerAndUiTests
{
    [Fact]
    public async Task FullStack_FilesystemControlPlane_DualWorkloadHosting_AndZeroPortDesktopUiBridge()
    {
        string tempHome = Path.Combine(Path.GetTempPath(), $"kite-e2e-{Guid.NewGuid():N}");
        var layout = new KiteHomeLayout(tempHome);
        layout.EnsureDirectoriesCreated();

        string staticRoot = Path.Combine(tempHome, "www-root");
        string vdirMedia = Path.Combine(tempHome, "shared-media");
        Directory.CreateDirectory(staticRoot);
        Directory.CreateDirectory(vdirMedia);

        await File.WriteAllTextAsync(Path.Combine(staticRoot, "index.html"), "<h1>Root Static Site</h1>");
        await File.WriteAllTextAsync(Path.Combine(vdirMedia, "banner.txt"), "VirtualDirectoryBannerContent");

        // Start a real upstream Kestrel server for testing Mode A (OutOfProcessProxy / ExternalReverseProxy)
        string upstreamPipe = $"kite-e2e-proxy-{Guid.NewGuid():N}";
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
        await using WebApplication upstreamApp = upstreamBuilder.Build();
        upstreamApp.Run(async ctx =>
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync($$"""{"mode":"ModeA_Proxy","path":"{{ctx.Request.Path}}"}""");
        });
        await upstreamApp.StartAsync();
        string upstreamUrl = $"http://{upstreamPipe}.pipe.local";

        try
        {
            // 1. Seed Application Pools (Pool 1: Mode B KiteWorkerIpc, Pool 2: Mode A Proxy)
            var ipcPool = new ApplicationPoolConfig
            {
                Id = "ipc-pool",
                Name = "IPC Worker Pool",
                DesiredState = DesiredResourceState.Started,
                RecycleVersion = 1,
                ProcessModel = new PoolProcessModelConfig { WorkerCount = 2, GcMode = PoolGcMode.DatasServer }
            };
            var proxyPool = new ApplicationPoolConfig
            {
                Id = "proxy-pool",
                Name = "Reverse Proxy Pool",
                DesiredState = DesiredResourceState.Started,
                RecycleVersion = 1
            };

            await AtomicJsonStore.WriteAtomicAsync(layout.GetPoolConfigPath(ipcPool.Id), ipcPool, KiteJsonContext.Default.ApplicationPoolConfig);
            await AtomicJsonStore.WriteAtomicAsync(layout.GetPoolConfigPath(proxyPool.Id), proxyPool, KiteJsonContext.Default.ApplicationPoolConfig);

            // 2. Seed Site with Root Static App + Virtual Directory (/media) + Nested App (/ipc-api) + Nested App (/proxy-api)
            var site = new SiteConfig
            {
                Id = "enterprise-portal",
                Name = "Enterprise Portal",
                DesiredState = DesiredResourceState.Started,
                Bindings =
                [
                    new SiteBindingConfig { Protocol = "http", IpAddress = "*", Port = 18080, Host = "portal.kite.local" }
                ],
                RootApplication = new SiteApplicationConfig
                {
                    Path = "/",
                    PoolId = "ipc-pool",
                    PhysicalPath = staticRoot,
                    HostingMode = WorkloadHostingMode.StaticOnly,
                    VirtualDirectories =
                    [
                        new VirtualDirectoryConfig { VirtualPath = "/media", PhysicalPath = vdirMedia }
                    ]
                },
                NestedApplications =
                [
                    new SiteApplicationConfig
                    {
                        Path = "/ipc-api",
                        PoolId = "ipc-pool",
                        HostingMode = WorkloadHostingMode.KiteWorkerIpc
                    },
                    new SiteApplicationConfig
                    {
                        Path = "/proxy-api",
                        PoolId = "proxy-pool",
                        HostingMode = WorkloadHostingMode.ExternalReverseProxy,
                        UpstreamUrl = upstreamUrl
                    }
                ]
            };

            await AtomicJsonStore.WriteAtomicAsync(layout.GetSiteConfigPath(site.Id), site, KiteJsonContext.Default.SiteConfig);

            // 2b. Before KiteServerEngine starts, verify KiteManagementService accurately reports Offline & Stopped
            var preStartMgmt = new Kite.Management.Core.KiteManagementService(layout);
            var offlineSnapshot = await preStartMgmt.GetWorkspaceSnapshotAsync();
            Assert.False(offlineSnapshot.IsServerOnline);
            Assert.All(offlineSnapshot.Pools, p => Assert.Equal(RuntimeResourceState.Stopped, p.EffectiveState));
            Assert.All(offlineSnapshot.Sites, s => Assert.Equal(RuntimeResourceState.Stopped, s.EffectiveState));

            // 3. Start KiteServerEngine (in-memory listener mode for deterministic test execution)
            await using var serverEngine = new KiteServerEngine(layout, enableKestrelListeners: false);
            await serverEngine.StartAsync(seedDefaultsIfEmpty: false);

            var onlineSnapshot = await preStartMgmt.GetWorkspaceSnapshotAsync();
            Assert.True(onlineSnapshot.IsServerOnline);
            Assert.All(onlineSnapshot.Pools, p => Assert.Equal(RuntimeResourceState.Started, p.EffectiveState));
            Assert.All(onlineSnapshot.Sites, s => Assert.Equal(RuntimeResourceState.Started, s.EffectiveState));

            // Verify state/*.status.json files were written to disk
            Assert.True(File.Exists(layout.ServerStatusPath));
            Assert.True(File.Exists(layout.GetPoolStatusPath("ipc-pool")));
            Assert.True(File.Exists(layout.GetSiteStatusPath("enterprise-portal")));

            // 4. Verify Root Static File serving
            DefaultHttpContext staticCtx = CreatePortalRequest("GET", "/");
            await serverEngine.Pipeline.ExecuteAsync(staticCtx);
            Assert.Equal(200, staticCtx.Response.StatusCode);
            Assert.Contains("Root Static Site", ReadBody(staticCtx));

            // 5. Verify Virtual Directory (/media/banner.txt) serving
            DefaultHttpContext vdirCtx = CreatePortalRequest("GET", "/media/banner.txt");
            await serverEngine.Pipeline.ExecuteAsync(vdirCtx);
            Assert.Equal(200, vdirCtx.Response.StatusCode);
            Assert.Equal("VirtualDirectoryBannerContent", ReadBody(vdirCtx));

            // 6. Verify Nested Application Mode B (KiteWorkerIpc over Named Pipes)
            DefaultHttpContext ipcCtx = CreatePortalRequest("GET", "/ipc-api/orders");
            await serverEngine.Pipeline.ExecuteAsync(ipcCtx);
            Assert.Equal(200, ipcCtx.Response.StatusCode);
            string ipcJson = ReadBody(ipcCtx);
            Assert.Contains("\"mode\":\"KiteWorkerIpc\"", ipcJson);
            Assert.Contains("\"/orders\"", ipcJson);

            // 7. Verify Nested Application Mode A (Reverse Proxy over HTTP Loopback)
            DefaultHttpContext proxyCtx = CreatePortalRequest("GET", "/proxy-api/customers");
            await serverEngine.Pipeline.ExecuteAsync(proxyCtx);
            Assert.Equal(200, proxyCtx.Response.StatusCode);
            string proxyJson = ReadBody(proxyCtx);
            Assert.Contains("\"mode\":\"ModeA_Proxy\"", proxyJson);
            Assert.Contains("\"/customers\"", proxyJson);

            // 8. Start Zero-Port Desktop UI In-Memory Bridge (KiteInMemoryHost) and verify all Top Menu Bar Scopes
            await using var uiHost = new KiteInMemoryHost(layout);
            await uiHost.StartAsync(seedDefaults: false);

            // Scope: Server
            using InMemoryHttpResponse serverPage = await uiHost.DispatchAsync(new InMemoryHttpRequest
            {
                Method = "GET",
                PathAndQuery = "/?scope=server"
            });
            Assert.Equal(200, serverPage.StatusCode);
            string serverHtml = await serverPage.ReadBodyAsStringAsync();
            Assert.Contains("Server Node:", serverHtml);
            Assert.Contains("IPC Worker Pool", serverHtml);
            Assert.Contains("Enterprise Portal", serverHtml);
            Assert.DoesNotContain("v@p.Config.RecycleVersion", serverHtml);
            Assert.Contains("v1", serverHtml);

            // Scope: Application Pools
            using InMemoryHttpResponse poolsPage = await uiHost.DispatchAsync(new InMemoryHttpRequest
            {
                Method = "GET",
                PathAndQuery = "/?scope=pools&entity=ipc-pool"
            });
            Assert.Equal(200, poolsPage.StatusCode);
            string poolsHtml = await poolsPage.ReadBodyAsStringAsync();
            Assert.Contains("Application Pool: IPC Worker Pool", poolsHtml);
            Assert.Contains("Overlapped Recycle", poolsHtml);

            // Scope: Sites
            using InMemoryHttpResponse sitesPage = await uiHost.DispatchAsync(new InMemoryHttpRequest
            {
                Method = "GET",
                PathAndQuery = "/?scope=sites&entity=enterprise-portal"
            });
            Assert.Equal(200, sitesPage.StatusCode);
            string sitesHtml = await sitesPage.ReadBodyAsStringAsync();
            Assert.Contains("Site: Enterprise Portal", sitesHtml);
            Assert.Contains("/ipc-api", sitesHtml);
            Assert.Contains("/proxy-api", sitesHtml);

            // 9. Trigger Overlapped Recycle from the Desktop UI via POST (writes to config/pools/ipc-pool.json)
            string formBody = "poolId=ipc-pool&actionType=recycle";
            using InMemoryHttpResponse recyclePost = await uiHost.DispatchAsync(new InMemoryHttpRequest
            {
                Method = "POST",
                PathAndQuery = "/?handler=PoolAction",
                ContentType = "application/x-www-form-urlencoded",
                Body = Encoding.UTF8.GetBytes(formBody)
            });
            Assert.Equal(200, recyclePost.StatusCode);
            string recycleHtml = await recyclePost.ReadBodyAsStringAsync();
            Assert.Contains("RecycleVersion=2", recycleHtml);

            // Reconcile on server and verify AppliedRecycleVersion updated to 2 via filesystem!
            await serverEngine.ConfigMonitor.ReloadIfChangedAsync();
            Assert.Equal(2, serverEngine.GetPoolSupervisor("ipc-pool")!.AppliedRecycleVersion);
        }
        finally
        {
            await upstreamApp.StopAsync();
            if (Directory.Exists(tempHome))
            {
                try { Directory.Delete(tempHome, recursive: true); } catch { }
            }
        }
    }

    private static DefaultHttpContext CreatePortalRequest(string method, string path)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = method;
        ctx.Request.Scheme = "http";
        ctx.Request.Headers["Host"] = "portal.kite.local:18080";
        ctx.Connection.LocalPort = 18080;
        ctx.Request.Path = path;
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static string ReadBody(HttpContext ctx) =>
        Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray());
}
