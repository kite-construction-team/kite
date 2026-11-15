using System.Reflection;
using System.Runtime.InteropServices;
using Kite.Core.Storage;
using Kite.Server.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

if (args.Length > 0 && args[0] is "--version" or "-v" or "version")
{
    string info = typeof(KiteServerEngine).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";
    int plusIdx = info.IndexOf('+');
    Console.WriteLine($"kite-server {(plusIdx > 0 ? info[..plusIdx] : info)} ({RuntimeInformation.RuntimeIdentifier})");
    return;
}

if (args.Length > 0 && args[0] is "--help" or "-h" or "help")
{
    Console.WriteLine("Kite.Server (kite-server) - Kite Edge Server Background Host (.NET 10)");
    Console.WriteLine("Usage: kite-server [--home <path>] [--version] [--help]");
    return;
}

KiteHomeLayout layout = KiteHomeLayout.ResolveFromArgsAndEnv(args);

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(layout);
builder.Services.AddSingleton<KiteServerEngine>();
builder.Services.AddHostedService<KiteBackgroundHostedService>();

using IHost host = builder.Build();
await host.RunAsync();

public sealed class KiteBackgroundHostedService(KiteServerEngine engine) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken) =>
        await engine.StartAsync(seedDefaultsIfEmpty: true, cancellationToken);

    public async Task StopAsync(CancellationToken cancellationToken) =>
        await engine.DisposeAsync();
}
