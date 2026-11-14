using System.Collections.Frozen;
using Kite.Core.Memory;
using Kite.Core.Models;

namespace Kite.Core.Routing;

public readonly record struct VirtualRouteMatch(
    SiteApplicationConfig Application,
    VirtualDirectoryConfig? VirtualDirectory,
    int MatchedPrefixLength)
{
    public ReadOnlySpan<char> GetSubPath(ReadOnlySpan<char> requestPath)
    {
        if (requestPath.IsEmpty || MatchedPrefixLength >= requestPath.Length)
        {
            return "/";
        }

        if (MatchedPrefixLength <= 0)
        {
            return requestPath;
        }

        ReadOnlySpan<char> slice = requestPath.Slice(MatchedPrefixLength);
        return slice.IsEmpty ? "/" : slice;
    }

    public string GetPhysicalTargetRoot() =>
        VirtualDirectory is not null ? VirtualDirectory.PhysicalPath : Application.PhysicalPath;
}

/// <summary>
/// Precompiled, immutable segment-prefix lookup table for resolving a Site's RootApplication,
/// NestedApplications, and VirtualDirectories in zero heap allocations using <see cref="ReadOnlySpan{T}"/>.
/// </summary>
public sealed class VirtualPathTree
{
    private readonly SiteConfig _site;
    private readonly VirtualRouteMatch _rootMatch;
    private readonly FrozenDictionary<string, VirtualRouteMatch> _routes;
    private readonly FrozenDictionary<string, VirtualRouteMatch>.AlternateLookup<ReadOnlySpan<char>> _spanLookup;

    public VirtualPathTree(SiteConfig site)
    {
        ArgumentNullException.ThrowIfNull(site);
        _site = site;
        _rootMatch = new VirtualRouteMatch(site.RootApplication, null, 0);

        var map = new Dictionary<string, VirtualRouteMatch>(StringComparer.OrdinalIgnoreCase);

        // Register root application's virtual directories
        RegisterApplicationVirtualDirectories(map, "/", site.RootApplication);

        // Register nested applications and their virtual directories
        foreach (SiteApplicationConfig nestedApp in site.NestedApplications)
        {
            string normalizedAppPath = NormalizePrefix(nestedApp.Path);
            if (normalizedAppPath.Length > 1) // not "/"
            {
                map[normalizedAppPath] = new VirtualRouteMatch(nestedApp, null, normalizedAppPath.Length);
                RegisterApplicationVirtualDirectories(map, normalizedAppPath, nestedApp);
            }
        }

        _routes = map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _spanLookup = _routes.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public SiteConfig Site => _site;

    /// <summary>
    /// Matches a request URL path against all Nested Applications and Virtual Directories using
    /// longest-segment-prefix matching with zero heap allocations.
    /// </summary>
    public VirtualRouteMatch Match(ReadOnlySpan<char> requestPath)
    {
        if (requestPath.IsEmpty || _routes.Count == 0)
        {
            return _rootMatch;
        }

        // Trim trailing slash if length > 1
        ReadOnlySpan<char> current = requestPath;
        while (current.Length > 1 && current[^1] == '/')
        {
            current = current[..^1];
        }

        while (current.Length > 1)
        {
            if (_spanLookup.TryGetValue(current, out VirtualRouteMatch matched))
            {
                return new VirtualRouteMatch(matched.Application, matched.VirtualDirectory, current.Length);
            }

            int lastSlash = current.LastIndexOf('/');
            if (lastSlash <= 0)
            {
                break;
            }

            current = current[..lastSlash];
        }

        return _rootMatch;
    }

    public void MatchInto(ReadOnlySpan<char> requestPath, KiteRouteMatchContext context)
    {
        VirtualRouteMatch matched = Match(requestPath);
        context.Site = _site;
        context.Application = matched.Application;
        context.VirtualDirectory = matched.VirtualDirectory;
        context.MatchedPrefixLength = matched.MatchedPrefixLength;
    }

    private static void RegisterApplicationVirtualDirectories(
        Dictionary<string, VirtualRouteMatch> map,
        string appPrefix,
        SiteApplicationConfig app)
    {
        foreach (VirtualDirectoryConfig vdir in app.VirtualDirectories)
        {
            string normalizedVdir = NormalizePrefix(vdir.VirtualPath);
            if (normalizedVdir.Length <= 1)
            {
                continue;
            }

            string combined = appPrefix == "/"
                ? normalizedVdir
                : string.Concat(appPrefix, normalizedVdir);

            map[combined] = new VirtualRouteMatch(app, vdir, combined.Length);
        }
    }

    public static string NormalizePrefix(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "/";
        }

        string trimmed = path.Trim().Replace('\\', '/');
        if (!trimmed.StartsWith('/'))
        {
            trimmed = "/" + trimmed;
        }

        while (trimmed.Length > 1 && trimmed.EndsWith('/'))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed;
    }
}
