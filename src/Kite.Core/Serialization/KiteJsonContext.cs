using System.Text.Json;
using System.Text.Json.Serialization;
using Kite.Core.Models;

namespace Kite.Core.Serialization;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ServerConfig))]
[JsonSerializable(typeof(ApplicationPoolConfig))]
[JsonSerializable(typeof(SiteConfig))]
[JsonSerializable(typeof(SiteFeaturesConfig))]
[JsonSerializable(typeof(AcmeAccountRecord))]
[JsonSerializable(typeof(ServerStatus))]
[JsonSerializable(typeof(InstanceDescriptor))]
[JsonSerializable(typeof(List<InstanceDescriptor>))]
[JsonSerializable(typeof(ApplicationPoolStatus))]
[JsonSerializable(typeof(List<WorkerProcessStatus>))]
[JsonSerializable(typeof(SiteStatus))]
[JsonSerializable(typeof(FrebTraceRecord))]
[JsonSerializable(typeof(List<FrebTraceRecord>))]
[JsonSerializable(typeof(List<SiteConfig>))]
[JsonSerializable(typeof(List<ApplicationPoolConfig>))]
[JsonSerializable(typeof(ConfigSnapshotSummary))]
[JsonSerializable(typeof(List<ConfigSnapshotSummary>))]
[JsonSerializable(typeof(PipelineTraceResult))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<int, string>))]
public partial class KiteJsonContext : JsonSerializerContext
{
}
