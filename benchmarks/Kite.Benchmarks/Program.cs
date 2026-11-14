using System.Diagnostics;
using Kite.Core.Memory;
using Kite.Core.Models;
using Kite.Core.Routing;
using Kite.Server.HttpModules;
using Kite.Server.Pools;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

Console.WriteLine("# Kite .NET 10 Performance & Allocation Benchmark Suite");
Console.WriteLine($"Runtime: .NET {Environment.Version} | Server GC: {System.Runtime.GCSettings.IsServerGC} | Processor Count: {Environment.ProcessorCount}");
Console.WriteLine();

// 1. Setup sample SiteConfig & HostRouteTable for routing benchmark
var site = new SiteConfig
{
    Id = "bench-site",
    Name = "Benchmark Site",
    DesiredState = DesiredResourceState.Started,
    Bindings =
    [
        new SiteBindingConfig { Protocol = "https", IpAddress = "*", Port = 8443, Host = "api.kite.local" }
    ],
    RootApplication = new SiteApplicationConfig
    {
        Path = "/",
        PoolId = "default-pool",
        VirtualDirectories =
        [
            new VirtualDirectoryConfig { VirtualPath = "/static/media", PhysicalPath = "C:/media" }
        ]
    },
    NestedApplications =
    [
        new SiteApplicationConfig { Path = "/api/v2/payments", PoolId = "payments-pool", HostingMode = WorkloadHostingMode.KiteWorkerIpc }
    ]
};

var routeTable = new HostRouteTable([site]);
ReadOnlyMemory<char> sampleHost = "api.kite.local:8443".AsMemory();
ReadOnlyMemory<char> samplePath = "/api/v2/payments/invoices/2026".AsMemory();

RunBenchmark("1. HostRouteTable + VirtualPathTree Match", iterations: 100_000, () =>
{
    if (routeTable.TryMatchSite(8443, sampleHost.Span, out CompiledSiteRoute? matched) && matched is not null)
    {
        KiteRouteMatchContext ctx = KiteObjectPools.RouteContextPool.Get();
        matched.PathTree.MatchInto(samplePath.Span, ctx);
        KiteObjectPools.RouteContextPool.Return(ctx);
    }
});

// 2. W3C Access Log Span Formatter Benchmark
DateTimeOffset now = DateTimeOffset.UtcNow;
RunBenchmark("2. W3CAccessLogFormatter.TryFormatLine (Span)", iterations: 100_000, () =>
{
    Span<char> buf = stackalloc char[256];
    W3CAccessLogFormatter.TryFormatLine(
        buf,
        now,
        "192.168.1.10",
        "GET",
        "/api/v2/payments",
        "page=1&limit=50",
        8443,
        "api.kite.local",
        200,
        4096,
        2,
        out _);
});

// 3. Mode A (OutOfProcessProxy HTTP Loopback) vs Mode B (KiteWorkerIpc Named Pipe)
await RunWorkloadComparisonAsync();

static void RunBenchmark(string name, int iterations, Action action)
{
    // Warmup
    for (int i = 0; i < 1000; i++)
    {
        action();
    }

    GC.Collect();
    GC.WaitForPendingFinalizers();
    GC.Collect();

    long allocBefore = GC.GetAllocatedBytesForCurrentThread();
    long startTicks = Stopwatch.GetTimestamp();

    for (int i = 0; i < iterations; i++)
    {
        action();
    }

    TimeSpan elapsed = Stopwatch.GetElapsedTime(startTicks);
    long allocAfter = GC.GetAllocatedBytesForCurrentThread();
    double nsPerOp = (elapsed.TotalNanoseconds) / iterations;
    double allocPerOp = (double)(allocAfter - allocBefore) / iterations;

    Console.WriteLine($"| {name,-45} | {nsPerOp,10:F1} ns/op | {allocPerOp,8:F1} B/op |");
}

