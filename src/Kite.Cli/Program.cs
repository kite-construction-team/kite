using Kite.Cli;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

int exitCode = await KiteCliRunner.ExecuteAsync(args, Console.Out, Console.Error, cts.Token).ConfigureAwait(false);
return exitCode;
