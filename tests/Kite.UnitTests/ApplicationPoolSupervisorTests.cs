using Kite.Core.Models;
using Kite.Server.Pools;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Kite.UnitTests;

public sealed class ApplicationPoolSupervisorTests
{
    [Fact]
    public void BuildGcEnvironmentVariables_ConfiguresDatasServerAndHexHeapLimit()
    {
        var pool = new ApplicationPoolConfig
        {
            Id = "high-perf-pool",
            ProcessModel = new PoolProcessModelConfig
            {
                GcMode = PoolGcMode.DatasServer
            },
            Recycling = new PoolRecyclingConfig
            {
                MaxMemoryMegabytes = 512
            }
        };

        Dictionary<string, string> env = ApplicationPoolSupervisor.BuildGcEnvironmentVariables(pool);

        Assert.Equal("1", env["DOTNET_gcServer"]);
        Assert.Equal("1", env["DOTNET_GCDynamicAdaptationMode"]);
        Assert.Equal("1", env["DOTNET_gcConcurrent"]);
        // 512 MB = 536,870,912 bytes = 0x20000000
        Assert.Equal("20000000", env["DOTNET_GCHeapHardLimit"]);
        Assert.Equal("55", env["DOTNET_GCHighMemPercent"]);
    }

    [Fact]
    public async Task Supervisor_SupportsWebGarden_OverlappedRecycle_IdleSpinDown_AndColdStartWakeup()
    {
        var fakeTime = new FakeClock();
        var mockLauncher = new FakeWorkerLauncher();

        var poolCfg = new ApplicationPoolConfig
        {
            Id = "garden-pool",
            Name = "Web Garden Pool",
            DesiredState = DesiredResourceState.Started,
            RecycleVersion = 1,
            ProcessModel = new PoolProcessModelConfig
            {
                WorkerCount = 2,
                IdleTimeoutMinutes = 10
            }
        };

        await using var supervisor = new ApplicationPoolSupervisor(poolCfg, mockLauncher, fakeTime);
        await supervisor.StartOrReconcileAsync(poolCfg);

        ApplicationPoolStatus status1 = supervisor.GetStatusSnapshot();
        Assert.Equal(RuntimeResourceState.Started, status1.ActualState);
        Assert.Equal(2, status1.ActiveWorkers.Count);
        string firstGenWorker0 = status1.ActiveWorkers[0].WorkerId;

        // 1. Trigger Overlapped Recycle via RecycleVersion increment (1 -> 2)
        var updatedCfg = new ApplicationPoolConfig
        {
            Id = "garden-pool",
            Name = "Web Garden Pool",
            DesiredState = DesiredResourceState.Started,
            RecycleVersion = 2,
            ProcessModel = new PoolProcessModelConfig
            {
                WorkerCount = 2,
                IdleTimeoutMinutes = 10
            }
        };

        await supervisor.StartOrReconcileAsync(updatedCfg);
        ApplicationPoolStatus status2 = supervisor.GetStatusSnapshot();
        Assert.Equal(2, status2.AppliedRecycleVersion);
        Assert.Equal(2, status2.ActiveWorkers.Count);
        Assert.NotEqual(firstGenWorker0, status2.ActiveWorkers[0].WorkerId);

        // 2. Advance virtual clock past IdleTimeoutMinutes (10 minutes) -> Pool spins down to Idle (0 workers)
        fakeTime.Advance(TimeSpan.FromMinutes(11));
        await supervisor.EvaluateMaintenanceTickAsync();

        ApplicationPoolStatus idleStatus = supervisor.GetStatusSnapshot();
        Assert.Equal(RuntimeResourceState.Idle, idleStatus.ActualState);
        Assert.Empty(idleStatus.ActiveWorkers);

        // 3. Cold-Start Wakeup: Next request wakes the pool back to Started
        var realLauncher = new RealWorkerProcessLauncher();
        await using var liveSupervisor = new ApplicationPoolSupervisor(updatedCfg, realLauncher, fakeTime);
        var appCfg = new SiteApplicationConfig
        {
            Path = "/api",
            PoolId = "garden-pool",
            HostingMode = WorkloadHostingMode.KiteWorkerIpc
        };

        await liveSupervisor.StartOrReconcileAsync(updatedCfg, appCfg);
        fakeTime.Advance(TimeSpan.FromMinutes(11));
        await liveSupervisor.EvaluateMaintenanceTickAsync();
        Assert.Equal(RuntimeResourceState.Idle, liveSupervisor.ActualState);

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/api/wakeup";
        ctx.Response.Body = new MemoryStream();

        await liveSupervisor.ForwardHttpAsync(ctx, appCfg, "/wakeup".AsMemory());
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Equal(RuntimeResourceState.Started, liveSupervisor.ActualState);
    }

