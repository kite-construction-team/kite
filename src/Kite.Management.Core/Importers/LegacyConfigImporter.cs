using System.Text.RegularExpressions;
using System.Xml.Linq;
using Kite.Core.Models;

namespace Kite.Management.Core.Importers;

/// <summary>
/// Result of importing a third-party web server configuration (<c>nginx.conf</c>, <c>Caddyfile</c>, or IIS <c>applicationHost.config</c>).
/// </summary>
public sealed record LegacyImportResult(
    IReadOnlyList<ApplicationPoolConfig> Pools,
    IReadOnlyList<SiteConfig> Sites,
    IReadOnlyList<UpstreamClusterConfig> Upstreams,
    IReadOnlyList<string> Notes);

/// <summary>
/// Dependency-free configuration translators converting <c>nginx.conf</c>, <c>Caddyfile</c>,
/// and IIS <c>applicationHost.config</c> definitions into native Kite server entities.
/// </summary>
public static partial class LegacyConfigImporter
{
    public static LegacyImportResult Import(string format, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(format);
        ArgumentNullException.ThrowIfNull(content);

        return format.Trim().ToLowerInvariant() switch
        {
            "nginx" or "nginx.conf" => ImportNginx(content),
            "caddy" or "caddyfile" => ImportCaddyfile(content),
            "iis" or "applicationhost" or "applicationhost.config" => ImportIisApplicationHost(content),
            _ => throw new ArgumentException($"Unsupported import format '{format}'. Expected: nginx, caddy, or iis.", nameof(format))
        };
    }

    public static LegacyImportResult ImportNginx(string nginxConf)
    {
        var sites = new List<SiteConfig>();
        var upstreams = new List<UpstreamClusterConfig>();
        var upstreamMap = new Dictionary<string, List<UpstreamDestinationConfig>>(StringComparer.OrdinalIgnoreCase);
        var notes = new List<string>();

        // 1. Parse named `upstream <name> { server host:port; ... }` blocks first
        foreach (Match upMatch in Regex.Matches(nginxConf, @"\bupstream\s+([^\s\{]+)\s*\{([^}]*)\}", RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            string upName = upMatch.Groups[1].Value.Trim();
            string upBody = upMatch.Groups[2].Value;
            var dests = new List<UpstreamDestinationConfig>();
            foreach (Match srvLine in Regex.Matches(upBody, @"\bserver\s+([^\s;]+)", RegexOptions.IgnoreCase))
            {
                string addr = NormalizeHttpAddress(srvLine.Groups[1].Value.Trim());
                dests.Add(new UpstreamDestinationConfig { Id = $"dest-{dests.Count + 1}", Address = addr });
            }
            if (dests.Count > 0)
            {
                upstreamMap[upName] = dests;
            }
        }

        int serverIndex = 0;
        foreach (string block in ExtractNginxServerBlocks(nginxConf))
        {
            serverIndex++;
            int listenPort = 80;
            bool isSsl = false;
            string host = "*";
            string rootPath = "";
            var routes = new List<SiteRouteRuleConfig>();
            string? rootUpstreamCluster = null;

            foreach (string rawLine in block.Split('\n'))
            {
                string line = rawLine.Trim().TrimEnd(';').Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                if (line.StartsWith("listen ", StringComparison.OrdinalIgnoreCase))
                {
                    string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && int.TryParse(parts[1].Split(':')[^1], out int p))
                    {
                        listenPort = p;
                    }
                    isSsl = isSsl || line.Contains("ssl", StringComparison.OrdinalIgnoreCase);
                }
                else if (line.StartsWith("server_name ", StringComparison.OrdinalIgnoreCase))
                {
                    string val = line["server_name ".Length..].Trim();
                    if (!string.IsNullOrWhiteSpace(val) && val != "_")
                    {
                        host = val.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                    }
                }
                else if (line.StartsWith("root ", StringComparison.OrdinalIgnoreCase))
                {
                    rootPath = line["root ".Length..].Trim();
                }
            }

            foreach ((string locPrefix, string locBody) in ExtractLocationBlocks(block))
            {
                Match proxyMatch = ProxyPassRegex().Match(locBody);
                if (proxyMatch.Success)
                {
                    string target = proxyMatch.Groups[1].Value.Trim();
                    string bareHost = target
                        .Replace("http://", "", StringComparison.OrdinalIgnoreCase)
                        .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
                        .Split('/')[0];

                    string clusterId;
                    if (upstreamMap.TryGetValue(bareHost, out List<UpstreamDestinationConfig>? poolDests))
                    {
                        clusterId = NormalizeId(bareHost);
                        if (!upstreams.Any(u => string.Equals(u.Id, clusterId, StringComparison.OrdinalIgnoreCase)))
                        {
                            upstreams.Add(new UpstreamClusterConfig { Id = clusterId, Destinations = poolDests });
                        }
                    }
                    else
                    {
                        clusterId = $"nginx-upstream-{upstreams.Count + 1}";
                        upstreams.Add(new UpstreamClusterConfig
                        {
                            Id = clusterId,
                            Destinations = [new UpstreamDestinationConfig { Id = "dest-1", Address = NormalizeHttpAddress(target) }]
                        });
                    }

                    if (locPrefix == "/")
                    {
                        rootUpstreamCluster = clusterId;
                    }
                    else
                    {
                        routes.Add(new SiteRouteRuleConfig
                        {
                            Id = $"route-{routes.Count + 1}",
                            PathPrefix = locPrefix,
                            TargetClusterId = clusterId
                        });
                    }
                }
            }

            string siteId = NormalizeId(host == "*" ? $"nginx-site-{serverIndex}" : host);
            sites.Add(new SiteConfig
            {
                Id = siteId,
                Name = $"Imported Nginx ({host})",
                DesiredState = DesiredResourceState.Started,
                Bindings =
                [
                    new SiteBindingConfig
                    {
                        Protocol = isSsl ? "https" : "http",
                        Port = listenPort,
                        Host = host,
                        Tls = isSsl ? new SiteTlsConfig { UseSelfSignedDevelopmentCert = true } : null
                    }
                ],
                RootApplication = new SiteApplicationConfig
                {
                    Path = "/",
                    PoolId = "default-pool",
                    PhysicalPath = rootPath,
                    HostingMode = rootUpstreamCluster is not null ? WorkloadHostingMode.ExternalReverseProxy : WorkloadHostingMode.StaticOnly,
                    UpstreamClusterId = rootUpstreamCluster
                },
                Features = new SiteFeaturesConfig
                {
                    Routes = routes
                }
            });
        }

        notes.Add($"Imported {sites.Count} server block(s) and {upstreams.Count} upstream cluster(s) from nginx.conf.");
        return new LegacyImportResult([], sites, upstreams, notes);
    }

