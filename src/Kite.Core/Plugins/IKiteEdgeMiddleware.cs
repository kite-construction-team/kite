using Microsoft.AspNetCore.Http;

namespace Kite.Core.Plugins;

/// <summary>
/// Contract implemented by hot-reloadable C# edge middleware assemblies loaded from <c>plugins/</c>.
/// </summary>
public interface IKiteEdgeMiddleware
{
    /// <summary>
    /// Executes edge plugin logic before request forwarding/static handling.
    /// Returning <see langword="false"/> short-circuits further pipeline execution.
    /// </summary>
    ValueTask<bool> InvokeAsync(
        HttpContext context,
        IReadOnlyDictionary<string, string> settings,
        CancellationToken cancellationToken);
}
