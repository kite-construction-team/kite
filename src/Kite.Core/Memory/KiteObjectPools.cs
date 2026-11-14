using System.Text;
using Kite.Core.Models;
using Microsoft.Extensions.ObjectPool;

namespace Kite.Core.Memory;

/// <summary>
/// Central object pools for hot-path request routing and log formatting contexts.
/// </summary>
public static class KiteObjectPools
{
    private static readonly DefaultObjectPoolProvider Provider = new()
    {
        MaximumRetained = Environment.ProcessorCount * 64
    };

    public static readonly ObjectPool<StringBuilder> StringBuilderPool =
        Provider.CreateStringBuilderPool(initialCapacity: 256, maximumRetainedCapacity: 4096);

    public static readonly ObjectPool<KiteRouteMatchContext> RouteContextPool =
        Provider.Create(new DefaultPooledObjectPolicy<KiteRouteMatchContext>());

    public static readonly ObjectPool<SegmentedBufferStream> BufferStreamPool =
        Provider.Create(new DefaultPooledObjectPolicy<SegmentedBufferStream>());
}

/// <summary>
/// Reusable per-request route resolution state rented from <see cref="KiteObjectPools.RouteContextPool"/>.
/// Implements <see cref="IResettable"/> so it resets cleanly without heap allocations.
/// </summary>
public sealed class KiteRouteMatchContext : IResettable
{
    public SiteConfig? Site { get; set; }
    public SiteApplicationConfig? Application { get; set; }
    public VirtualDirectoryConfig? VirtualDirectory { get; set; }
    public int MatchedPrefixLength { get; set; }
    public long RequestStartTimestamp { get; set; }

    public ReadOnlySpan<char> GetRemainingSubPath(ReadOnlySpan<char> fullRequestPath)
    {
        if (fullRequestPath.IsEmpty || MatchedPrefixLength >= fullRequestPath.Length)
        {
            return "/";
        }

        if (MatchedPrefixLength <= 0)
        {
            return fullRequestPath;
        }

        ReadOnlySpan<char> slice = fullRequestPath.Slice(MatchedPrefixLength);
        return slice.IsEmpty ? "/" : slice;
    }

    public bool TryReset()
    {
        Site = null;
        Application = null;
        VirtualDirectory = null;
        MatchedPrefixLength = 0;
        RequestStartTimestamp = 0;
        return true;
    }
}