    public static LegacyImportResult ImportCaddyfile(string caddyfile)
    {
        var sites = new List<SiteConfig>();
        var upstreams = new List<UpstreamClusterConfig>();
        var notes = new List<string>();

        string[] lines = caddyfile.Replace("\r\n", "\n").Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            string header = lines[i].Trim();
            i++;
            if (string.IsNullOrEmpty(header) || header.StartsWith('#'))
            {
                continue;
            }

            if (!header.EndsWith('{'))
            {
                continue;
            }

            string siteLabel = header[..^1].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            var bodyLines = new List<string>();
            int depth = 1;
            while (i < lines.Length && depth > 0)
            {
                string line = lines[i];
                i++;
                foreach (char ch in line)
                {
                    if (ch == '{') depth++;
                    else if (ch == '}') depth--;
                }
                if (depth > 0)
                {
                    bodyLines.Add(line.Trim());
                }
            }

            (string proto, string host, int port) = ParseCaddySiteAddress(siteLabel);
            string rootPath = "";
            string? upstreamAddr = null;
            var routes = new List<SiteRouteRuleConfig>();

            foreach (string line in bodyLines)
            {
                if (line.StartsWith('#') || line.Length == 0)
                {
                    continue;
                }

                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && parts[0].Equals("root", StringComparison.OrdinalIgnoreCase))
                {
                    rootPath = parts[^1];
                }
                else if (parts.Length >= 2 && parts[0].Equals("reverse_proxy", StringComparison.OrdinalIgnoreCase))
                {
                    if (parts[1].StartsWith('/'))
                    {
                        string pathPrefix = parts[1];
                        string target = parts.Length >= 3 ? parts[2] : "127.0.0.1:8080";
                        string clusterId = $"caddy-upstream-{upstreams.Count + 1}";
                        upstreams.Add(new UpstreamClusterConfig
                        {
                            Id = clusterId,
                            Destinations = [new UpstreamDestinationConfig { Id = "dest-1", Address = NormalizeHttpAddress(target) }]
                        });
                        routes.Add(new SiteRouteRuleConfig
                        {
                            Id = $"route-{routes.Count + 1}",
                            PathPrefix = pathPrefix,
                            TargetClusterId = clusterId
                        });
                    }
                    else
                    {
                        string clusterId = $"caddy-upstream-{upstreams.Count + 1}";
                        upstreams.Add(new UpstreamClusterConfig
                        {
                            Id = clusterId,
                            Destinations = [new UpstreamDestinationConfig { Id = "dest-1", Address = NormalizeHttpAddress(parts[1]) }]
                        });
                        upstreamAddr = clusterId;
                    }
                }
            }

