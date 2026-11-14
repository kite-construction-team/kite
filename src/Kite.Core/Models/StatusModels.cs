using System.Text.Json.Serialization;

namespace Kite.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter<RuntimeResourceState>))]
public enum RuntimeResourceState
{
    Stopped,
    Starting,
    Started,
    Recycling,
    Idle,
    Faulted,
    RapidFailTripped
}

[JsonConverter(typeof(JsonStringEnumConverter<InstanceRole>))]
public enum InstanceRole
{
    Standalone,
    MeshLeader,
    MeshPeer
}

public sealed class InstanceDescriptor
{
    public string InstanceId { get; set; } = "";
    public string KiteHomePath { get; set; } = "";
    public string ServerName { get; set; } = "";
    public int Pid { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public InstanceRole Role { get; set; } = InstanceRole.Standalone;
    public List<int> FrontDoorPorts { get; set; } = [];
    public string? DirectHttpUrl { get; set; }
    public string? DirectHttpsUrl { get; set; }
    public int? MeshHttpIngressPort { get; set; }
    public List<string> HostedDomains { get; set; } = [];
    public List<string> PeerRoutesSummary { get; set; } = [];
}

public sealed class ServerStatus
{
    public int ServerPid { get; set; }
    public string ServerName { get; set; } = "Kite-Production-Node";
    public string? InstanceId { get; set; }
    public InstanceRole MeshRole { get; set; } = InstanceRole.Standalone;
    public string? MeshDirectUrl { get; set; }
    public List<string> HostedDomains { get; set; } = [];
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
    public int ActiveBindingsCount { get; set; }
    public long TotalRequestsProcessed { get; set; }
    public long ActiveConnections { get; set; }
    public long TotalBytesSent { get; set; }
    public long TotalBytesReceived { get; set; }
    public double RequestsPerSecond { get; set; }
    public double MbpsOut { get; set; }
    public double MbpsIn { get; set; }
    public double AvgLatencyMs { get; set; }
    public double P99LatencyMs { get; set; }
    public long ManagedHeapBytes { get; set; }
    public long WorkingSetBytes { get; set; }
    public int Gen0Collections { get; set; }
    public int Gen1Collections { get; set; }
    public int Gen2Collections { get; set; }
    public int ActiveWorkerThreads { get; set; }
    public int ActiveIoThreads { get; set; }
    public long LockContentionCount { get; set; }
    public double CacheHitRatioPercent { get; set; }
    public List<string> ActiveBindings { get; set; } = [];
    public List<string> ConfigurationErrors { get; set; } = [];
    public List<TelemetrySampleBucket> RecentSamples { get; set; } = [];
    public List<UpstreamClusterStatus> Upstreams { get; set; } = [];
}

public sealed class TelemetrySampleBucket
{
    public long TimestampEpochSeconds { get; set; }
    public double RequestsPerSecond { get; set; }
    public long BytesSentPerSecond { get; set; }
    public long BytesReceivedPerSecond { get; set; }
    public int Status2xxCount { get; set; }
    public int Status3xxCount { get; set; }
    public int Status4xxCount { get; set; }
    public int Status5xxCount { get; set; }
    public double P99LatencyMs { get; set; }
}

public sealed class ApplicationPoolStatus
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DesiredResourceState DesiredState { get; set; }
    public RuntimeResourceState ActualState { get; set; }
    public long AppliedRecycleVersion { get; set; }
    public List<WorkerProcessStatus> ActiveWorkers { get; set; } = [];
    public long TotalRequestsServed { get; set; }
    public int RecentCrashCount { get; set; }
    public DateTimeOffset? LastRecycleUtc { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class WorkerProcessStatus
{
    public string WorkerId { get; set; } = "";
    public int ProcessId { get; set; }
    public string TransportAddress { get; set; } = "";
    public WorkloadHostingMode HostingMode { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public long MemoryBytes { get; set; }
    public long VirtualMemoryBytes { get; set; }
    public double CpuUsagePercent { get; set; }
    public long RequestsServed { get; set; }
    public int ActiveRequestsCount { get; set; }
    public int QueuedRequestsCount { get; set; }
    public int WorkerThreadsActive { get; set; }
    public int IoThreadsActive { get; set; }
    public long LockContentionCount { get; set; }
    public bool IsDraining { get; set; }
    public int RestartCount { get; set; }
    public List<string> RecentOutputLines { get; set; } = [];
    public Dictionary<string, string> InjectedGcEnvironment { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<WorkerThreadInfo> TopThreads { get; set; } = [];
}

public sealed class WorkerThreadInfo
{
    public int ThreadId { get; set; }
    public string State { get; set; } = "Running";
    public string Priority { get; set; } = "Normal";
    public string WaitReason { get; set; } = "-";
    public double UserTimeMs { get; set; }
}

public sealed class SiteStatus
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DesiredResourceState DesiredState { get; set; }
    public RuntimeResourceState ActualState { get; set; }
    public List<BindingRuntimeStatus> BindingStatuses { get; set; } = [];
    public List<TlsCertificateChainStatus> Certificates { get; set; } = [];
    public long TotalRequests { get; set; }
    public long Status2xxCount { get; set; }
    public long Status3xxCount { get; set; }
    public long Status4xxCount { get; set; }
    public long Status5xxCount { get; set; }
    public double RequestsPerSecond { get; set; }
    public double AvgLatencyMs { get; set; }
    public double P99LatencyMs { get; set; }
    public double CacheHitRatioPercent { get; set; }
    public long TotalBytesOut { get; set; }
    public long CacheHits { get; set; }
    public int ActiveWebSockets { get; set; }
    public List<string> ValidationErrors { get; set; } = [];
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class BindingRuntimeStatus
{
    public string Endpoint { get; set; } = "";
    public string Protocol { get; set; } = "http";
    public string Host { get; set; } = "*";
    public bool IsListening { get; set; }
    public bool TlsEnabled { get; set; }
    public string? Error { get; set; }
}

public sealed class TlsCertificateChainStatus
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 443;
    public string Subject { get; set; } = "";
    public string Issuer { get; set; } = "";
    public string Thumbprint { get; set; } = "";
    public DateTimeOffset NotBeforeUtc { get; set; }
    public DateTimeOffset NotAfterUtc { get; set; }
    public int DaysRemaining { get; set; }
    public bool Tls13Supported { get; set; } = true;
    public string ProvisioningMode { get; set; } = "DevelopmentSAN";
    public bool MtlsEnabled { get; set; }
}

public sealed class FrebTraceRecord
{
    public string TraceId { get; set; } = "";
    public string CorrelationId { get; set; } = "";
    public string SiteId { get; set; } = "";
    public DateTimeOffset TimestampUtc { get; set; }
    public string Method { get; set; } = "GET";
    public string Host { get; set; } = "";
    public string Path { get; set; } = "/";
    public string QueryString { get; set; } = "";
    public string ClientIp { get; set; } = "127.0.0.1";
    public string UserAgent { get; set; } = "";
    public int StatusCode { get; set; }
    public string? SubStatus { get; set; }
    public double TotalDurationMs { get; set; }
    public string TriggerReason { get; set; } = "StatusCode";
    public string? ExceptionMessage { get; set; }
    public string? ExceptionStackTrace { get; set; }
    public Dictionary<string, string> RequestHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> ResponseHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<FrebStageTiming> Stages { get; set; } = [];
}

public sealed class FrebStageTiming
{
    public string StageName { get; set; } = "";
    public double DurationMs { get; set; }
    public string Outcome { get; set; } = "Completed";
}

public sealed class UpstreamClusterStatus
{
    public string ClusterId { get; set; } = "";
    public LoadBalancingPolicy LoadBalancingPolicy { get; set; }
    public List<UpstreamDestinationStatus> Destinations { get; set; } = [];
}

public sealed class UpstreamDestinationStatus
{
    public string Id { get; set; } = "";
    public string Address { get; set; } = "";
    public bool IsHealthy { get; set; } = true;
    public bool CircuitOpen { get; set; }
    public int ActiveRequests { get; set; }
    public long TotalRequests { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Summarizes a persisted configuration snapshot for history and rollback.</summary>
public sealed class ConfigSnapshotSummary
{
    public string SnapshotId { get; set; } = "";
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string Reason { get; set; } = "Manual save";
    public string ResourceKind { get; set; } = "all";
    public string ResourceId { get; set; } = "*";
    public string Sha256 { get; set; } = "";
}

/// <summary>Captures a stage-by-stage simulated or live request pipeline trace.</summary>
public sealed class PipelineTraceResult
{
    public string Method { get; set; } = "GET";
    public string Host { get; set; } = "localhost";
    public string Path { get; set; } = "/";
    public string? MatchedSiteId { get; set; }
    public string? MatchedApplicationPath { get; set; }
    public string? MatchedRouteRuleId { get; set; }
    public string? ResolvedTarget { get; set; }
    public string RewrittenPath { get; set; } = "/";
    public int SimulatedStatusCode { get; set; } = 200;
    public string FinalOutcome { get; set; } = "Allowed";
    public Dictionary<string, string> AppliedHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<FrebStageTiming> Stages { get; set; } = [];
}
