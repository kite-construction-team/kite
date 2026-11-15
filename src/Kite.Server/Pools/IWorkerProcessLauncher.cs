using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using Kite.Core.Ipc;
using Kite.Core.Models;
using Kite.Worker;

namespace Kite.Server.Pools;

public interface IWorkerInstance : IAsyncDisposable
{
    string WorkerId { get; }
    int ProcessId { get; }
    string TransportAddress { get; }
    WorkloadHostingMode HostingMode { get; }
    DateTimeOffset StartedAtUtc { get; }
    bool HasExited { get; }
    IReadOnlyDictionary<string, string> InjectedGcEnvironment { get; }
    IReadOnlyList<string> RecentOutputLines => [];
    long GetWorkingSetBytes();
    event Action<IWorkerInstance>? Exited;

    ValueTask<bool> ProbeReadinessAsync(CancellationToken ct);
}

public interface IWorkerProcessLauncher
{
    ValueTask<IWorkerInstance> LaunchWorkerAsync(
        ApplicationPoolConfig pool,
        SiteApplicationConfig? appConfig,
        string workerId,
        DateTimeOffset startedAtUtc,
        CancellationToken ct);
}

/// <summary>
/// Production worker launcher that spawns either an in-host/out-of-process <c>Kite.Worker</c> IPC engine
/// or an external executable listening on a dynamically allocated loopback port.
/// </summary>
public sealed class RealWorkerProcessLauncher : IWorkerProcessLauncher
{
    public async ValueTask<IWorkerInstance> LaunchWorkerAsync(
        ApplicationPoolConfig pool,
        SiteApplicationConfig? appConfig,
        string workerId,
        DateTimeOffset startedAtUtc,
        CancellationToken ct)
    {
        Dictionary<string, string> gcEnv = ApplicationPoolSupervisor.BuildGcEnvironmentVariables(pool);
        WorkloadHostingMode mode = appConfig?.HostingMode ?? WorkloadHostingMode.KiteWorkerIpc;

        if (mode == WorkloadHostingMode.StaticOnly)
        {
            return new ExternalUpstreamWorkerInstance(
                workerId,
                $"static-pipeline://{pool.Id}/{workerId}",
                WorkloadHostingMode.StaticOnly,
                startedAtUtc,
                gcEnv);
        }

        if (mode == WorkloadHostingMode.ExternalReverseProxy ||
            (mode == WorkloadHostingMode.OutOfProcessProxy && !string.IsNullOrWhiteSpace(appConfig?.UpstreamUrl)))
        {
            string upstream = appConfig?.UpstreamUrl ?? "http://127.0.0.1:80";
            return new ExternalUpstreamWorkerInstance(workerId, upstream, mode, startedAtUtc, gcEnv);
        }

        if (mode == WorkloadHostingMode.OutOfProcessProxy && !string.IsNullOrWhiteSpace(appConfig?.Executable))
        {
            string rawArgs = appConfig.Arguments ?? string.Empty;
            bool useUnixSocket = pool.ProcessModel.UseUnixDomainSocket && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17063) || (pool.ProcessModel.UseUnixDomainSocket && !OperatingSystem.IsWindows());
            string? unixSocketPath = useUnixSocket &&
                                     (rawArgs.Contains("{socket}", StringComparison.OrdinalIgnoreCase) ||
                                      rawArgs.Contains("--urls {url}", StringComparison.OrdinalIgnoreCase))
                ? Path.Combine(Path.GetTempPath(), $"kite-{pool.Id}-{workerId}-{Guid.NewGuid():N}.sock")
                : null;

            int port = AllocateFreeTcpPort();
            string transportUrl = unixSocketPath is not null
                ? $"unix://{unixSocketPath}"
                : $"http://127.0.0.1:{port}";
            string portStr = port.ToString();

            string effectiveArgs;
            if (unixSocketPath is not null)
            {
                string kestrelUnixUrl = $"http://unix:{unixSocketPath}";
                effectiveArgs = rawArgs
                    .Replace("{socket}", unixSocketPath, StringComparison.OrdinalIgnoreCase)
                    .Replace("{port}", portStr, StringComparison.OrdinalIgnoreCase)
                    .Replace("{url}", rawArgs.Contains("--urls", StringComparison.OrdinalIgnoreCase) ? kestrelUnixUrl : transportUrl, StringComparison.OrdinalIgnoreCase)
                    .Trim();
            }
            else if (rawArgs.Contains("{port}", StringComparison.OrdinalIgnoreCase) ||
                     rawArgs.Contains("{url}", StringComparison.OrdinalIgnoreCase))
            {
                effectiveArgs = rawArgs
                    .Replace("{port}", portStr, StringComparison.OrdinalIgnoreCase)
                    .Replace("{url}", transportUrl, StringComparison.OrdinalIgnoreCase)
                    .Trim();
            }
            else
            {
                effectiveArgs = $"{rawArgs} --urls {transportUrl}".Trim();
            }

            var psi = new ProcessStartInfo
            {
                FileName = appConfig.Executable,
                Arguments = effectiveArgs,
                WorkingDirectory = !string.IsNullOrWhiteSpace(appConfig.PhysicalPath) && Directory.Exists(appConfig.PhysicalPath)
                    ? appConfig.PhysicalPath
                    : Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            foreach (var kvp in gcEnv)
            {
                psi.Environment[kvp.Key] = kvp.Value;
            }
            foreach (var kvp in pool.ProcessModel.EnvironmentVariables)
            {
                psi.Environment[kvp.Key] = kvp.Value;
            }
            psi.Environment["HOST"] = "127.0.0.1";
            psi.Environment["PORT"] = portStr;
            psi.Environment["ASPNETCORE_URLS"] = transportUrl;
            psi.Environment["KITE_UPSTREAM_URL"] = transportUrl;
            if (unixSocketPath is not null)
            {
                psi.Environment["KITE_UNIX_SOCKET"] = unixSocketPath;
            }

            Process proc = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start process '{appConfig.Executable}'.");
            var instance = new ExternalProcessWorkerInstance(workerId, proc, transportUrl, unixSocketPath, mode, startedAtUtc, gcEnv);
            await instance.ProbeReadinessAsync(ct).ConfigureAwait(false);
            return instance;
        }
        else
        {
            string pipeName = $"kite-ipc-{pool.Id}-{workerId}-{Guid.NewGuid():N}";
            var workerOptions = new KiteWorkerOptions
            {
                PipeName = pipeName,
                PoolId = pool.Id,
                WorkerId = workerId,
                PhysicalPath = appConfig?.PhysicalPath,
                EntryPointDll = appConfig?.EntryPoint
            };

            var engine = new KiteWorkerHostEngine(workerOptions);
            engine.Start();
            var instance = new ManagedIpcWorkerInstance(workerId, pipeName, engine, startedAtUtc, gcEnv);
            await instance.ProbeReadinessAsync(ct).ConfigureAwait(false);
            return instance;
        }
    }

