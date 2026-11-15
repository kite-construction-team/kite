using Kite.Cli;

using var cts = new CancellationTokenSource();
int disposed = 0;

void OnCancelKeyPress(object? _, ConsoleCancelEventArgs e)
{
    e.Cancel = true;
    if (Volatile.Read(ref disposed) == 0)
    {
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }
}

void OnProcessExit(object? _, EventArgs __)
{
    if (Volatile.Read(ref disposed) == 0)
    {
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }
}

Console.CancelKeyPress += OnCancelKeyPress;
AppDomain.CurrentDomain.ProcessExit += OnProcessExit;

try
{
    return await KiteCliRunner.ExecuteAsync(args, Console.Out, Console.Error, cts.Token).ConfigureAwait(false);
}
finally
{
    Interlocked.Exchange(ref disposed, 1);
    Console.CancelKeyPress -= OnCancelKeyPress;
    AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
}
