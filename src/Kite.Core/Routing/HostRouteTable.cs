using System.Collections.Frozen;
using Kite.Core.Models;

namespace Kite.Core.Routing;

public sealed class CompiledSiteRoute
{
    public required SiteConfig Site { get; init; }
    public required SiteBindingConfig Binding { get; init; }
    public required VirtualPathTree PathTree { get; init; }
    public required CompiledSiteFeatures Features { get; init; }
}

/// <summary>
/// Lock-free, immutable multi-tenant routing snapshot mapping (LocalPort, HostHeader) and SNI Hostnames
/// to their <see cref="CompiledSiteRoute"/> with zero managed allocations on the request hot-path.
/// </summary>
public sealed class HostRouteTable
{
    private readonly FrozenDictionary<int, PortRouteGroup> _portGroups;
    private readonly FrozenDictionary<string, CompiledSiteRoute> _sniRoutes;
    private readonly FrozenDictionary<string, CompiledSiteRoute>.AlternateLookup<ReadOnlySpan<char>> _sniSpanLookup;
    private readonly CompiledSiteRoute? _defaultTlsRoute;
    private readonly List<string> _bindingConflicts = [];

    public ServerConfig ServerConfig { get; }

    public HostRouteTable(IEnumerable<SiteConfig> sites, ServerConfig? serverConfig = null)
    {
        ServerConfig = serverConfig ?? new ServerConfig();
        var portMap = new Dictionary<int, PortGroupBuilder>();
        var sniMap = new Dictionary<string, CompiledSiteRoute>(StringComparer.OrdinalIgnoreCase);
        CompiledSiteRoute? defaultTls = null;

        foreach (SiteConfig site in sites)
        {
            if (site.DesiredState != DesiredResourceState.Started)
            {
                continue;
            }

            var pathTree = new VirtualPathTree(site);
            CompiledSiteFeatures compiledFeatures = CompiledSiteFeatures.Compile(site, serverConfig);

            foreach (SiteBindingConfig binding in site.Bindings)
            {
                if (!portMap.TryGetValue(binding.Port, out PortGroupBuilder? builder))
                {
                    builder = new PortGroupBuilder();
                    portMap[binding.Port] = builder;
                }

                var compiled = new CompiledSiteRoute
                {
                    Site = site,
                    Binding = binding,
                    PathTree = pathTree,
                    Features = compiledFeatures
                };

                bool isWildcardHost = string.IsNullOrWhiteSpace(binding.Host) || binding.Host == "*";
                if (isWildcardHost)
                {
                    if (builder.WildcardRoute is not null)
                    {
                        _bindingConflicts.Add(
                            $"Port {binding.Port} wildcard host '*' conflict between site '{builder.WildcardRoute.Site.Id}' and '{site.Id}'.");
                    }
                    else
                    {
                        builder.WildcardRoute = compiled;
                    }
                }
                else
                {
                    string hostKey = binding.Host.Trim();
                    if (!builder.NamedHosts.TryAdd(hostKey, compiled))
                    {
                        _bindingConflicts.Add(
                            $"Port {binding.Port} host '{hostKey}' conflict between site '{builder.NamedHosts[hostKey].Site.Id}' and '{site.Id}'.");
                    }
                }

                if (string.Equals(binding.Protocol, "https", StringComparison.OrdinalIgnoreCase))
                {
                    defaultTls ??= compiled;
                    if (!isWildcardHost)
                    {
                        sniMap.TryAdd(binding.Host.Trim(), compiled);
                    }
                }
            }
        }

        _portGroups = portMap.ToFrozenDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Build());

