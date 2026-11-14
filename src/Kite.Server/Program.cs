using Kite.Core.Storage;
using Kite.Server.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
