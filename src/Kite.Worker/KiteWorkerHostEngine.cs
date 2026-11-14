using System.IO.Pipes;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using Kite.Core.Ipc;
using Kite.Core.Memory;
using Microsoft.Extensions.ObjectPool;

namespace Kite.Worker;

public sealed class KiteWorkerOptions
{
    public string PipeName { get; set; } = "kite-worker-default";
    public string PoolId { get; set; } = "default-pool";
    public string WorkerId { get; set; } = "worker-0";
    public string? PhysicalPath { get; set; }
    public string? EntryPointDll { get; set; }
    public int MaxConcurrentPipes { get; set; } = Math.Max(8, Environment.ProcessorCount * 2);
}

/// <summary>
/// High-speed Named Pipe IPC host engine for <c>Kite.Worker</c>.
/// Supports persistent connection reuse, pooled header/body buffers, and isolated <see cref="AssemblyLoadContext"/> loading.
/// </summary>
public sealed class KiteWorkerHostEngine : IAsyncDisposable
{
    private readonly KiteWorkerOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _acceptLoops = [];
    private readonly ObjectPool<KiteIpcRequestHeader> _requestHeaderPool;
    private readonly ObjectPool<KiteIpcResponseHeader> _responseHeaderPool;
    private readonly string _workerPidString = Environment.ProcessId.ToString( System.Globalization.CultureInfo.InvariantCulture);
    private Func<KiteIpcRequestHeader, Stream, KiteIpcResponseHeader, Stream, CancellationToken, ValueTask>? _customHandler;
    private AssemblyLoadContext? _appLoadContext;
    private MethodInfo? _entryDiagnosticMethod;

    public KiteWorkerHostEngine(
        KiteWorkerOptions options,
        Func<KiteIpcRequestHeader, Stream, KiteIpcResponseHeader, Stream, CancellationToken, ValueTask>? customHandler = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _customHandler = customHandler;

        var provider = new DefaultObjectPoolProvider { MaximumRetained = 128 };
        _requestHeaderPool = provider.Create(new DefaultPooledObjectPolicy<KiteIpcRequestHeader>());
        _responseHeaderPool = provider.Create(new DefaultPooledObjectPolicy<KiteIpcResponseHeader>());
    }

    public string PipeName => _options.PipeName;

    public void Start()
    {
        TryLoadTargetAssembly();

        for (int i = 0; i < _options.MaxConcurrentPipes; i++)
        {
            _acceptLoops.Add(Task.Run(() => RunPipeListenerLoopAsync(_cts.Token)));
        }
    }

    private void TryLoadTargetAssembly()
    {
        if (string.IsNullOrWhiteSpace(_options.EntryPointDll))
        {
            return;
        }

        string candidatePath = _options.EntryPointDll;
        if (!Path.IsPathRooted(candidatePath) && !string.IsNullOrWhiteSpace(_options.PhysicalPath))
        {
            candidatePath = Path.Combine(_options.PhysicalPath, candidatePath);
        }

        if (!File.Exists(candidatePath))
        {
            return;
        }

        _appLoadContext = new AssemblyLoadContext($"KiteApp_{_options.PoolId}_{_options.WorkerId}", isCollectible: true);
        Assembly loaded = _appLoadContext.LoadFromAssemblyPath(Path.GetFullPath(candidatePath));
        Type? handlerType = loaded.GetType("KiteHostedApp.EntryPoint", throwOnError: false, ignoreCase: true);
        _entryDiagnosticMethod = handlerType?.GetMethod("HandleRequest", BindingFlags.Public | BindingFlags.Static);
    }