    [Fact]
    public async Task Supervisor_RapidFailProtection_TripsCircuitBreakerAfterMaxCrashes()
    {
        var fakeTime = new FakeClock();
        var mockLauncher = new FakeWorkerLauncher();

        var poolCfg = new ApplicationPoolConfig
        {
            Id = "crash-pool",
            DesiredState = DesiredResourceState.Started,
            RecycleVersion = 1,
            ProcessModel = new PoolProcessModelConfig { WorkerCount = 1 },
            RapidFailProtection = new PoolRapidFailConfig
            {
                Enabled = true,
                MaxCrashes = 3,
                IntervalMinutes = 5
            }
        };

        await using var supervisor = new ApplicationPoolSupervisor(poolCfg, mockLauncher, fakeTime);
        await supervisor.StartOrReconcileAsync(poolCfg);

        // Simulate 3 consecutive crashes within 5 minutes
        for (int i = 0; i < 3; i++)
        {
            FakeWorkerInstance active = mockLauncher.LastCreated!;
            active.SimulateCrash();
            await Task.Delay(30);
        }

        Assert.Equal(RuntimeResourceState.RapidFailTripped, supervisor.ActualState);

        // Verify requests receive 503 Problem Details while tripped
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        await supervisor.ForwardHttpAsync(ctx, new SiteApplicationConfig(), "/".AsMemory());
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, ctx.Response.StatusCode);

        // Incrementing RecycleVersion resets the circuit breaker and restarts workers
        poolCfg.RecycleVersion = 2;
        await supervisor.StartOrReconcileAsync(poolCfg);
        Assert.Equal(RuntimeResourceState.Started, supervisor.ActualState);
    }

    [Fact]
    public async Task Supervisor_AutoscalesToZeroOnMinWorkers0_AndWakesOnDemand()
    {
        var fakeTime = new FakeClock();
        var mockLauncher = new FakeWorkerLauncher();

        var poolCfg = new ApplicationPoolConfig
        {
            Id = "zero-scale-pool",
            Name = "Scale To Zero Pool",
            DesiredState = DesiredResourceState.Started,
            RecycleVersion = 1,
            ProcessModel = new PoolProcessModelConfig
            {
                WorkerCount = 1,
                MinWorkers = 0,
                MaxWorkers = 3,
                IdleTimeoutMinutes = 0,
                ScaleDownIdleSeconds = 5
            }
        };

        await using var supervisor = new ApplicationPoolSupervisor(poolCfg, mockLauncher, fakeTime);
        await supervisor.StartOrReconcileAsync(poolCfg);
        Assert.Single(supervisor.GetStatusSnapshot().ActiveWorkers);

        // Advance clock past ScaleDownIdleSeconds (5s) -> MinWorkers=0 scales down to Idle (0 workers)
        fakeTime.Advance(TimeSpan.FromSeconds(10));
        await supervisor.EvaluateMaintenanceTickAsync();

        Assert.Equal(RuntimeResourceState.Idle, supervisor.ActualState);
        Assert.Empty(supervisor.GetStatusSnapshot().ActiveWorkers);

        // Verify live worker wakes back up on incoming request
        var realLauncher = new RealWorkerProcessLauncher();
        await using var liveSupervisor = new ApplicationPoolSupervisor(poolCfg, realLauncher, fakeTime);
        var appCfg = new SiteApplicationConfig { Path = "/api", PoolId = "zero-scale-pool", HostingMode = WorkloadHostingMode.KiteWorkerIpc };
        await liveSupervisor.StartOrReconcileAsync(poolCfg, appCfg);
        fakeTime.Advance(TimeSpan.FromSeconds(10));
        await liveSupervisor.EvaluateMaintenanceTickAsync();
        Assert.Equal(RuntimeResourceState.Idle, liveSupervisor.ActualState);

        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/api/ping";
        ctx.Response.Body = new MemoryStream();
        await liveSupervisor.ForwardHttpAsync(ctx, appCfg, "/ping".AsMemory());
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
        Assert.Equal(RuntimeResourceState.Started, liveSupervisor.ActualState);
    }

    private sealed class FakeClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 11, 19, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }

    private sealed class FakeWorkerLauncher : IWorkerProcessLauncher
    {
        private int _nextPid = 10_000;
        public FakeWorkerInstance? LastCreated { get; private set; }

        public ValueTask<IWorkerInstance> LaunchWorkerAsync(
            ApplicationPoolConfig pool,
            SiteApplicationConfig? appConfig,
            string workerId,
            DateTimeOffset startedAtUtc,
            CancellationToken ct)
        {
            int pid = Interlocked.Increment(ref _nextPid);
            var instance = new FakeWorkerInstance(
                workerId,
                pid,
                startedAtUtc,
                ApplicationPoolSupervisor.BuildGcEnvironmentVariables(pool));
            LastCreated = instance;
            return ValueTask.FromResult<IWorkerInstance>(instance);
        }
    }

    private sealed class FakeWorkerInstance(
        string workerId,
        int pid,
        DateTimeOffset startedAtUtc,
        IReadOnlyDictionary<string, string> gcEnv) : IWorkerInstance
    {
        public string WorkerId => workerId;
        public int ProcessId => pid;
        public string TransportAddress => $"fake-pipe-{workerId}";
        public WorkloadHostingMode HostingMode => WorkloadHostingMode.KiteWorkerIpc;
        public DateTimeOffset StartedAtUtc => startedAtUtc;
        public bool HasExited { get; private set; }
        public IReadOnlyDictionary<string, string> InjectedGcEnvironment => gcEnv;
        public event Action<IWorkerInstance>? Exited;

        public long GetWorkingSetBytes() => 32 * 1024 * 1024;
        public ValueTask<bool> ProbeReadinessAsync(CancellationToken ct) => ValueTask.FromResult(true);

        public void SimulateCrash()
        {
            HasExited = true;
            Exited?.Invoke(this);
        }

        public ValueTask DisposeAsync()
        {
            HasExited = true;
            return ValueTask.CompletedTask;
        }
    }
}
