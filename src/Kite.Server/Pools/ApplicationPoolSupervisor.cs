using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Kite.Core.Ipc;
using Kite.Core.Memory;
using Kite.Core.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Primitives;

namespace Kite.Server.Pools;

/// <summary>
/// Manages an IIS-style Application Pool lifecycle:
/// - Per-pool .NET 10 GC configuration (<c>DOTNET_gcServer</c>, <c>DOTNET_GCDynamicAdaptationMode</c>, <c>DOTNET_GCHeapHardLimit</c>)
/// - Web Garden multi-worker round-robin load balancing
/// - Idle Timeout spin-down and lazy cold-start wakeup on next request
/// - Zero-downtime Overlapped Recycling (via <c>RecycleVersion</c>, max requests, or memory threshold)
/// - Rapid-Fail Protection circuit breaker
/// - Dual transport forwarding: Mode A (HTTP Reverse Proxy via pooled <see cref="HttpMessageInvoker"/>) and Mode B (<c>Kite.Worker</c> Named Pipe IPC)
/// </summary>
public sealed class ApplicationPoolSupervisor : IAsyncDisposable
{
    private readonly IWorkerProcessLauncher _launcher;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly List<ManagedWorkerSlot> _activeSlots = [];
    private readonly List<ManagedWorkerSlot> _drainingSlots = [];
    private readonly List<Process> _sidecarProcesses = [];
    private readonly Queue<DateTimeOffset> _crashTimestamps = new();
    private readonly ObjectPool<KiteIpcRequestHeader> _ipcReqPool;
    private readonly ObjectPool<KiteIpcResponseHeader> _ipcRespPool;

    private ApplicationPoolConfig _config;
    private SiteApplicationConfig? _primaryAppConfig;
    private RuntimeResourceState _actualState = RuntimeResourceState.Stopped;
    private long _appliedRecycleVersion;
    private long _totalRequestsServed;
    private long _generationRequestsServed;
    private int _roundRobinIndex;
    private int _workerSequence;
    private int _lastPrimaryWorkerCount;
    private DateTimeOffset _lastActivityUtc;
    private DateTimeOffset? _lastRecycleUtc;
    private DateOnly? _lastScheduledRecycleDate;
    private string? _lastError;
    private bool _disposed;

    public ApplicationPoolSupervisor(
        ApplicationPoolConfig config,
        IWorkerProcessLauncher launcher,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(launcher);
        _config = config;
        _launcher = launcher;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _appliedRecycleVersion = config.RecycleVersion;
        _lastPrimaryWorkerCount = config.ProcessModel.WorkerCount;
        _lastActivityUtc = _timeProvider.GetUtcNow();

        var poolProvider = new DefaultObjectPoolProvider { MaximumRetained = 128 };
        _ipcReqPool = poolProvider.Create(new DefaultPooledObjectPolicy<KiteIpcRequestHeader>());
        _ipcRespPool = poolProvider.Create(new DefaultPooledObjectPolicy<KiteIpcResponseHeader>());
    }

    public string PoolId => _config.Id;
    public ApplicationPoolConfig Config => _config;
    public RuntimeResourceState ActualState => _actualState;
    public long AppliedRecycleVersion => _appliedRecycleVersion;
    public long TotalRequestsServed => Interlocked.Read(ref _totalRequestsServed);

    /// <summary>
    /// Computes the .NET 10 runtime Garbage Collection environment variables for a worker in this pool.
    /// </summary>
    public static Dictionary<string, string> BuildGcEnvironmentVariables(ApplicationPoolConfig pool)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        switch (pool.ProcessModel.GcMode)
        {
            case PoolGcMode.DatasServer:
                env["DOTNET_gcServer"] = "1";
                env["DOTNET_GCDynamicAdaptationMode"] = "1";
                env["DOTNET_gcConcurrent"] = "1";
                break;
            case PoolGcMode.Server:
                env["DOTNET_gcServer"] = "1";
                env["DOTNET_GCDynamicAdaptationMode"] = "0";
                env["DOTNET_gcConcurrent"] = "1";
                break;
            case PoolGcMode.Workstation:
                env["DOTNET_gcServer"] = "0";
                env["DOTNET_gcConcurrent"] = "1";
                break;
        }

        if (pool.Recycling.MaxMemoryMegabytes > 0)
        {
            long hardLimitBytes = pool.Recycling.MaxMemoryMegabytes * 1024L * 1024L;
            env["DOTNET_GCHeapHardLimit"] = hardLimitBytes.ToString("X");
            env["DOTNET_GCHighMemPercent"] = "55"; // hexadecimal 0x55 = 85%
        }

