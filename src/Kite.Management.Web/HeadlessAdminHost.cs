using System.Net;
using System.Security.Cryptography;
using System.Text;
using Kite.Core.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Kite.Management.Web;

public sealed record HeadlessAdminOptions(
    int Port = 0,
    string? UnixSocketPath = null,
    string? BearerToken = null,
    string? BasicAuthCredentials = null);

/// <summary>
/// Opt-in headless web host for <see cref="KiteInMemoryHost"/> designed for Linux/Docker
/// deployments where WinForms/WebView2 is unavailable. Binds only to loopback or a Unix domain socket
/// and enforces Bearer or Basic authentication when configured.
/// </summary>
public sealed class HeadlessAdminHost : IAsyncDisposable
{
    private readonly KiteInMemoryHost _inMemoryHost;
    private readonly HeadlessAdminOptions _options;
    private WebApplication? _app;

    public HeadlessAdminHost(KiteHomeLayout layout, HeadlessAdminOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        _inMemoryHost = new KiteInMemoryHost(layout);
        _options = options ?? new HeadlessAdminOptions();
    }

    public int ListeningPort { get; private set; }

    public async Task StartAsync(CancellationToken ct = default)
    {
        await _inMemoryHost.StartAsync(seedDefaults: true, ct).ConfigureAwait(false);

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            if (!string.IsNullOrWhiteSpace(_options.UnixSocketPath))
            {
                if (File.Exists(_options.UnixSocketPath))
                {
                    File.Delete(_options.UnixSocketPath);
                }
                kestrel.ListenUnixSocket(_options.UnixSocketPath);
            }
            else
            {
                int port = _options.Port > 0 ? _options.Port : 0;
                kestrel.Listen(IPAddress.Loopback, port);
            }
        });

        WebApplication app = builder.Build();
        app.Run(HandleRequestAsync);
        await app.StartAsync(ct).ConfigureAwait(false);
        _app = app;

        string? addr = app.Urls.FirstOrDefault(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
        if (addr is not null && Uri.TryCreate(addr, UriKind.Absolute, out Uri? uri))
        {
            ListeningPort = uri.Port;
        }
    }

    private async Task HandleRequestAsync(HttpContext context)
    {
        if (!IsAuthorized(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers["WWW-Authenticate"] = !string.IsNullOrWhiteSpace(_options.BasicAuthCredentials)
                ? "Basic realm=\"Kite Headless Admin\""
                : "Bearer realm=\"Kite Headless Admin\"";
            await context.Response.WriteAsync("Unauthorized", context.RequestAborted).ConfigureAwait(false);
            return;
        }

        string pathAndQuery = (context.Request.Path.Value ?? "/") + context.Request.QueryString.Value;
        using InMemoryUiResponse uiResp = await _inMemoryHost.SendAsync(
            context.Request.Method,
            pathAndQuery,
            context.Request.ContentLength > 0 ? context.Request.Body : null,
            context.Request.ContentType,
            context.RequestAborted).ConfigureAwait(false);

        context.Response.StatusCode = uiResp.StatusCode;
        context.Response.ContentType = uiResp.ContentType;
        foreach (var kvp in uiResp.Headers)
        {
            context.Response.Headers[kvp.Key] = kvp.Value;
        }

        uiResp.BodyStream.Position = 0;
        context.Response.ContentLength = uiResp.BodyStream.Length;
        await uiResp.BodyStream.CopySegmentsToAsync(context.Response.Body, context.RequestAborted).ConfigureAwait(false);
    }

    private bool IsAuthorized(HttpRequest request)
    {
        bool hasBearer = !string.IsNullOrWhiteSpace(_options.BearerToken);
        bool hasBasic = !string.IsNullOrWhiteSpace(_options.BasicAuthCredentials);
        if (!hasBearer && !hasBasic)
        {
            return true;
        }

        string authHeader = request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authHeader))
        {
            return false;
        }

        if (hasBearer && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            string presented = authHeader["Bearer ".Length..].Trim();
            return FixedTimeEqualsUtf8(presented, _options.BearerToken!);
        }

        if (hasBasic && authHeader.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            string encoded = authHeader["Basic ".Length..].Trim();
            try
            {
                string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                return FixedTimeEqualsUtf8(decoded, _options.BasicAuthCredentials!);
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    private static bool FixedTimeEqualsUtf8(string a, string b)
    {
        byte[] ab = Encoding.UTF8.GetBytes(a);
        byte[] bb = Encoding.UTF8.GetBytes(b);
        return ab.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ab, bb);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            try { await _app.StopAsync().ConfigureAwait(false); } catch { }
            await _app.DisposeAsync().ConfigureAwait(false);
        }

        await _inMemoryHost.DisposeAsync().ConfigureAwait(false);
    }
}
