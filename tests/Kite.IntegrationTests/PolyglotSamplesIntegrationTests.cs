using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Kite.Core.Models;
using Kite.Core.Serialization;
using Kite.Core.Storage;
using Kite.Management.Web;
using Kite.Server.Hosting;
using Xunit;

namespace Kite.IntegrationTests;

public sealed class PolyglotSamplesIntegrationTests
{
    [Fact]
    public async Task NodeSample_RunsEndToEndThroughKiteOutOfProcessProxy()
    {
        string nodeExe = ResolveExecutable("node", @"C:\Program Files\nodejs\node.exe");
        string sampleDir = Path.Combine(ResolveRepoRoot(), "samples", "Sample.NodeApi");
        Assert.True(Directory.Exists(sampleDir));

        await VerifySampleThroughKiteAsync(
            sampleId: "sample-node-api",
            poolId: "node-pool",
            executable: nodeExe,
            arguments: "server.js",
            physicalPath: sampleDir,
            expectedService: "Sample.NodeApi",
            infoPath: "/api/telemetry",
            ssePath: "/sse/events");
    }

    [Fact]
    public async Task PythonSample_RunsEndToEndThroughKiteOutOfProcessProxy()
    {
        string pythonExe = ResolvePythonExecutable();
        string sampleDir = Path.Combine(ResolveRepoRoot(), "samples", "Sample.PythonApi");
        Assert.True(Directory.Exists(sampleDir));

        await VerifySampleThroughKiteAsync(
            sampleId: "sample-python-api",
            poolId: "python-pool",
            executable: pythonExe,
            arguments: "app.py --port {port}",
            physicalPath: sampleDir,
            expectedService: "Sample.PythonApi",
            infoPath: "/api/info",
            ssePath: "/sse/stream");
    }

    [Fact]
    public async Task ManagementUi_PersistsOutOfProcessExecutableAndArguments()
    {
        string tempHome = Path.Combine(Path.GetTempPath(), $"kite-polyglot-ui-{Guid.NewGuid():N}");
        try
        {
            var layout = new KiteHomeLayout(tempHome);
            await using var host = new KiteInMemoryHost(layout);
            await host.StartAsync(seedDefaults: true);

            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(
                "id=default-site&name=node-worker&poolId=default-pool&hostingMode=OutOfProcessProxy&executable=node&arguments=server.js&httpPort=8080&hostHeader=*&routingFieldsPresent=true"));
            using InMemoryUiResponse postResp = await host.SendAsync("POST", "/actions/site-save", ms, "application/x-www-form-urlencoded");
            Assert.Equal(200, postResp.StatusCode);

            (SiteConfig? site, _) = await AtomicJsonStore.ReadWithHashAsync(layout.GetSiteConfigPath("default-site"), KiteJsonContext.Default.SiteConfig);
            Assert.NotNull(site);
            Assert.Equal(WorkloadHostingMode.OutOfProcessProxy, site!.RootApplication.HostingMode);
            Assert.Equal("node", site.RootApplication.Executable);
            Assert.Equal("server.js", site.RootApplication.Arguments);
        }
        finally
        {
            try { Directory.Delete(tempHome, true); } catch { }
        }
    }

    private static async Task VerifySampleThroughKiteAsync(
        string sampleId,
        string poolId,
        string executable,
        string arguments,
        string physicalPath,
        string expectedService,
        string infoPath,
        string ssePath)
    {
        string tempHome = Path.Combine(Path.GetTempPath(), $"kite-{sampleId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempHome);
        try
        {
            var layout = new KiteHomeLayout(tempHome);
            layout.EnsureDirectoriesCreated();
            int edgePort = GetFreeLoopbackPort();

            await AtomicJsonStore.WriteAtomicAsync(
                layout.ServerConfigPath,
                new ServerConfig { ServerName = $"Polyglot-{expectedService}" },
                KiteJsonContext.Default.ServerConfig);

            await AtomicJsonStore.WriteAtomicAsync(
                layout.GetPoolConfigPath(poolId),
                new ApplicationPoolConfig
                {
                    Id = poolId,
                    Name = poolId,
                    DesiredState = DesiredResourceState.Started,
                    ProcessModel = new PoolProcessModelConfig
                    {
                        WorkerCount = 1,
                        StartMode = PoolStartMode.AlwaysRunning,
                        EnvironmentVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["PYTHONUNBUFFERED"] = "1",
                            ["NODE_ENV"] = "production"
                        }
                    }
                },
                KiteJsonContext.Default.ApplicationPoolConfig);

            await AtomicJsonStore.WriteAtomicAsync(
                layout.GetSiteConfigPath(sampleId),
                new SiteConfig
                {
                    Id = sampleId,
                    Name = sampleId,
                    DesiredState = DesiredResourceState.Started,
                    Bindings =
                    [
                        new SiteBindingConfig { Protocol = "http", IpAddress = "*", Port = edgePort, Host = "*" }
                    ],
                    RootApplication = new SiteApplicationConfig
                    {
                        Path = "/",
                        PoolId = poolId,
                        PhysicalPath = physicalPath,
                        HostingMode = WorkloadHostingMode.OutOfProcessProxy,
                        Executable = executable,
                        Arguments = arguments,
                        EnableStreamingPassThrough = true
                    }
                },
                KiteJsonContext.Default.SiteConfig);

            await using var engine = new KiteServerEngine(layout, enableKestrelListeners: true);
            await engine.StartAsync(seedDefaultsIfEmpty: false);

            using var client = new HttpClient
            {
                BaseAddress = new Uri($"http://127.0.0.1:{edgePort}"),
                Timeout = TimeSpan.FromSeconds(10)
            };

            string rootBody = await client.GetStringAsync("/");
            using (JsonDocument doc = JsonDocument.Parse(rootBody))
            {
                Assert.Equal(expectedService, doc.RootElement.GetProperty("service").GetString());
                Assert.True(doc.RootElement.GetProperty("pid").GetInt32() > 0);
            }

            string infoBody = await client.GetStringAsync(infoPath);
            using (JsonDocument infoDoc = JsonDocument.Parse(infoBody))
            {
                Assert.Equal(expectedService, infoDoc.RootElement.GetProperty("service").GetString());
            }

            using var postContent = new StringContent("{\"hello\":\"kite\"}", Encoding.UTF8, "application/json");
            HttpResponseMessage postResp = await client.PostAsync("/api/echo", postContent);
            postResp.EnsureSuccessStatusCode();
            string postBody = await postResp.Content.ReadAsStringAsync();
            Assert.Contains("hello", postBody);
            Assert.Contains(expectedService, postBody);

            string sseBody = await client.GetStringAsync(ssePath);
            Assert.Contains("event:", sseBody);
        }
        finally
        {
            try { Directory.Delete(tempHome, true); } catch { }
        }
    }

    private static string ResolveRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "samples", "Sample.NodeApi")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repository root containing samples/Sample.NodeApi.");
    }

    private static string ResolveExecutable(string commandName, string fallbackWindowsPath)
    {
        if (File.Exists(fallbackWindowsPath))
        {
            return fallbackWindowsPath;
        }
        return commandName;
    }

    private static string ResolvePythonExecutable()
    {
        string[] candidates =
        [
            @"C:\Python313\python.exe",
            @"C:\Python312\python.exe",
            @"C:\Python311\python.exe",
            @"C:\Python310\python.exe",
            @"C:\Python39\python.exe",
            @"C:\Python38\python.exe"
        ];
        foreach (string c in candidates)
        {
            if (File.Exists(c))
            {
                return c;
            }
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
