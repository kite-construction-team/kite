using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Kite.Cli;
using Kite.Core.Models;
using Kite.Core.Serialization;
using Kite.Core.Storage;
using Kite.Management.Core;
using Kite.Management.Web;
using Kite.Server.Hosting;
using Xunit;

namespace Kite.IntegrationTests;

public sealed class MultiInstanceEdgeMeshIntegrationTests
{
    private static int ReserveFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task SeedMeshInstanceAsync(
        string homeDir,
        string registryDir,
        string serverName,
        int frontDoorPort,
        string hostName,
        string wwwContent)
    {
        var layout = new KiteHomeLayout(homeDir);
        var mgmt = new KiteManagementService(layout);
        await mgmt.EnsureDefaultWorkspaceSeededAsync();

        string wwwDir = Path.Combine(homeDir, "wwwroot");
        Directory.CreateDirectory(wwwDir);
        await File.WriteAllTextAsync(Path.Combine(wwwDir, "hello.txt"), wwwContent);

        await mgmt.MutateServerConfigAsync(srv =>
        {
            srv.ServerName = serverName;
            srv.StatusPublishIntervalMilliseconds = 250;
            srv.Mesh = new MeshConfig
            {
                Mode = MeshMode.Auto,
                FrontDoorPorts = [frontDoorPort],
                RegistryDirectoryOverride = registryDir
            };
        });

        await mgmt.MutateSiteConfigAsync("default-site", site =>
        {
            site.Name = $"{serverName} Site";
            site.Bindings =
            [
                new SiteBindingConfig
                {
                    Protocol = "http",
                    IpAddress = "127.0.0.1",
                    Port = frontDoorPort,
                    Host = hostName
                }
            ];
            site.RootApplication.HostingMode = WorkloadHostingMode.StaticOnly;
            site.RootApplication.PhysicalPath = wwwDir;
        });
    }

    [Fact]
    public async Task MultiInstanceEdgeMesh_RoutesPeerHostsThroughLeader_AndPromotesPeerOnFailover()
    {
        int frontDoorPort = ReserveFreePort();
        string root = Path.Combine(Path.GetTempPath(), "kite_mesh_e2e_" + Guid.NewGuid().ToString("N"));
        string regDir = Path.Combine(root, "registry");
        string homeA = Path.Combine(root, "repo-alpha", ".kite");
        string homeB = Path.Combine(root, "repo-beta", ".kite");

        try
        {
            await SeedMeshInstanceAsync(homeA, regDir, "Alpha-Node", frontDoorPort, "alpha.localhost", "FROM-ALPHA-LEADER");
            await SeedMeshInstanceAsync(homeB, regDir, "Beta-Node", frontDoorPort, "beta.localhost", "FROM-BETA-PEER");

            var layoutA = new KiteHomeLayout(homeA);
            var layoutB = new KiteHomeLayout(homeB);

            await using var engineA = new KiteServerEngine(layoutA, registryDirectoryOverride: regDir);
            await engineA.StartAsync(seedDefaultsIfEmpty: false);
            Assert.Equal(InstanceRole.MeshLeader, engineA.MeshRole);

            await using var engineB = new KiteServerEngine(layoutB, registryDirectoryOverride: regDir);
            await engineB.StartAsync(seedDefaultsIfEmpty: false);
            Assert.Equal(InstanceRole.MeshPeer, engineB.MeshRole);
            Assert.NotNull(engineB.MeshPeerHttpPort);
            Assert.NotEqual(frontDoorPort, engineB.MeshPeerHttpPort!.Value);

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

            // Wait briefly for Leader A's background tick/watcher to ingest Peer B's registration
            string? betaViaLeader = null;
            for (int i = 0; i < 20; i++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{frontDoorPort}/hello.txt");
                req.Headers.Host = "beta.localhost";
                using HttpResponseMessage resp = await http.SendAsync(req);
                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    betaViaLeader = await resp.Content.ReadAsStringAsync();
                    if (betaViaLeader == "FROM-BETA-PEER")
                    {
                        break;
                    }
                }
                await Task.Delay(150);
            }
            Assert.Equal("FROM-BETA-PEER", betaViaLeader);

            // Verify Alpha still serves locally on the same front-door port
            using (var reqA = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{frontDoorPort}/hello.txt"))
            {
                reqA.Headers.Host = "alpha.localhost";
                using HttpResponseMessage respA = await http.SendAsync(reqA);
                Assert.Equal(HttpStatusCode.OK, respA.StatusCode);
                Assert.Equal("FROM-ALPHA-LEADER", await respA.Content.ReadAsStringAsync());
            }

            // Verify Fleet UI Host exposes both live instances and can switch between them
            await using var uiHost = new KiteInMemoryHost(layoutA, registryDirectoryOverride: regDir);
            InMemoryUiResponse fleetResp = await uiHost.SendAsync("/api/fleet/instances");
            Assert.Equal(200, fleetResp.StatusCode);
            using (var sr = new StreamReader(fleetResp.BodyStream))
            {
                string fleetJson = await sr.ReadToEndAsync();
                List<InstanceDescriptor>? list = JsonSerializer.Deserialize(fleetJson, KiteJsonContext.Default.ListInstanceDescriptor);
                Assert.NotNull(list);
                Assert.Equal(2, list!.Count);
                Assert.Contains(list, d => d.ServerName == "Alpha-Node" && d.Role == InstanceRole.MeshLeader);
                Assert.Contains(list, d => d.ServerName == "Beta-Node" && d.Role == InstanceRole.MeshPeer);
            }

            // Failover: shut down Leader A and verify Peer B promotes to MeshLeader and claims frontDoorPort
            await engineA.DisposeAsync();

            for (int i = 0; i < 30 && engineB.MeshRole != InstanceRole.MeshLeader; i++)
            {
                await Task.Delay(150);
            }
            Assert.Equal(InstanceRole.MeshLeader, engineB.MeshRole);

            using (var reqAfterFailover = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{frontDoorPort}/hello.txt"))
            {
                reqAfterFailover.Headers.Host = "beta.localhost";
                using HttpResponseMessage respFailover = await http.SendAsync(reqAfterFailover);
                Assert.Equal(HttpStatusCode.OK, respFailover.StatusCode);
                Assert.Equal("FROM-BETA-PEER", await respFailover.Content.ReadAsStringAsync());
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public async Task KiteCliInit_ScaffoldsDotKiteWorkspace_LockedToV1()
    {
        string tempProject = Path.Combine(Path.GetTempPath(), "kite_init_" + Guid.NewGuid().ToString("N"));
        string dotKite = Path.Combine(tempProject, ".kite");
        try
        {
            using var outW = new StringWriter();
            using var errW = new StringWriter();

            int code = await KiteCliRunner.ExecuteAsync(
                ["init", "--home", dotKite, "--host", "billing.localhost"],
                outW,
                errW);
            Assert.Equal(0, code);
            Assert.Contains("billing.localhost", outW.ToString());
            Assert.True(File.Exists(Path.Combine(dotKite, "config", "server.json")));

            outW.GetStringBuilder().Clear();
            int valCode = await KiteCliRunner.ExecuteAsync(["validate", "--home", dotKite], outW, errW);
            Assert.Equal(0, valCode);
            Assert.Contains("[configVersion=1.0]", outW.ToString());
        }
        finally
        {
            try { Directory.Delete(tempProject, true); } catch { }
        }
    }
}