        return env;
    }

    public async ValueTask StartOrReconcileAsync(
        ApplicationPoolConfig newConfig,
        SiteApplicationConfig? primaryApp = null,
        CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool wasFaulted = _actualState == RuntimeResourceState.RapidFailTripped;
            bool recycleRequested = newConfig.RecycleVersion > _appliedRecycleVersion;
            bool stateChangedToStarted = _config.DesiredState == DesiredResourceState.Stopped &&
                                         newConfig.DesiredState == DesiredResourceState.Started;

            _config = newConfig;
            if (primaryApp is not null)
            {
                _primaryAppConfig = primaryApp;
            }

            if (newConfig.DesiredState == DesiredResourceState.Stopped)
            {
                await StopAllWorkersInternalAsync().ConfigureAwait(false);
                StopSidecarsInternal();
                _actualState = RuntimeResourceState.Stopped;
                _appliedRecycleVersion = newConfig.RecycleVersion;
                return;
            }

            if (wasFaulted && (recycleRequested || stateChangedToStarted))
            {
                _crashTimestamps.Clear();
                _lastError = null;
            }
            else if (wasFaulted)
            {
                return;
            }

            ReconcileSidecarsInternal(newConfig);

            bool scaleToZeroInitial = newConfig.ProcessModel.MinWorkers == 0 &&
                                      (newConfig.ProcessModel.StartMode == PoolStartMode.OnDemand || newConfig.ProcessModel.WorkerCount == 0);
            int targetInitialCount = scaleToZeroInitial ? 0 : Math.Max(1, newConfig.ProcessModel.WorkerCount);

            int previousTargetCount = _activeSlots.Count;
            bool isAutoscaledAboveBase = newConfig.ProcessModel.MaxWorkers > newConfig.ProcessModel.MinWorkers &&
                                         previousTargetCount > targetInitialCount &&
                                         previousTargetCount >= newConfig.ProcessModel.MinWorkers &&
                                         previousTargetCount <= newConfig.ProcessModel.MaxWorkers &&
                                         _lastPrimaryWorkerCount == newConfig.ProcessModel.WorkerCount;
            _lastPrimaryWorkerCount = newConfig.ProcessModel.WorkerCount;

            if (_activeSlots.Count == 0)
            {
                if (newConfig.ProcessModel.StartMode == PoolStartMode.OnDemand && !stateChangedToStarted || scaleToZeroInitial)
                {
                    _actualState = RuntimeResourceState.Idle;
                    _appliedRecycleVersion = newConfig.RecycleVersion;
                }
                else
                {
                    await SpawnWorkerGenerationInternalAsync(ct).ConfigureAwait(false);
                    _appliedRecycleVersion = newConfig.RecycleVersion;
                }
            }
            else if (recycleRequested || (targetInitialCount > 0 && _activeSlots.Count != targetInitialCount && !isAutoscaledAboveBase))
            {
                await PerformRecycleInternalAsync(ct).ConfigureAwait(false);
                _appliedRecycleVersion = newConfig.RecycleVersion;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Evaluates idle timeouts and memory-based recycling rules.
    /// </summary>
    public async ValueTask EvaluateMaintenanceTickAsync(CancellationToken ct = default)
    {
        if (_disposed)
        {
            return;
        }

        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_actualState != RuntimeResourceState.Started || _activeSlots.Count == 0)
            {
                return;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();

            // 1. Check Idle Timeout or Autoscale Scale-Down (supports MinWorkers == 0 scale-to-zero)
            if (_config.ProcessModel.IdleTimeoutMinutes > 0)
            {
                TimeSpan idleDuration = now - _lastActivityUtc;
                if (idleDuration >= TimeSpan.FromMinutes(_config.ProcessModel.IdleTimeoutMinutes))
                {
                    await StopAllWorkersInternalAsync().ConfigureAwait(false);
                    _actualState = RuntimeResourceState.Idle;
                    return;
                }
            }

            int minWorkers = Math.Max(0, _config.ProcessModel.MinWorkers);
            if (_activeSlots.Count > minWorkers &&
                _config.ProcessModel.ScaleDownIdleSeconds > 0 &&
                (now - _lastActivityUtc) >= TimeSpan.FromSeconds(_config.ProcessModel.ScaleDownIdleSeconds))
            {
                int totalInFlight = 0;
                for (int i = 0; i < _activeSlots.Count; i++)
                {
                    totalInFlight += Math.Max(0, Volatile.Read(ref _activeSlots[i].InFlightRequests));
                }

                if (totalInFlight == 0)
                {
                    while (_activeSlots.Count > minWorkers)
                    {
                        ManagedWorkerSlot last = _activeSlots[^1];
                        _activeSlots.RemoveAt(_activeSlots.Count - 1);
                        last.IsDraining = true;
                        await DisposeSlotSafeAsync(last).ConfigureAwait(false);
                    }

                    if (_activeSlots.Count == 0)
                    {
                        _actualState = RuntimeResourceState.Idle;
                        return;
                    }
                }
            }

            // 2. Check Periodic Restart or Max Memory Recycle
            bool shouldRecycle = false;
            if (_config.Recycling.PeriodicRestartMinutes > 0)
            {
                DateTimeOffset baseline = _lastRecycleUtc ?? _activeSlots[0].Worker.StartedAtUtc;
                if (now - baseline >= TimeSpan.FromMinutes(_config.Recycling.PeriodicRestartMinutes))
                {
                    shouldRecycle = true;
                }
            }

            if (!shouldRecycle && _config.Recycling.MaxMemoryMegabytes > 0)
            {
                long limitBytes = _config.Recycling.MaxMemoryMegabytes * 1024L * 1024L;
                for (int i = 0; i < _activeSlots.Count; i++)
                {
                    if (_activeSlots[i].Worker.GetWorkingSetBytes() > limitBytes)
                    {
                        shouldRecycle = true;
                        break;
                    }
                }
            }

            if (!shouldRecycle &&
                !string.IsNullOrWhiteSpace(_config.Recycling.ScheduledRestartUtcTime) &&
                TimeOnly.TryParseExact(_config.Recycling.ScheduledRestartUtcTime.Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly schedTime))
            {
                DateTime utcNow = now.UtcDateTime;
                DateOnly today = DateOnly.FromDateTime(utcNow);
                DateTimeOffset baseline = _lastRecycleUtc ?? _activeSlots[0].Worker.StartedAtUtc;
                var scheduledToday = new DateTimeOffset(today.ToDateTime(schedTime), TimeSpan.Zero);
                if (utcNow >= scheduledToday.UtcDateTime &&
                    baseline < scheduledToday &&
                    _lastScheduledRecycleDate != today)
                {
                    _lastScheduledRecycleDate = today;
                    shouldRecycle = true;
                }
            }

            if (shouldRecycle)
            {
                await PerformRecycleInternalAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// Selects a healthy worker (waking the pool from Idle if needed) and forwards the HTTP request.
    /// </summary>
    public async ValueTask ForwardHttpAsync(
        HttpContext context,
        SiteApplicationConfig appConfig,
        ReadOnlyMemory<char> subPath,
        CancellationToken ct = default)
    {
        ManagedWorkerSlot? slot = await AcquireWorkerSlotAsync(appConfig, ct).ConfigureAwait(false);
        if (slot is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsync(
                $$"""{"type":"https://RFC-editor.org/rfc/rfc9457","title":"Service Unavailable","status":503,"detail":"Application pool '{{_config.Id}}' is in state '{{_actualState}}'."}""",
                ct).ConfigureAwait(false);
            return;
        }

        Interlocked.Increment(ref slot.InFlightRequests);
        try
        {
            _lastActivityUtc = _timeProvider.GetUtcNow();
            long genReqs = Interlocked.Increment(ref _generationRequestsServed);
            Interlocked.Increment(ref _totalRequestsServed);
            Interlocked.Increment(ref slot.RequestsServed);

            if (appConfig.HostingMode == WorkloadHostingMode.KiteWorkerIpc)
            {
                await ForwardViaNamedPipeIpcAsync(context, slot, subPath, ct).ConfigureAwait(false);
            }
            else
            {
                await ForwardViaHttpProxyAsync(context, slot, appConfig, subPath, ct).ConfigureAwait(false);
            }

            // Trigger max-requests recycle asynchronously if configured
            if (_config.Recycling.MaxRequests > 0 && genReqs >= _config.Recycling.MaxRequests)
            {
                _ = Task.Run(() => TriggerRecycleAsync(CancellationToken.None));
            }
        }
        finally
        {
            int remaining = Interlocked.Decrement(ref slot.InFlightRequests);
            if (slot.IsDraining && remaining <= 0)
            {
                await DisposeSlotSafeAsync(slot).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask TriggerRecycleAsync(CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_config.DesiredState == DesiredResourceState.Started && _actualState != RuntimeResourceState.RapidFailTripped)
            {
                await PerformRecycleInternalAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public ApplicationPoolStatus GetStatusSnapshot()
    {
        var workers = new List<WorkerProcessStatus>(_activeSlots.Count + _drainingSlots.Count);
        foreach (ManagedWorkerSlot s in _activeSlots.ToArray())
        {
            workers.Add(ToWorkerStatus(s));
        }
        foreach (ManagedWorkerSlot s in _drainingSlots.ToArray())
        {
            workers.Add(ToWorkerStatus(s));
        }

        return new ApplicationPoolStatus
        {
            Id = _config.Id,
            Name = _config.Name,
            DesiredState = _config.DesiredState,
            ActualState = _actualState,
            AppliedRecycleVersion = _appliedRecycleVersion,
            ActiveWorkers = workers,
            TotalRequestsServed = Interlocked.Read(ref _totalRequestsServed),
            RecentCrashCount = _crashTimestamps.Count,
            LastRecycleUtc = _lastRecycleUtc,
            LastError = _lastError,
            UpdatedAtUtc = _timeProvider.GetUtcNow()
        };
    }

    private static WorkerProcessStatus ToWorkerStatus(ManagedWorkerSlot slot)
    {
        long virtualMem = 0;
        double cpuPct = 0.0;
        var threads = new List<WorkerThreadInfo>(8);

        try
        {
            long nowTicks = Stopwatch.GetTimestamp();
            if (slot.ShouldRefreshThreads(nowTicks, out List<WorkerThreadInfo> cachedThreads))
            {
                using Process? proc = slot.Worker.ProcessId == Environment.ProcessId
                    ? Process.GetCurrentProcess()
                    : (slot.Worker.ProcessId > 0 ? Process.GetProcessById(slot.Worker.ProcessId) : null);

                if (proc is not null && !proc.HasExited)
                {
                    virtualMem = proc.VirtualMemorySize64;
                    TimeSpan currentCpu = proc.TotalProcessorTime;
                    cpuPct = slot.SampleCpuPercent(currentCpu, nowTicks);

                    ProcessThreadCollection ptc = proc.Threads;
                    int limit = Math.Min(8, ptc.Count);
                    var fresh = new List<WorkerThreadInfo>(limit);
                    for (int i = 0; i < limit; i++)
                    {
                        try
                        {
                            ProcessThread pt = ptc[i];
                            fresh.Add(new WorkerThreadInfo
                            {
                                ThreadId = pt.Id,
                                State = pt.ThreadState.ToString(),
                                Priority = OperatingSystem.IsWindows() || OperatingSystem.IsLinux()
                                    ? pt.PriorityLevel.ToString()
                                    : $"Base:{pt.BasePriority}",
                                WaitReason = pt.ThreadState == System.Diagnostics.ThreadState.Wait ? pt.WaitReason.ToString() : "-",
                                UserTimeMs = Math.Round(pt.UserProcessorTime.TotalMilliseconds, 1)
                            });
                        }
                        catch
                        {
                        }
                    }
                    slot.UpdateCachedTelemetry(fresh, virtualMem, cpuPct);
                    threads = fresh;
                }
            }
            else
            {
                threads = cachedThreads;
                virtualMem = slot.CachedVirtualMemoryBytes;
                cpuPct = slot.CachedCpuPercent;
            }
        }
        catch
        {
        }

        ThreadPool.GetMaxThreads(out int maxWorker, out int maxIo);
        ThreadPool.GetAvailableThreads(out int availWorker, out int availIo);

        return new WorkerProcessStatus
        {
            WorkerId = slot.Worker.WorkerId,
            ProcessId = slot.Worker.ProcessId,
            TransportAddress = slot.Worker.TransportAddress,
            HostingMode = slot.Worker.HostingMode,
            StartedAtUtc = slot.Worker.StartedAtUtc,
            MemoryBytes = slot.Worker.GetWorkingSetBytes(),
            VirtualMemoryBytes = virtualMem,
            CpuUsagePercent = cpuPct,
            RequestsServed = Interlocked.Read(ref slot.RequestsServed),
            ActiveRequestsCount = Math.Max(0, Volatile.Read(ref slot.InFlightRequests)),
            QueuedRequestsCount = 0,
            WorkerThreadsActive = Math.Max(0, maxWorker - availWorker),
            IoThreadsActive = Math.Max(0, maxIo - availIo),
            LockContentionCount = Monitor.LockContentionCount,
            IsDraining = slot.IsDraining,
            RecentOutputLines = [.. slot.Worker.RecentOutputLines],
            InjectedGcEnvironment = new Dictionary<string, string>(slot.Worker.InjectedGcEnvironment, StringComparer.OrdinalIgnoreCase),
            TopThreads = threads
        };
    }

    private async ValueTask<ManagedWorkerSlot?> AcquireWorkerSlotAsync(SiteApplicationConfig appConfig, CancellationToken ct)
    {
        if (_config.DesiredState == DesiredResourceState.Stopped || _actualState == RuntimeResourceState.RapidFailTripped)
        {
            return null;
        }

        // Fast lock-free path when pool is Started and has active workers
        if (_actualState == RuntimeResourceState.Started && _activeSlots.Count > 0)
        {
            ManagedWorkerSlot? picked = PickRoundRobinSlot();
            if (picked is not null)
            {
                int threshold = Math.Max(1, _config.ProcessModel.ScaleUpConcurrencyThreshold);
                int maxAllowed = Math.Max(Math.Max(1, _config.ProcessModel.WorkerCount), _config.ProcessModel.MaxWorkers);
                if (Volatile.Read(ref picked.InFlightRequests) >= threshold && _activeSlots.Count < maxAllowed)
                {
                    _ = TryScaleUpSingleWorkerAsync(appConfig);
                }
                return picked;
            }
        }

        // Slow path: cold-start wakeup from Idle or initial start
        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _primaryAppConfig ??= appConfig;
            if (_config.DesiredState == DesiredResourceState.Stopped || _actualState == RuntimeResourceState.RapidFailTripped)
            {
                return null;
            }

            if (_activeSlots.Count == 0)
            {
                await SpawnWorkerGenerationInternalAsync(ct).ConfigureAwait(false);
            }

            return PickRoundRobinSlot();
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private ManagedWorkerSlot? PickRoundRobinSlot()
    {
        int count = _activeSlots.Count;
        if (count == 0)
        {
            return null;
        }

        uint idx = (uint)Interlocked.Increment(ref _roundRobinIndex);
        return _activeSlots[(int)(idx % (uint)count)];
    }

    private async Task TryScaleUpSingleWorkerAsync(SiteApplicationConfig appConfig)
    {
        if (!await _lifecycleLock.WaitAsync(0).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            int maxAllowed = Math.Max(Math.Max(1, _config.ProcessModel.WorkerCount), _config.ProcessModel.MaxWorkers);
            if (_disposed || _config.DesiredState != DesiredResourceState.Started || _activeSlots.Count >= maxAllowed)
            {
                return;
            }

            _primaryAppConfig ??= appConfig;
            int seq = Interlocked.Increment(ref _workerSequence);
            IWorkerInstance extra = await _launcher
                .LaunchWorkerAsync(_config, _primaryAppConfig, $"w{seq}", _timeProvider.GetUtcNow(), CancellationToken.None)
                .ConfigureAwait(false);
            var slot = new ManagedWorkerSlot(extra);
            extra.Exited += OnWorkerExitedUnexpectedly;
            _activeSlots.Add(slot);
        }
        catch
        {
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async ValueTask SpawnWorkerGenerationInternalAsync(CancellationToken ct)
    {
        _actualState = RuntimeResourceState.Starting;
        int targetCount = Math.Max(1, _config.ProcessModel.WorkerCount);
        DateTimeOffset now = _timeProvider.GetUtcNow();

        var launchTasks = new Task<IWorkerInstance>[targetCount];
        for (int i = 0; i < targetCount; i++)
        {
            int seq = Interlocked.Increment(ref _workerSequence);
            string workerId = $"w{seq}";
            launchTasks[i] = _launcher.LaunchWorkerAsync(_config, _primaryAppConfig, workerId, now, ct).AsTask();
        }

        IWorkerInstance[] instances = await Task.WhenAll(launchTasks).ConfigureAwait(false);
        foreach (IWorkerInstance instance in instances)
        {
            var slot = new ManagedWorkerSlot(instance);
            instance.Exited += OnWorkerExitedUnexpectedly;
            _activeSlots.Add(slot);
        }

        Interlocked.Exchange(ref _generationRequestsServed, 0);
        _lastActivityUtc = _timeProvider.GetUtcNow();
        _actualState = RuntimeResourceState.Started;
    }

    private async ValueTask PerformRecycleInternalAsync(CancellationToken ct)
    {
        _actualState = RuntimeResourceState.Recycling;
        var retiring = _activeSlots.ToList();

        if (_config.Recycling.DisallowOverlappingRotation)
        {
            // Stop old workers first, then spawn new generation
            _activeSlots.Clear();
            foreach (ManagedWorkerSlot old in retiring)
            {
                old.IsDraining = true;
                await DisposeSlotSafeAsync(old).ConfigureAwait(false);
            }

            await SpawnWorkerGenerationInternalAsync(ct).ConfigureAwait(false);
        }
        else
        {
            // Overlapped recycling: spawn new workers FIRST in parallel, verify readiness, then swap & drain old workers
            int targetCount = Math.Max(1, _config.ProcessModel.WorkerCount);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            var launchTasks = new Task<IWorkerInstance>[targetCount];
            for (int i = 0; i < targetCount; i++)
            {
                int seq = Interlocked.Increment(ref _workerSequence);
                string workerId = $"w{seq}";
                launchTasks[i] = _launcher.LaunchWorkerAsync(_config, _primaryAppConfig, workerId, now, ct).AsTask();
            }

            IWorkerInstance[] instances = await Task.WhenAll(launchTasks).ConfigureAwait(false);
            var replacementSlots = new List<ManagedWorkerSlot>(targetCount);
            foreach (IWorkerInstance instance in instances)
            {
                var slot = new ManagedWorkerSlot(instance);
                instance.Exited += OnWorkerExitedUnexpectedly;
                replacementSlots.Add(slot);
            }

            _activeSlots.Clear();
            _activeSlots.AddRange(replacementSlots);
            Interlocked.Exchange(ref _generationRequestsServed, 0);

            foreach (ManagedWorkerSlot old in retiring)
            {
                old.IsDraining = true;
                if (Volatile.Read(ref old.InFlightRequests) <= 0)
                {
                    await DisposeSlotSafeAsync(old).ConfigureAwait(false);
                }
                else
                {
                    _drainingSlots.Add(old);
                }
            }
        }

        _lastRecycleUtc = _timeProvider.GetUtcNow();
        _actualState = RuntimeResourceState.Started;
    }

    private void OnWorkerExitedUnexpectedly(IWorkerInstance crashedWorker)
    {
        if (_disposed)
        {
            return;
        }

        _ = HandleWorkerCrashAsync(crashedWorker);
    }

    private async Task HandleWorkerCrashAsync(IWorkerInstance crashedWorker)
    {
        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            ManagedWorkerSlot? slot = _activeSlots.FirstOrDefault(s => ReferenceEquals(s.Worker, crashedWorker));
            if (slot is null || slot.IsDraining)
            {
                return;
            }

            _activeSlots.Remove(slot);
            await DisposeSlotSafeAsync(slot).ConfigureAwait(false);

            DateTimeOffset now = _timeProvider.GetUtcNow();
            _crashTimestamps.Enqueue(now);

            TimeSpan window = TimeSpan.FromMinutes(Math.Max(1, _config.RapidFailProtection.IntervalMinutes));
            while (_crashTimestamps.Count > 0 && now - _crashTimestamps.Peek() > window)
            {
                _crashTimestamps.Dequeue();
            }

            if (_config.RapidFailProtection.Enabled &&
                _crashTimestamps.Count >= _config.RapidFailProtection.MaxCrashes)
            {
                _actualState = RuntimeResourceState.RapidFailTripped;
                _lastError = $"Rapid-Fail Protection tripped: {_crashTimestamps.Count} worker crashes within {_config.RapidFailProtection.IntervalMinutes} minutes.";
                await StopAllWorkersInternalAsync().ConfigureAwait(false);
                return;
            }

            // Auto-heal by spawning a replacement worker if pool is still supposed to be running
            if (_config.DesiredState == DesiredResourceState.Started)
            {
                int seq = Interlocked.Increment(ref _workerSequence);
                IWorkerInstance replacement = await _launcher.LaunchWorkerAsync(
                    _config,
                    _primaryAppConfig,
                    $"w{seq}",
                    now,
                    CancellationToken.None).ConfigureAwait(false);

                var newSlot = new ManagedWorkerSlot(replacement);
                replacement.Exited += OnWorkerExitedUnexpectedly;
                _activeSlots.Add(newSlot);
                _actualState = RuntimeResourceState.Started;
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            _actualState = RuntimeResourceState.Faulted;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async ValueTask ForwardViaNamedPipeIpcAsync(
        HttpContext context,
        ManagedWorkerSlot slot,
        ReadOnlyMemory<char> subPath,
        CancellationToken ct)
    {
        NamedPipeClientStream pipe = await slot.RentPipeConnectionAsync(ct).ConfigureAwait(false);
        KiteIpcRequestHeader reqHeader = _ipcReqPool.Get();
        KiteIpcResponseHeader respHeader = _ipcRespPool.Get();
        bool returnPipe = true;

        try
        {
            reqHeader.Reset();
            reqHeader.Method = context.Request.Method;
            reqHeader.Scheme = context.Request.Scheme;
            reqHeader.Host = context.Request.Host.Value ?? "localhost";
            reqHeader.Path = new string(subPath.Span);
            reqHeader.QueryString = context.Request.QueryString.Value ?? "";
            reqHeader.RemoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
            reqHeader.RemotePort = context.Connection.RemotePort;

            foreach (KeyValuePair<string, StringValues> header in context.Request.Headers)
            {
                reqHeader.Headers.Add(new KeyValuePair<string, string>(header.Key, header.Value.ToString()));
            }

            // Inject RFC 7239 Forwarded header
            reqHeader.Headers.Add(new KeyValuePair<string, string>(
                "Forwarded",
                $"for=\"{reqHeader.RemoteIp}\";proto={reqHeader.Scheme};host=\"{reqHeader.Host}\""));

            Stream? bodyStream = context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding")
                ? context.Request.Body
                : null;
            await KiteIpcProtocol.WriteRequestMessageAsync(pipe, reqHeader, bodyStream, ct).ConfigureAwait(false);

            await KiteIpcProtocol.ReadResponseHeaderAsync(pipe, respHeader, ct).ConfigureAwait(false);
            context.Response.StatusCode = respHeader.StatusCode;

            for (int i = 0; i < respHeader.Headers.Count; i++)
            {
                KeyValuePair<string, string> h = respHeader.Headers[i];
                context.Response.Headers[h.Key] = h.Value;
            }

            await KiteIpcProtocol.ReadBodyFramesToAsync(pipe, context.Response.Body, ct).ConfigureAwait(false);
        }
        catch
        {
            returnPipe = false;
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        finally
        {
            reqHeader.Reset();
            respHeader.Reset();
            _ipcReqPool.Return(reqHeader);
            _ipcRespPool.Return(respHeader);

            if (returnPipe)
            {
                slot.ReturnPipeConnection(pipe);
            }
        }
    }

    private static async ValueTask ForwardViaHttpProxyAsync(
        HttpContext context,
        ManagedWorkerSlot slot,
        SiteApplicationConfig appConfig,
        ReadOnlyMemory<char> subPath,
        CancellationToken ct)
    {
        string rawAddress = !string.IsNullOrWhiteSpace(appConfig.UpstreamUrl)
            ? appConfig.UpstreamUrl
            : slot.Worker.TransportAddress;

        string baseAddress = rawAddress.StartsWith("unix://", StringComparison.OrdinalIgnoreCase)
            ? "http://kite-unix.local"
            : rawAddress;

        await HttpProxyForwarder.ForwardAsync(
            context,
            slot.HttpInvoker,
            baseAddress,
            subPath,
            appConfig.EnableStreamingPassThrough,
            ct).ConfigureAwait(false);
    }

    private void ReconcileSidecarsInternal(ApplicationPoolConfig cfg)
    {
        if (cfg.ProcessModel.Sidecars.Count == 0 && _sidecarProcesses.Count == 0)
        {
            return;
        }

        if (_sidecarProcesses.Count > 0)
        {
            return;
        }

        foreach (PoolSidecarConfig sc in cfg.ProcessModel.Sidecars)
        {
            if (!sc.Enabled || string.IsNullOrWhiteSpace(sc.Executable))
            {
                continue;
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = sc.Executable,
                    Arguments = sc.Arguments ?? string.Empty,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                foreach (KeyValuePair<string, string> kv in sc.EnvironmentVariables)
                {
                    psi.Environment[kv.Key] = kv.Value;
                }
                Process? p = Process.Start(psi);
                if (p is not null)
                {
                    _sidecarProcesses.Add(p);
                }
            }
            catch
            {
            }
        }
    }

    private void StopSidecarsInternal()
    {
        foreach (Process p in _sidecarProcesses)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
            finally
            {
                p.Dispose();
            }
        }
        _sidecarProcesses.Clear();
    }

    private async ValueTask StopAllWorkersInternalAsync()
    {
        foreach (ManagedWorkerSlot slot in _activeSlots)
        {
            slot.IsDraining = true;
            await DisposeSlotSafeAsync(slot).ConfigureAwait(false);
        }
        _activeSlots.Clear();

        foreach (ManagedWorkerSlot slot in _drainingSlots)
        {
            await DisposeSlotSafeAsync(slot).ConfigureAwait(false);
        }
        _drainingSlots.Clear();
    }

    private async ValueTask DisposeSlotSafeAsync(ManagedWorkerSlot slot)
    {
        slot.Worker.Exited -= OnWorkerExitedUnexpectedly;
        _drainingSlots.Remove(slot);
        await slot.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAllWorkersInternalAsync().ConfigureAwait(false);
        StopSidecarsInternal();
        _lifecycleLock.Dispose();
    }

    private sealed class ManagedWorkerSlot : IAsyncDisposable
    {
        private readonly ConcurrentBag<NamedPipeClientStream> _pipePool = [];
        private readonly SocketsHttpHandler _socketsHandler;

        public ManagedWorkerSlot(IWorkerInstance worker)
        {
            Worker = worker;
            string? unixPath = worker.TransportAddress.StartsWith("unix://", StringComparison.OrdinalIgnoreCase)
                ? worker.TransportAddress["unix://".Length..]
                : null;

            _socketsHandler = new SocketsHttpHandler
            {
                UseCookies = false,
                AllowAutoRedirect = false,
                AutomaticDecompression = DecompressionMethods.None,
                PooledConnectionLifetime = TimeSpan.FromMinutes(10),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 1024,
                ConnectCallback = async (context, token) =>
                {
                    string host = context.DnsEndPoint.Host;
                    if (unixPath is not null && string.Equals(host, "kite-unix.local", StringComparison.OrdinalIgnoreCase))
                    {
                        var unixEp = new UnixDomainSocketEndPoint(unixPath);
                        var unixSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                        try
                        {
                            await unixSocket.ConnectAsync(unixEp, token).ConfigureAwait(false);
                            return new NetworkStream(unixSocket, ownsSocket: true);
                        }
                        catch
                        {
                            unixSocket.Dispose();
                            throw;
                        }
                    }

                    if (host.EndsWith(".pipe.local", StringComparison.OrdinalIgnoreCase))
                    {
                        string pipeName = host[..^".pipe.local".Length];
                        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                        await pipe.ConnectAsync(3000, token).ConfigureAwait(false);
                        return pipe;
                    }

                    var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp)
                    {
                        NoDelay = true
                    };
                    try
                    {
                        await socket.ConnectAsync(context.DnsEndPoint, token).ConfigureAwait(false);
                        return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };
            HttpInvoker = new HttpMessageInvoker(_socketsHandler, disposeHandler: true);
        }

        public IWorkerInstance Worker { get; }
        public HttpMessageInvoker HttpInvoker { get; }
        public int InFlightRequests;
        public long RequestsServed;
        public volatile bool IsDraining;
        private TimeSpan _lastCpuTime;
        private long _lastCpuSampleTicks = Stopwatch.GetTimestamp();
        private long _lastThreadSampleTicks;
        private List<WorkerThreadInfo> _cachedThreads = [];

        public long CachedVirtualMemoryBytes { get; private set; }
        public double CachedCpuPercent { get; private set; }

        public bool ShouldRefreshThreads(long nowTicks, out List<WorkerThreadInfo> cached)
        {
            cached = _cachedThreads;
            if (_lastThreadSampleTicks == 0)
            {
                // Skip expensive NtQuerySystemInformation OS thread enumeration on the initial constructor tick
                _lastThreadSampleTicks = nowTicks;
                return false;
            }

            if (Stopwatch.GetElapsedTime(_lastThreadSampleTicks, nowTicks).TotalMilliseconds >= 1500)
            {
                _lastThreadSampleTicks = nowTicks;
                return true;
            }

            return false;
        }

        public void UpdateCachedTelemetry(List<WorkerThreadInfo> fresh, long virtualMem, double cpuPct)
        {
            _cachedThreads = fresh;
            CachedVirtualMemoryBytes = virtualMem;
            CachedCpuPercent = cpuPct;
        }

        public double SampleCpuPercent(TimeSpan totalCpuTime, long nowTicks)
        {
            long prevTicks = Interlocked.Exchange(ref _lastCpuSampleTicks, nowTicks);
            TimeSpan prevCpu = _lastCpuTime;
            _lastCpuTime = totalCpuTime;
            if (prevCpu == TimeSpan.Zero)
            {
                return 0.0;
            }

            double wallMs = Stopwatch.GetElapsedTime(prevTicks, nowTicks).TotalMilliseconds;
            if (wallMs <= 10.0)
            {
                return 0.0;
            }

            double cpuMs = (totalCpuTime - prevCpu).TotalMilliseconds;
            double pct = (cpuMs / (wallMs * Environment.ProcessorCount)) * 100.0;
            return Math.Round(Math.Clamp(pct, 0.0, 100.0), 1);
        }

        public async ValueTask<NamedPipeClientStream> RentPipeConnectionAsync(CancellationToken ct)
        {
            while (_pipePool.TryTake(out NamedPipeClientStream? existing))
            {
                if (existing.IsConnected)
                {
                    return existing;
                }
                await existing.DisposeAsync().ConfigureAwait(false);
            }

            var client = new NamedPipeClientStream(".", Worker.TransportAddress, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(3000, ct).ConfigureAwait(false);
            return client;
        }

        public void ReturnPipeConnection(NamedPipeClientStream pipe)
        {
            if (!IsDraining && pipe.IsConnected)
            {
                _pipePool.Add(pipe);
            }
            else
            {
                pipe.Dispose();
            }
        }

        public async ValueTask DisposeAsync()
        {
            while (_pipePool.TryTake(out NamedPipeClientStream? pipe))
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
            HttpInvoker.Dispose();
            await Worker.DisposeAsync().ConfigureAwait(false);
        }
    }
}