            string siteId = NormalizeId(host == "*" ? $"caddy-site-{sites.Count + 1}" : host);
            sites.Add(new SiteConfig
            {
                Id = siteId,
                Name = $"Imported Caddy ({siteLabel})",
                DesiredState = DesiredResourceState.Started,
                Bindings =
                [
                    new SiteBindingConfig
                    {
                        Protocol = proto,
                        Port = port,
                        Host = host,
                        Tls = proto == "https" ? new SiteTlsConfig { UseSelfSignedDevelopmentCert = true } : null
                    }
                ],
                RootApplication = new SiteApplicationConfig
                {
                    Path = "/",
                    PoolId = "default-pool",
                    PhysicalPath = rootPath,
                    HostingMode = upstreamAddr is not null ? WorkloadHostingMode.ExternalReverseProxy : WorkloadHostingMode.StaticOnly,
                    UpstreamClusterId = upstreamAddr
                },
                Features = new SiteFeaturesConfig
                {
                    Routes = routes
                }
            });
        }

        notes.Add($"Imported {sites.Count} site block(s) and {upstreams.Count} upstream cluster(s) from Caddyfile.");
        return new LegacyImportResult([], sites, upstreams, notes);
    }

    public static LegacyImportResult ImportIisApplicationHost(string xmlContent)
    {
        XDocument doc = XDocument.Parse(xmlContent);
        var pools = new List<ApplicationPoolConfig>();
        var sites = new List<SiteConfig>();
        var notes = new List<string>();

        foreach (XElement poolEl in doc.Descendants("applicationPools").Elements("add"))
        {
            string name = (string?)poolEl.Attribute("name") ?? $"iis-pool-{pools.Count + 1}";
            string id = NormalizeId(name);
            int maxWorkers = int.TryParse((string?)poolEl.Element("processModel")?.Attribute("maxProcesses"), out int w) && w > 0 ? w : 1;
            string startMode = string.Equals((string?)poolEl.Attribute("startMode"), "AlwaysRunning", StringComparison.OrdinalIgnoreCase)
                ? "AlwaysRunning"
                : "OnDemand";

            pools.Add(new ApplicationPoolConfig
            {
                Id = id,
                Name = name,
                DesiredState = DesiredResourceState.Started,
                ProcessModel = new PoolProcessModelConfig
                {
                    WorkerCount = maxWorkers,
                    MinWorkers = maxWorkers,
                    MaxWorkers = Math.Max(4, maxWorkers),
                    StartMode = startMode == "AlwaysRunning" ? PoolStartMode.AlwaysRunning : PoolStartMode.OnDemand
                }
            });
        }

        foreach (XElement siteEl in doc.Descendants("sites").Elements("site"))
        {
            string name = (string?)siteEl.Attribute("name") ?? $"iis-site-{sites.Count + 1}";
            string id = NormalizeId(name);

            var bindings = new List<SiteBindingConfig>();
            foreach (XElement bindEl in siteEl.Elements("bindings").Elements("binding"))
            {
                string protocol = (string?)bindEl.Attribute("protocol") ?? "http";
                string info = (string?)bindEl.Attribute("bindingInformation") ?? "*:80:";
                string[] parts = info.Split(':');
                string ip = parts.Length >= 1 && !string.IsNullOrEmpty(parts[0]) ? parts[0] : "*";
                int port = parts.Length >= 2 && int.TryParse(parts[1], out int p) ? p : (protocol == "https" ? 443 : 80);
                string host = parts.Length >= 3 && !string.IsNullOrEmpty(parts[2]) ? parts[2] : "*";

                bindings.Add(new SiteBindingConfig
                {
                    Protocol = protocol,
                    IpAddress = ip,
                    Port = port,
                    Host = host,
                    Tls = string.Equals(protocol, "https", StringComparison.OrdinalIgnoreCase)
                        ? new SiteTlsConfig { UseSelfSignedDevelopmentCert = true }
                        : null
                });
            }

            SiteApplicationConfig rootApp = new();
            var nestedApps = new List<SiteApplicationConfig>();
            foreach (XElement appEl in siteEl.Elements("application"))
            {
                string appPath = (string?)appEl.Attribute("path") ?? "/";
                string appPool = NormalizeId((string?)appEl.Attribute("applicationPool") ?? "default-pool");
                string physPath = (string?)appEl.Elements("virtualDirectory").FirstOrDefault()?.Attribute("physicalPath") ?? "";

                var vdirs = new List<VirtualDirectoryConfig>();
                foreach (XElement vdEl in appEl.Elements("virtualDirectory"))
                {
                    string vpath = (string?)vdEl.Attribute("path") ?? "/";
                    if (vpath != "/")
                    {
                        vdirs.Add(new VirtualDirectoryConfig
                        {
                            VirtualPath = vpath,
                            PhysicalPath = (string?)vdEl.Attribute("physicalPath") ?? ""
                        });
                    }
                }

                var appCfg = new SiteApplicationConfig
                {
                    Path = appPath,
                    PoolId = appPool,
                    PhysicalPath = physPath,
                    HostingMode = WorkloadHostingMode.StaticOnly,
                    VirtualDirectories = vdirs
                };

                if (appPath == "/")
                {
                    rootApp = appCfg;
                }
                else
                {
                    nestedApps.Add(appCfg);
                }
            }

            sites.Add(new SiteConfig
            {
                Id = id,
                Name = name,
                DesiredState = DesiredResourceState.Started,
                Bindings = bindings,
                RootApplication = rootApp,
                NestedApplications = nestedApps
            });
        }

        notes.Add($"Imported {pools.Count} application pool(s) and {sites.Count} site(s) from IIS applicationHost.config.");
        return new LegacyImportResult(pools, sites, [], notes);
    }

    private static IEnumerable<string> ExtractNginxServerBlocks(string text)
    {
        foreach (Match m in Regex.Matches(text, @"\bserver\s*\{", RegexOptions.IgnoreCase))
        {
            int braceOpen = m.Index + m.Length - 1;
            int depth = 1;
            int pos = braceOpen + 1;
            while (pos < text.Length && depth > 0)
            {
                if (text[pos] == '{') depth++;
                else if (text[pos] == '}') depth--;
                pos++;
            }

            yield return text[(braceOpen + 1)..Math.Max(braceOpen + 1, pos - 1)];
        }
    }

    private static IEnumerable<(string Prefix, string Body)> ExtractLocationBlocks(string serverBlock)
    {
        int idx = 0;
        while ((idx = serverBlock.IndexOf("location ", idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int braceOpen = serverBlock.IndexOf('{', idx + 9);
            if (braceOpen < 0)
            {
                break;
            }

            string prefix = serverBlock[(idx + 9)..braceOpen].Trim();
            int depth = 1;
            int pos = braceOpen + 1;
            while (pos < serverBlock.Length && depth > 0)
            {
                if (serverBlock[pos] == '{') depth++;
                else if (serverBlock[pos] == '}') depth--;
                pos++;
            }

            yield return (string.IsNullOrEmpty(prefix) ? "/" : prefix, serverBlock[(braceOpen + 1)..Math.Max(braceOpen + 1, pos - 1)]);
            idx = pos;
        }
    }

    private static (string Proto, string Host, int Port) ParseCaddySiteAddress(string addr)
    {
        string work = addr.Trim();
        string proto = "https";
        int port = 443;
        if (work.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            proto = "http";
            port = 80;
            work = work["http://".Length..];
        }
        else if (work.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            work = work["https://".Length..];
        }

        if (work.StartsWith(':'))
        {
            proto = "http";
            int.TryParse(work[1..], out port);
            return (proto, "*", port > 0 ? port : 80);
        }

        int colonIdx = work.LastIndexOf(':');
        if (colonIdx > 0 && int.TryParse(work[(colonIdx + 1)..], out int customPort))
        {
            port = customPort;
            work = work[..colonIdx];
            if (port == 80) proto = "http";
        }

        return (proto, string.IsNullOrWhiteSpace(work) ? "*" : work, port);
    }

    private static string NormalizeHttpAddress(string target)
    {
        string trimmed = target.Trim().TrimEnd(';');
        return trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : $"http://{trimmed}";
    }

    private static string NormalizeId(string raw)
    {
        char[] chars = raw.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        string clean = new string(chars).Trim('-');
        return string.IsNullOrEmpty(clean) ? "imported-site" : clean;
    }

    [GeneratedRegex(@"proxy_pass\s+([^;\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex ProxyPassRegex();
}
