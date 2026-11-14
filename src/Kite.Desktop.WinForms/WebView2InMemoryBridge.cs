using System.Buffers;
using System.Text;
using Kite.Core.Memory;
using Kite.Management.Web;
using Microsoft.Web.WebView2.Core;

namespace Kite.Desktop.WinForms;

/// <summary>
/// Bridges <see cref="CoreWebView2.WebResourceRequested"/> on RFC 6761 loopback origin <c>http://kite.localhost</c>
/// directly to <see cref="KiteInMemoryHost"/> (ASP.NET Core <c>TestServer</c>) in memory.
/// Uses <c>.localhost</c> (instead of <c>.local</c> mDNS) so Chromium resolves the host in 0ms in-memory
/// without broadcasting UDP 5353 mDNS packets or opens any TCP ports on the host machine.
/// </summary>
public sealed class WebView2InMemoryBridge
{
    public const string VirtualOrigin = "http://kite.localhost";
    public const string LegacyVirtualOrigin = "https://kite.local";
    private static readonly byte[] CachedCssBytes = Encoding.UTF8.GetBytes(KiteEmbeddedStyles.TailwindCss);
    private readonly KiteInMemoryHost _inMemoryHost;

    public WebView2InMemoryBridge(KiteInMemoryHost inMemoryHost)
    {
        ArgumentNullException.ThrowIfNull(inMemoryHost);
        _inMemoryHost = inMemoryHost;
    }

    public void Attach(CoreWebView2 coreWebView2)
    {
        ArgumentNullException.ThrowIfNull(coreWebView2);
        coreWebView2.AddWebResourceRequestedFilter($"{VirtualOrigin}/*", CoreWebView2WebResourceContext.All);
        coreWebView2.AddWebResourceRequestedFilter($"{LegacyVirtualOrigin}/*", CoreWebView2WebResourceContext.All);
        coreWebView2.WebResourceRequested += OnWebResourceRequested;
    }

    private async void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (sender is not CoreWebView2 coreWebView2)
        {
            return;
        }

        var uri = new Uri(e.Request.Uri);

        // Fast-path static assets directly from pre-encoded memory with immutable caching
        if (string.Equals(uri.AbsolutePath, "/css/kite-tailwind.css", StringComparison.OrdinalIgnoreCase))
        {
            e.Response = coreWebView2.Environment.CreateWebResourceResponse(
                new MemoryStream(CachedCssBytes, writable: false),
                200,
                "OK",
                "Content-Type: text/css; charset=utf-8\r\nCache-Control: public, max-age=31536000, immutable");
            return;
        }

        if (string.Equals(uri.AbsolutePath, "/favicon.ico", StringComparison.OrdinalIgnoreCase))
        {
            e.Response = coreWebView2.Environment.CreateWebResourceResponse(
                Stream.Null,
                204,
                "No Content",
                "Cache-Control: public, max-age=31536000, immutable");
            return;
        }

        CoreWebView2Deferral deferral = e.GetDeferral();
        try
        {
            string method = e.Request.Method ?? "GET";
            string pathAndQuery = uri.PathAndQuery;
            byte[]? bodyBytes = null;
            string? contentType = null;

            if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) && e.Request.Content is Stream contentStream)
            {
                contentType = "application/x-www-form-urlencoded";
                using var seg = new SegmentedBufferStream();
                byte[] rent = ArrayPool<byte>.Shared.Rent(4096);
                try
                {
                    int read;
                    while ((read = contentStream.Read(rent, 0, rent.Length)) > 0)
                    {
                        seg.Write(rent, 0, read);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(rent);
                }

                bodyBytes = new byte[seg.Length];
                seg.Position = 0;
                _ = seg.Read(bodyBytes, 0, bodyBytes.Length);
            }

            (int statusCode, string reasonPhrase, string respContentType, MemoryStream responseStream) = await Task.Run(async () =>
            {
                using InMemoryHttpResponse response = await _inMemoryHost.DispatchAsync(new InMemoryHttpRequest
                {
                    Method = method,
                    PathAndQuery = pathAndQuery,
                    Body = bodyBytes,
                    ContentType = contentType
                }).ConfigureAwait(false);

                var ms = new MemoryStream((int)response.BodyPayload.Length);
                response.BodyPayload.Position = 0;
                await response.BodyPayload.CopyToAsync(ms).ConfigureAwait(false);
                ms.Position = 0;
                return (response.StatusCode, response.ReasonPhrase, response.ContentType, ms);
            }).ConfigureAwait(true);

            string headerLines = $"Content-Type: {respContentType}\r\nCache-Control: no-store\r\nX-Frame-Options: DENY";
            e.Response = coreWebView2.Environment.CreateWebResourceResponse(
                responseStream,
                statusCode,
                reasonPhrase,
                headerLines);
        }
        catch (Exception ex)
        {
            var errStream = new MemoryStream(Encoding.UTF8.GetBytes($"<html><body><h1>Desktop UI Error</h1><pre>{System.Net.WebUtility.HtmlEncode(ex.ToString())}</pre></body></html>"));
            e.Response = coreWebView2.Environment.CreateWebResourceResponse(
                errStream,
                500,
                "Internal Error",
                "Content-Type: text/html; charset=utf-8");
        }
        finally
        {
            deferral.Complete();
        }
    }
}
