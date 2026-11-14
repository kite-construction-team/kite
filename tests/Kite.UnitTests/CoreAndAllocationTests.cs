using System.Security.Cryptography;
using Kite.Core.Memory;
using Kite.Core.Models;
using Kite.Core.Routing;
using Kite.Core.Serialization;
using Kite.Core.Storage;
using Kite.Server.HttpModules;
using Xunit;

namespace Kite.UnitTests;

public sealed class CoreAndAllocationTests
{
    [Fact]
    public void VirtualPathTree_MatchesRootNestedAndVirtualDirs_WithZeroAllocations()
    {
        var site = new SiteConfig
        {
            Id = "corp-site",
            DesiredState = DesiredResourceState.Started,
            RootApplication = new SiteApplicationConfig
            {
                Path = "/",
                PoolId = "root-pool",
                PhysicalPath = "C:/sites/root",
                VirtualDirectories =
                [
                    new VirtualDirectoryConfig { VirtualPath = "/media", PhysicalPath = "D:/shared/media" }
                ]
            },
            NestedApplications =
            [
                new SiteApplicationConfig
                {
                    Path = "/api",
                    PoolId = "api-pool",
                    PhysicalPath = "C:/sites/api",
                    VirtualDirectories =
                    [
                        new VirtualDirectoryConfig { VirtualPath = "/docs", PhysicalPath = "D:/shared/apidocs" }
                    ]
                },
                new SiteApplicationConfig
                {
                    Path = "/api/v2",
                    PoolId = "api-v2-pool",
                    PhysicalPath = "C:/sites/apiv2"
                }
            ]
        };

        var tree = new VirtualPathTree(site);

        // 1. Verify longest-prefix correctness
        VirtualRouteMatch rootMatch = tree.Match("/index.html".AsSpan());
        Assert.Equal("root-pool", rootMatch.Application.PoolId);
        Assert.Null(rootMatch.VirtualDirectory);
        Assert.Equal("/index.html", rootMatch.GetSubPath("/index.html".AsSpan()).ToString());

        // Segment boundary safety: /apiv2-other must NOT match /api
        VirtualRouteMatch boundaryMatch = tree.Match("/apiv2-other/item".AsSpan());
        Assert.Equal("root-pool", boundaryMatch.Application.PoolId);

        // Root virtual directory match
        VirtualRouteMatch mediaMatch = tree.Match("/media/banners/hero.png".AsSpan());
        Assert.Equal("root-pool", mediaMatch.Application.PoolId);
        Assert.NotNull(mediaMatch.VirtualDirectory);
        Assert.Equal("D:/shared/media", mediaMatch.GetPhysicalTargetRoot());
        Assert.Equal("/banners/hero.png", mediaMatch.GetSubPath("/media/banners/hero.png".AsSpan()).ToString());

        // Nested application match
        VirtualRouteMatch apiMatch = tree.Match("/api/users/42".AsSpan());
        Assert.Equal("api-pool", apiMatch.Application.PoolId);
        Assert.Equal("/users/42", apiMatch.GetSubPath("/api/users/42".AsSpan()).ToString());

        // Deeper nested application (/api/v2 beats /api)
        VirtualRouteMatch apiV2Match = tree.Match("/api/v2/orders".AsSpan());
        Assert.Equal("api-v2-pool", apiV2Match.Application.PoolId);
        Assert.Equal("/orders", apiV2Match.GetSubPath("/api/v2/orders".AsSpan()).ToString());

        // Virtual directory inside nested application (/api/docs)
        VirtualRouteMatch nestedVdirMatch = tree.Match("/api/docs/openapi.json".AsSpan());
        Assert.Equal("api-pool", nestedVdirMatch.Application.PoolId);
        Assert.NotNull(nestedVdirMatch.VirtualDirectory);
        Assert.Equal("D:/shared/apidocs", nestedVdirMatch.GetPhysicalTargetRoot());
        Assert.Equal("/openapi.json", nestedVdirMatch.GetSubPath("/api/docs/openapi.json".AsSpan()).ToString());

        // 2. Assert 0 bytes allocated on hot path
        ReadOnlyMemory<char> pathMemory = "/api/v2/orders/999".AsMemory();
        for (int i = 0; i < 100; i++)
        {
            _ = tree.Match(pathMemory.Span);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            _ = tree.Match(pathMemory.Span);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
    }

    [Fact]
    public void HostRouteTable_MatchesHostAndSni_WithZeroAllocations()
    {
        var siteA = new SiteConfig
        {
            Id = "site-a",
            DesiredState = DesiredResourceState.Started,
            Bindings =
            [
                new SiteBindingConfig { Protocol = "http", Port = 8080, Host = "alpha.example.local" },
                new SiteBindingConfig { Protocol = "https", Port = 8443, Host = "alpha.example.local" }
            ]
        };

        var siteWildcard = new SiteConfig
        {
            Id = "site-wildcard",
            DesiredState = DesiredResourceState.Started,
            Bindings =
            [
                new SiteBindingConfig { Protocol = "http", Port = 8080, Host = "*" }
            ]
        };

        var table = new HostRouteTable([siteA, siteWildcard]);
        Assert.Empty(table.BindingConflicts);

        Assert.True(table.TryMatchSite(8080, "alpha.example.local:8080".AsSpan(), out CompiledSiteRoute? matchedA));
        Assert.Equal("site-a", matchedA!.Site.Id);

        Assert.True(table.TryMatchSite(8080, "unknown.example.local:8080".AsSpan(), out CompiledSiteRoute? matchedWild));
        Assert.Equal("site-wildcard", matchedWild!.Site.Id);

        Assert.True(table.TryMatchSni("ALPHA.EXAMPLE.LOCAL".AsSpan(), out CompiledSiteRoute? sniMatch));
        Assert.Equal("site-a", sniMatch!.Site.Id);

        ReadOnlyMemory<char> hostMem = "alpha.example.local:8080".AsMemory();
        for (int i = 0; i < 100; i++)
        {
            table.TryMatchSite(8080, hostMem.Span, out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            table.TryMatchSite(8080, hostMem.Span, out _);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
    }

    [Fact]
    public void W3CAccessLogFormatter_FormatsIntoSpan_WithZeroAllocations()
    {
        DateTimeOffset ts = new(2026, 11, 11, 19, 30, 0, TimeSpan.Zero);

        // Warmup
        Span<char> warmBuf = stackalloc char[256];
        for (int i = 0; i < 50; i++)
        {
            W3CAccessLogFormatter.TryFormatLine(
                warmBuf, ts, "10.0.0.1", "GET", "/index.html", "id=1", 8080, "example.local", 200, 1024, 3, out _);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        int written = 0;
        Span<char> lineBuf = stackalloc char[256];
        for (int i = 0; i < 1000; i++)
        {
            W3CAccessLogFormatter.TryFormatLine(
                lineBuf, ts, "10.0.0.1", "GET", "/index.html", "id=1", 8080, "example.local", 200, 1024, 3, out written);
        }
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0, after - before);
        string formatted = new(lineBuf[..written]);
        Assert.Equal("2026-11-11 19:30:00 10.0.0.1 GET /index.html id=1 8080 example.local 200 1024 3\n", formatted);
    }

    [Fact]
    public async Task SegmentedBufferStream_Buffers256KB_Across16KBChunks_WithoutLohAllocation()
    {
        byte[] expected = RandomNumberGenerator.GetBytes(256 * 1024);

        await using var stream = new SegmentedBufferStream(segmentSize: 16 * 1024);
        await stream.WriteAsync(expected);

        Assert.Equal(256 * 1024, stream.Length);
        Assert.Equal(16, stream.SegmentCount);

        using PooledBytePayload payload = stream.DetachPayload();
        Assert.Equal(256 * 1024, payload.Length);

        await using Stream reader = payload.CreateReadStream();
        var roundTrip = new byte[expected.Length];
        int totalRead = 0;
        while (totalRead < roundTrip.Length)
        {
            int r = await reader.ReadAsync(roundTrip.AsMemory(totalRead, Math.Min(7000, roundTrip.Length - totalRead)));
            if (r == 0) break;
            totalRead += r;
        }

        Assert.Equal(expected.Length, totalRead);
        Assert.Equal(expected, roundTrip);
    }

    [Fact]
    public async Task AtomicJsonStore_And_FileSystemConfigMonitor_DeduplicatesHashAndNotifiesOnChanges()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"kite-unit-{Guid.NewGuid():N}");
        var layout = new KiteHomeLayout(tempRoot);

        try
        {
            await using var monitor = new FileSystemConfigMonitor(layout);
            int changeCount = 0;
            monitor.SnapshotChanged += _ =>
            {
                Interlocked.Increment(ref changeCount);
                return ValueTask.CompletedTask;
            };

            ConfigSnapshot initial = await monitor.InitializeAndStartAsync(seedDefaultsIfEmpty: true);
            Assert.Contains("default-pool", initial.Pools.Keys);

            // Write a new site atomically
            var site = new SiteConfig
            {
                Id = "marketing-site",
                Name = "Marketing Site",
                DesiredState = DesiredResourceState.Started
            };
            await AtomicJsonStore.WriteAtomicAsync(layout.GetSiteConfigPath(site.Id), site, KiteJsonContext.Default.SiteConfig);

            await monitor.ReloadIfChangedAsync();
            Assert.Contains("marketing-site", monitor.CurrentSnapshot.Sites.Keys);
            Assert.True(Volatile.Read(ref changeCount) >= 1);
            int countAfterFirstWrite = Volatile.Read(ref changeCount);

            // Writing the exact same site content produces the same SHA-256 hash -> ReloadIfChangedAsync returns false and does not fire SnapshotChanged!
            await AtomicJsonStore.WriteAtomicAsync(layout.GetSiteConfigPath(site.Id), site, KiteJsonContext.Default.SiteConfig);
            bool duplicateChange = await monitor.ReloadIfChangedAsync();
            Assert.False(duplicateChange);
            Assert.Equal(countAfterFirstWrite, Volatile.Read(ref changeCount));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                try { Directory.Delete(tempRoot, recursive: true); } catch { }
            }
        }
    }
}