        _sniRoutes = sniMap.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
        _sniSpanLookup = _sniRoutes.GetAlternateLookup<ReadOnlySpan<char>>();
        _defaultTlsRoute = defaultTls;
    }

    public IReadOnlyList<string> BindingConflicts => _bindingConflicts;
    public IEnumerable<int> ActivePorts => _portGroups.Keys;

    /// <summary>
    /// Resolves a site by TCP port and raw HTTP Host header value with zero string allocations.
    /// </summary>
    public bool TryMatchSite(int port, ReadOnlySpan<char> rawHostHeader, out CompiledSiteRoute? route)
    {
        if (TryMatchNamedSite(port, rawHostHeader, out route))
        {
            return true;
        }

        if (_portGroups.TryGetValue(port, out PortRouteGroup? group))
        {
            route = group.WildcardRoute;
            return route is not null;
        }

        route = null;
        return false;
    }

    /// <summary>
    /// Resolves only an explicit host-header binding (without wildcard '*' fallback).
    /// </summary>
    public bool TryMatchNamedSite(int port, ReadOnlySpan<char> rawHostHeader, out CompiledSiteRoute? route)
    {
        if (!_portGroups.TryGetValue(port, out PortRouteGroup? group))
        {
            route = null;
            return false;
        }

        ReadOnlySpan<char> hostName = ExtractHostWithoutPort(rawHostHeader);
        if (!hostName.IsEmpty && group.TryGetByHost(hostName, out route))
        {
            return true;
        }

        route = null;
        return false;
    }

    /// <summary>
    /// Resolves only an explicit SNI hostname binding (without default fallback certificate).
    /// </summary>
    public bool TryMatchNamedSni(ReadOnlySpan<char> sniHostName, out CompiledSiteRoute? route)
    {
        if (!sniHostName.IsEmpty && _sniSpanLookup.TryGetValue(sniHostName, out route))
        {
            return true;
        }

        route = null;
        return false;
    }

    /// <summary>
    /// Resolves an HTTPS site binding from the TLS ClientHello SNI hostname with zero string allocations.
    /// </summary>
    public bool TryMatchSni(ReadOnlySpan<char> sniHostName, out CompiledSiteRoute? route)
    {
        if (TryMatchNamedSni(sniHostName, out route))
        {
            return true;
        }

        route = _defaultTlsRoute;
        return route is not null;
    }

    public static ReadOnlySpan<char> ExtractHostWithoutPort(ReadOnlySpan<char> rawHost)
    {
        ReadOnlySpan<char> trimmed = rawHost.Trim();
        if (trimmed.IsEmpty)
        {
            return ReadOnlySpan<char>.Empty;
        }

        // IPv6 literal e.g. [::1]:8080
        if (trimmed[0] == '[')
        {
            int closeBracket = trimmed.IndexOf(']');
            return closeBracket > 0 ? trimmed[..(closeBracket + 1)] : trimmed;
        }

        int colonIndex = trimmed.LastIndexOf(':');
        return colonIndex >= 0 ? trimmed[..colonIndex] : trimmed;
    }

    private sealed class PortGroupBuilder
    {
        public Dictionary<string, CompiledSiteRoute> NamedHosts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public CompiledSiteRoute? WildcardRoute { get; set; }

        public PortRouteGroup Build() => new(NamedHosts.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase), WildcardRoute);
    }

    private sealed class PortRouteGroup(
        FrozenDictionary<string, CompiledSiteRoute> namedHosts,
        CompiledSiteRoute? wildcardRoute)
    {
        private readonly FrozenDictionary<string, CompiledSiteRoute>.AlternateLookup<ReadOnlySpan<char>> _spanLookup =
            namedHosts.GetAlternateLookup<ReadOnlySpan<char>>();

        public bool HasNamedHosts { get; } = namedHosts.Count > 0;

        public CompiledSiteRoute? WildcardRoute { get; } = wildcardRoute;

        public bool TryGetByHost(ReadOnlySpan<char> hostName, out CompiledSiteRoute? route)
        {
            if (!HasNamedHosts)
            {
                route = null;
                return false;
            }

            return _spanLookup.TryGetValue(hostName, out route);
        }
    }
}
