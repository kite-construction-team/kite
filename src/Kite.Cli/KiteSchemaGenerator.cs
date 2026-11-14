using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Kite.Core.Serialization;

namespace Kite.Cli;

/// <summary>
/// Generates RFC 2020-12 JSON Schema documents directly from source-generated <see cref="KiteJsonContext"/>
/// type metadata with zero external packages.
/// </summary>
public static class KiteSchemaGenerator
{
    private static readonly JsonSchemaExporterOptions ExporterOptions = new()
    {
        TreatNullObliviousAsNonNullable = true
    };

    public static string GenerateSchema(string kind)
    {
        JsonNode node = kind.Trim().ToLowerInvariant() switch
        {
            "server" => JsonSchemaExporter.GetJsonSchemaAsNode(KiteJsonContext.Default.ServerConfig, ExporterOptions),
            "pool" => JsonSchemaExporter.GetJsonSchemaAsNode(KiteJsonContext.Default.ApplicationPoolConfig, ExporterOptions),
            "site" => JsonSchemaExporter.GetJsonSchemaAsNode(KiteJsonContext.Default.SiteConfig, ExporterOptions),
            _ => throw new ArgumentException($"Unknown schema target '{kind}'. Expected 'server', 'pool', 'site', or 'all'.")
        };

        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
