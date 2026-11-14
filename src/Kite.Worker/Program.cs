using Kite.Worker;

var options = new KiteWorkerOptions();

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--pipe" when i + 1 < args.Length:
            options.PipeName = args[++i];
            break;
        case "--pool-id" when i + 1 < args.Length:
            options.PoolId = args[++i];
            break;
        case "--worker-id" when i + 1 < args.Length:
            options.WorkerId = args[++i];
            break;
        case "--app-dir" when i + 1 < args.Length:
            options.PhysicalPath = args[++i];
            break;
        case "--entry-dll" when i + 1 < args.Length:
            options.EntryPointDll = args[++i];
            break;
    }
}

await using var engine = new KiteWorkerHostEngine(options);
engine.Start();

using var shutdownTcs = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    shutdownTcs.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => shutdownTcs.Cancel();

try
{
    await Task.Delay(Timeout.Infinite, shutdownTcs.Token);
}
catch (OperationCanceledException)
{
}