static async Task RunWorkloadComparisonAsync()
{
    Console.WriteLine();
    Console.WriteLine("## Workload Hosting Mode A (OutOfProcessProxy) vs. Mode B (KiteWorkerIpc)");
    Console.WriteLine("| Mode | Throughput (req/s) | Avg Latency (us) | Managed Alloc / Req |");
    Console.WriteLine("| :--- | -----------------: | ---------------: | ------------------: |");

    // Setup Mode B: KiteWorkerIpc Pool
    var launcher = new RealWorkerProcessLauncher();
    var modeBPoolCfg = new ApplicationPoolConfig
    {
        Id = "bench-ipc-pool",
        DesiredState = DesiredResourceState.Started,
        ProcessModel = new PoolProcessModelConfig { WorkerCount = 1 }
    };
    var modeBAppCfg = new SiteApplicationConfig
    {
        Path = "/api",
        PoolId = "bench-ipc-pool",
        HostingMode = WorkloadHostingMode.KiteWorkerIpc
    };

    await using var ipcSupervisor = new ApplicationPoolSupervisor(modeBPoolCfg, launcher);
    await ipcSupervisor.StartOrReconcileAsync(modeBPoolCfg, modeBAppCfg);

    // Setup Mode A: Kestrel HTTP Upstream + OutOfProcessProxy Pool
    string upstreamPipe = $"kite-bench-proxy-{Guid.NewGuid():N}";
    WebApplicationBuilder upstreamBuilder = WebApplication.CreateSlimBuilder();
    upstreamBuilder.Logging.ClearProviders();
    if (OperatingSystem.IsWindows())
    {
        upstreamBuilder.WebHost.UseNamedPipes(options =>
        {
            options.CreateNamedPipeServerStream = static ctx => new System.IO.Pipes.NamedPipeServerStream(
                ctx.NamedPipeEndPoint.PipeName,
                System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.NamedPipeServerStream.MaxAllowedServerInstances,
                System.IO.Pipes.PipeTransmissionMode.Byte,
                System.IO.Pipes.PipeOptions.Asynchronous);
        });
    }
    upstreamBuilder.WebHost.ConfigureKestrel(k => k.ListenNamedPipe(upstreamPipe));
    await using WebApplication upstreamApp = upstreamBuilder.Build();
    upstreamApp.Run(async ctx =>
    {
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync("""{"status":"ok","mode":"OutOfProcessProxy"}""");
    });
    await upstreamApp.StartAsync();
    string upstreamAddress = $"http://{upstreamPipe}.pipe.local";

    var modeAPoolCfg = new ApplicationPoolConfig
    {
        Id = "bench-proxy-pool",
        DesiredState = DesiredResourceState.Started,
        ProcessModel = new PoolProcessModelConfig { WorkerCount = 1 }
    };
    var modeAAppCfg = new SiteApplicationConfig
    {
        Path = "/api",
        PoolId = "bench-proxy-pool",
        HostingMode = WorkloadHostingMode.ExternalReverseProxy,
        UpstreamUrl = upstreamAddress
    };

    await using var proxySupervisor = new ApplicationPoolSupervisor(modeAPoolCfg, launcher);
    await proxySupervisor.StartOrReconcileAsync(modeAPoolCfg, modeAAppCfg);

    await BenchmarkSupervisorAsync("Mode A: OutOfProcessProxy (HTTP Loopback)", proxySupervisor, modeAAppCfg, 500);
    await BenchmarkSupervisorAsync("Mode B: KiteWorkerIpc (Named Pipe IPC)", ipcSupervisor, modeBAppCfg, 500);

    await upstreamApp.StopAsync();
}

static async Task BenchmarkSupervisorAsync(
    string label,
    ApplicationPoolSupervisor supervisor,
    SiteApplicationConfig appConfig,
    int requestCount)
{
    // Warmup
    for (int i = 0; i < 25; i++)
    {
        var warmCtx = new DefaultHttpContext();
        warmCtx.Request.Method = "GET";
        warmCtx.Request.Scheme = "http";
        warmCtx.Request.Host = new HostString("localhost");
        warmCtx.Request.Path = "/api/bench";
        warmCtx.Response.Body = Stream.Null;
        await supervisor.ForwardHttpAsync(warmCtx, appConfig, "/bench".AsMemory());
    }

    long allocBefore = GC.GetTotalAllocatedBytes(precise: false);
    long start = Stopwatch.GetTimestamp();

    for (int i = 0; i < requestCount; i++)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Scheme = "http";
        ctx.Request.Host = new HostString("localhost");
        ctx.Request.Path = "/api/bench";
        ctx.Response.Body = Stream.Null;
        await supervisor.ForwardHttpAsync(ctx, appConfig, "/bench".AsMemory());
    }

    TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
    long allocAfter = GC.GetTotalAllocatedBytes(precise: false);

    double rps = requestCount / elapsed.TotalSeconds;
    double avgUs = elapsed.TotalMicroseconds / requestCount;
    double bytesPerReq = (double)(allocAfter - allocBefore) / requestCount;

    Console.WriteLine($"| {label,-42} | {rps,18:F0} | {avgUs,16:F1} | {bytesPerReq,16:F0} B |");
}