    private static int AllocateFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private sealed class ExternalUpstreamWorkerInstance(
        string workerId,
        string upstreamUrl,
        WorkloadHostingMode mode,
        DateTimeOffset startedAtUtc,
        IReadOnlyDictionary<string, string> gcEnv) : IWorkerInstance
    {
        public string WorkerId => workerId;
        public int ProcessId => Environment.ProcessId;
        public string TransportAddress => upstreamUrl;
        public WorkloadHostingMode HostingMode => mode;
        public DateTimeOffset StartedAtUtc => startedAtUtc;
        public bool HasExited => false;
        public IReadOnlyDictionary<string, string> InjectedGcEnvironment => gcEnv;
        public event Action<IWorkerInstance>? Exited { add { } remove { } }
        public long GetWorkingSetBytes() => 0;
        public ValueTask<bool> ProbeReadinessAsync(CancellationToken ct) => ValueTask.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ManagedIpcWorkerInstance(
        string workerId,
        string pipeName,
        KiteWorkerHostEngine engine,
        DateTimeOffset startedAtUtc,
        IReadOnlyDictionary<string, string> gcEnv) : IWorkerInstance
    {
        private bool _disposed;

        public string WorkerId => workerId;
        public int ProcessId => Environment.ProcessId;
        public string TransportAddress => pipeName;
        public WorkloadHostingMode HostingMode => WorkloadHostingMode.KiteWorkerIpc;
        public DateTimeOffset StartedAtUtc => startedAtUtc;
        public bool HasExited => _disposed;
        public IReadOnlyDictionary<string, string> InjectedGcEnvironment => gcEnv;
        public event Action<IWorkerInstance>? Exited;

        public long GetWorkingSetBytes() => GC.GetTotalMemory(forceFullCollection: false);

        public async ValueTask<bool> ProbeReadinessAsync(CancellationToken ct)
        {
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(2000, ct).ConfigureAwait(false);
            await KiteIpcProtocol.WriteMessageTypeAsync(client, KiteIpcMessageType.Ping, ct).ConfigureAwait(false);
            await client.FlushAsync(ct).ConfigureAwait(false);
            KiteIpcMessageType? reply = await KiteIpcProtocol.ReadMessageTypeAsync(client, ct).ConfigureAwait(false);
            return reply == KiteIpcMessageType.Pong;
        }

        public void SimulateCrash()
        {
            _disposed = true;
            Exited?.Invoke(this);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                await engine.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class ExternalProcessWorkerInstance : IWorkerInstance
    {
        private const int MaxLogLines = 120;
        private readonly Process _process;
        private readonly string? _unixSocketPath;
        private readonly ConcurrentQueue<string> _outputRing = new();

        public ExternalProcessWorkerInstance(
            string workerId,
            Process process,
            string transportUrl,
            string? unixSocketPath,
            WorkloadHostingMode mode,
            DateTimeOffset startedAtUtc,
            IReadOnlyDictionary<string, string> gcEnv)
        {
            WorkerId = workerId;
            _process = process;
            _unixSocketPath = unixSocketPath;
            TransportAddress = transportUrl;
            HostingMode = mode;
            StartedAtUtc = startedAtUtc;
            InjectedGcEnvironment = gcEnv;

            _process.EnableRaisingEvents = true;
            _process.OutputDataReceived += (_, e) => AppendLine("OUT", e.Data);
            _process.ErrorDataReceived += (_, e) => AppendLine("ERR", e.Data);
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _process.Exited += (_, _) => Exited?.Invoke(this);
        }

        private void AppendLine(string stream, string? line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return;
            }

            _outputRing.Enqueue($"[{DateTime.UtcNow:HH:mm:ss} {stream}] {line}");
            while (_outputRing.Count > MaxLogLines)
            {
                _outputRing.TryDequeue(out _);
            }
        }

        public string WorkerId { get; }
        public int ProcessId => _process.Id;
        public string TransportAddress { get; }
        public WorkloadHostingMode HostingMode { get; }
        public DateTimeOffset StartedAtUtc { get; }
        public bool HasExited => _process.HasExited;
        public IReadOnlyDictionary<string, string> InjectedGcEnvironment { get; }
        public IReadOnlyList<string> RecentOutputLines => _outputRing.ToArray();
        public event Action<IWorkerInstance>? Exited;

        public long GetWorkingSetBytes()
        {
            try
            {
                return _process.HasExited ? 0 : _process.WorkingSet64;
            }
            catch
            {
                return 0;
            }
        }

        public async ValueTask<bool> ProbeReadinessAsync(CancellationToken ct)
        {
            if (_unixSocketPath is not null)
            {
                for (int i = 0; i < 40; i++)
                {
                    if (_process.HasExited)
                    {
                        return false;
                    }
                    if (File.Exists(_unixSocketPath))
                    {
                        return true;
                    }
                    await Task.Delay(75, ct).ConfigureAwait(false);
                }
                return false;
            }

            using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(500) };
            for (int i = 0; i < 30; i++)
            {
                if (_process.HasExited)
                {
                    return false;
                }

                try
                {
                    using var response = await client.GetAsync(TransportAddress, ct).ConfigureAwait(false);
                    return true;
                }
                catch
                {
                    await Task.Delay(100, ct).ConfigureAwait(false);
                }
            }
            return false;
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
            finally
            {
                _process.Dispose();
                if (_unixSocketPath is not null)
                {
                    try
                    {
                        File.Delete(_unixSocketPath);
                    }
                    catch
                    {
                    }
                }
            }
            return ValueTask.CompletedTask;
        }
    }
}
