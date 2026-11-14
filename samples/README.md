# Kite Sample Workloads

These sample applications demonstrate Kite's polyglot hosting modes:

| Sample | Runtime | Hosting Mode | Highlights |
| --- | --- | --- | --- |
| [Sample.StaticSite](./Sample.StaticSite) | Static HTML | `StaticOnly` | Zero-allocation in-memory cached static site |
| [Sample.DotNetApi](./Sample.DotNetApi) | .NET 10 / ASP.NET Core | `KiteWorkerIpc` / `OutOfProcessProxy` | Native named-pipe IPC or standalone Kestrel API |
| [Sample.NodeApi](./Sample.NodeApi) | Node.js (>=18) | `OutOfProcessProxy` | Zero-dependency `node:http` API, telemetry & SSE stream |
| [Sample.PythonApi](./Sample.PythonApi) | Python 3 (>=3.8) | `OutOfProcessProxy` | Zero-dependency `ThreadingHTTPServer` API, echo & SSE stream (`{port}` placeholder supported for Uvicorn/Gunicorn) |
