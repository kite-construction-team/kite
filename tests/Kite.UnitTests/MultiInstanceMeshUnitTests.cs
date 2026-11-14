using Kite.Core.Models;
using Kite.Core.Storage;
using Xunit;

namespace Kite.UnitTests;

public sealed class MultiInstanceMeshUnitTests
{
    [Fact]
    public void KiteHomeLayout_DiscoversNearestDotKiteDirectory_UnlessExplicitHomeSpecified()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "kite_dot_" + Guid.NewGuid().ToString("N"));
        try
        {
            string projectRoot = Path.Combine(tempRoot, "orders-api");
            string dotKite = Path.Combine(projectRoot, ".kite");
            string nestedSrc = Path.Combine(projectRoot, "src", "Controllers");
            Directory.CreateDirectory(dotKite);
            Directory.CreateDirectory(nestedSrc);

            KiteHomeLayout discovered = KiteHomeLayout.ResolveFromArgsAndEnv([], workingDirectory: nestedSrc);
            Assert.Equal(Path.GetFullPath(dotKite), discovered.RootDirectory);

            string explicitHome = Path.Combine(tempRoot, "custom-home");
            KiteHomeLayout overridden = KiteHomeLayout.ResolveFromArgsAndEnv(["--home", explicitHome], workingDirectory: nestedSrc);
            Assert.Equal(Path.GetFullPath(explicitHome), overridden.RootDirectory);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    [Fact]
    public void KiteHomeLock_PreventsConcurrentServerOnSameHome_AndReleasesOnDispose()
    {
        string tempHome = Path.Combine(Path.GetTempPath(), "kite_lock_" + Guid.NewGuid().ToString("N"));
        try
        {
            var layout = new KiteHomeLayout(tempHome);
            using (KiteHomeLock firstLock = KiteHomeLock.Acquire(layout))
            {
                Assert.True(File.Exists(firstLock.LockPath));
                Assert.Throws<KiteHomeLockedException>(() => KiteHomeLock.Acquire(layout));
            }

            using KiteHomeLock reacquired = KiteHomeLock.Acquire(layout);
            Assert.True(File.Exists(reacquired.LockPath));
        }
        finally
        {
            try { Directory.Delete(tempHome, true); } catch { }
        }
    }

    [Fact]
    public async Task KiteInstanceRegistry_TracksLiveInstances_AndPrunesOrphanedEntries()
    {
        string tempReg = Path.Combine(Path.GetTempPath(), "kite_reg_" + Guid.NewGuid().ToString("N"));
        string tempHome = Path.Combine(Path.GetTempPath(), "kite_reg_home_" + Guid.NewGuid().ToString("N"));
        try
        {
            var registry = new KiteInstanceRegistry(tempReg);
            var desc = new InstanceDescriptor
            {
                KiteHomePath = tempHome,
                ServerName = "Orders-Dev-Node",
                Pid = Environment.ProcessId,
                StartedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Role = InstanceRole.MeshLeader,
                DirectHttpUrl = "http://127.0.0.1:8080",
                HostedDomains = ["orders.localhost"]
            };

            await using KiteInstanceRegistry.InstanceRegistrationHandle reg = await registry.RegisterAsync(desc);

            // Drop an orphaned descriptor without an active lock
            string orphanJson = Path.Combine(tempReg, "stale-node-0000.json");
            await File.WriteAllTextAsync(orphanJson, "{\"instanceId\":\"stale-node-0000\",\"serverName\":\"Ghost\"}");

            IReadOnlyList<InstanceDescriptor> live = await registry.ListLiveInstancesAsync();
            Assert.Single(live);
            Assert.Equal("Orders-Dev-Node", live[0].ServerName);
            Assert.False(File.Exists(orphanJson));
        }
        finally
        {
            try { Directory.Delete(tempReg, true); } catch { }
            try { Directory.Delete(tempHome, true); } catch { }
        }
    }
}