    private async Task RunPipeListenerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? serverPipe = null;
            try
            {
                serverPipe = new NamedPipeServerStream(
                    _options.PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await serverPipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

                // Handle persistent connection in background so this listener slot can accept or process
                await ProcessPersistentConnectionAsync(serverPipe, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Transient pipe error; continue listening while not cancelled
                await Task.Delay(10, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                if (serverPipe is not null)
                {
                    await serverPipe.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
    }

    private async Task ProcessPersistentConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        while (pipe.IsConnected && !ct.IsCancellationRequested)
        {
            KiteIpcMessageType? msgType;
            try
            {
                msgType = await KiteIpcProtocol.ReadMessageTypeAsync(pipe, ct).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                break;
            }
            catch (IOException)
            {
                break;
            }

            if (msgType is null)
            {
                break;
            }

            if (msgType == KiteIpcMessageType.Ping)
            {
                await KiteIpcProtocol.WriteMessageTypeAsync(pipe, KiteIpcMessageType.Pong, ct).ConfigureAwait(false);
                await pipe.FlushAsync(ct).ConfigureAwait(false);
                continue;
            }

            if (msgType == KiteIpcMessageType.HttpRequest)
            {
                KiteIpcRequestHeader reqHeader = _requestHeaderPool.Get();
                KiteIpcResponseHeader respHeader = _responseHeaderPool.Get();
                SegmentedBufferStream reqBody = KiteObjectPools.BufferStreamPool.Get();
                SegmentedBufferStream respBody = KiteObjectPools.BufferStreamPool.Get();

                try
                {
                    await KiteIpcProtocol.ReadRequestHeaderAsync(pipe, reqHeader, ct).ConfigureAwait(false);
                    await KiteIpcProtocol.ReadBodyFramesToAsync(pipe, reqBody, ct).ConfigureAwait(false);
                    reqBody.Position = 0;

                    respHeader.Reset();
                    await DispatchApplicationRequestAsync(reqHeader, reqBody, respHeader, respBody, ct).ConfigureAwait(false);

                    respBody.Position = 0;
                    await KiteIpcProtocol.WriteResponseMessageAsync(pipe, respHeader, respBody, ct).ConfigureAwait(false);
                }
                finally
                {
                    reqHeader.Reset();
                    respHeader.Reset();
                    _requestHeaderPool.Return(reqHeader);
                    _responseHeaderPool.Return(respHeader);
                    KiteObjectPools.BufferStreamPool.Return(reqBody);
                    KiteObjectPools.BufferStreamPool.Return(respBody);
                }
            }
        }
    }

    private async ValueTask DispatchApplicationRequestAsync(
        KiteIpcRequestHeader req,
        Stream reqBody,
        KiteIpcResponseHeader resp,
        Stream respBody,
        CancellationToken ct)
    {
        if (_customHandler is not null)
        {
            await _customHandler(req, reqBody, resp, respBody, ct).ConfigureAwait(false);
            return;
        }

        if (_entryDiagnosticMethod is not null)
        {
            object? result = _entryDiagnosticMethod.Invoke(null, [req.Path, req.Method]);
            if (result is string textResult)
            {
                resp.StatusCode = 200;
                resp.Headers.Add(new KeyValuePair<string, string>("Content-Type", "application/json; charset=utf-8"));
                resp.Headers.Add(new KeyValuePair<string, string>("X-Kite-Worker-Id", _options.WorkerId));
                byte[] utf8 = Encoding.UTF8.GetBytes(textResult);
                await respBody.WriteAsync(utf8, ct).ConfigureAwait(false);
                return;
            }
        }

        // Default built-in high-performance worker endpoints
        resp.StatusCode = 200;
        resp.Headers.Add(new KeyValuePair<string, string>("Content-Type", "application/json; charset=utf-8"));
        resp.Headers.Add(new KeyValuePair<string, string>("X-Kite-Hosting-Mode", "KiteWorkerIpc"));
        resp.Headers.Add(new KeyValuePair<string, string>("X-Kite-Pool-Id", _options.PoolId));
        resp.Headers.Add(new KeyValuePair<string, string>("X-Kite-Worker-Id", _options.WorkerId));
        resp.Headers.Add(new KeyValuePair<string, string>("X-Kite-Worker-Pid", _workerPidString));

        Span<byte> utf8Buf = stackalloc byte[512];
        if (System.Text.Unicode.Utf8.TryWrite(
            utf8Buf,
            $$"""{"status":"ok","mode":"KiteWorkerIpc","poolId":"{{_options.PoolId}}","workerId":"{{_options.WorkerId}}","pid":{{_workerPidString}},"method":"{{req.Method}}","path":"{{req.Path}}","query":"{{req.QueryString}}"}""",
            out int bytesWritten))
        {
            respBody.Write(utf8Buf[..bytesWritten]);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(_acceptLoops).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch
        {
            // Ignore shutdown timeouts
        }

        _appLoadContext?.Unload();
        _cts.Dispose();
    }
}
