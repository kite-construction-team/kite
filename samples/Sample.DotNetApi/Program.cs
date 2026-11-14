using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/{**path}", (HttpContext ctx) =>
{
    ctx.Response.Headers["X-Kite-Hosting-Mode"] = "OutOfProcessProxy";
    ctx.Response.Headers["X-Worker-Pid"] = Environment.ProcessId.ToString();
    return Results.Content(
        KiteHostedApp.EntryPoint.HandleRequest(ctx.Request.Path.Value ?? "/", ctx.Request.Method),
        "application/json; charset=utf-8");
});

app.MapPost("/{**path}", (HttpContext ctx) =>
{
    ctx.Response.Headers["X-Kite-Hosting-Mode"] = "OutOfProcessProxy";
    ctx.Response.Headers["X-Worker-Pid"] = Environment.ProcessId.ToString();
    return Results.Content(
        KiteHostedApp.EntryPoint.HandleRequest(ctx.Request.Path.Value ?? "/", ctx.Request.Method),
        "application/json; charset=utf-8");
});

await app.RunAsync();

namespace KiteHostedApp
{
    public static class EntryPoint
    {
        public static string HandleRequest(string path, string method)
        {
            return $$"""{"service":"Sample.DotNetApi","method":"{{method}}","path":"{{path}}","pid":{{Environment.ProcessId}}}""";
        }
    }
}
