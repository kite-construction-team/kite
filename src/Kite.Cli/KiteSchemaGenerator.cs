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
        string normalized = kind.Trim().ToLowerInvariant();
        JsonNode node = normalized switch
        {
            "server" => JsonSchemaExporter.GetJsonSchemaAsNode(KiteJsonContext.Default.ServerConfig, ExporterOptions),
            "pool" => JsonSchemaExporter.GetJsonSchemaAsNode(KiteJsonContext.Default.ApplicationPoolConfig, ExporterOptions),
            "site" => JsonSchemaExporter.GetJsonSchemaAsNode(KiteJsonContext.Default.SiteConfig, ExporterOptions),
            _ => throw new ArgumentException($"Unknown schema target '{kind}'. Expected 'server', 'pool', 'site', or 'all'.")
        };

        if (node is JsonObject obj)
        {
            obj["$id"] = $"https://kite.local/schemas/{normalized}.v1.json";
            if (obj["properties"] is JsonObject props && props["configVersion"] is JsonObject verProp)
            {
                verProp["const"] = Kite.Core.Models.KiteConfigVersion.Current;
                verProp["default"] = Kite.Core.Models.KiteConfigVersion.Current;
            }
        }

        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
}
