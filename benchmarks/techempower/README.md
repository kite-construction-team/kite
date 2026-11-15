# Kite TechEmpower Framework Benchmarks

This directory contains the [TechEmpower Framework Benchmarks (TFB)](https://github.com/TechEmpower/FrameworkBenchmarks) harness for **Kite**, a .NET 10 web server and reverse proxy running on Kestrel.

## Test Profiles

- `default` (**Kite [Edge Plugin]**): Executes requests through Kite's `HostRouteTable`, HTTP security/filtering pipeline, and in-process hot-reloadable C# Edge Plugin (`Kite.TechEmpower.Plugin`), including all PostgreSQL database tests (`/db`, `/queries/`, `/updates/`, `/fortunes`, `/cached-worlds/`).
- `ipc` (**Kite [Worker IPC]**): Executes `/plaintext` and `/json` via `KiteWorkerIpc` binary pipe/UDS framing into an isolated collectible `AssemblyLoadContext` (`Kite.TechEmpower.App`).
- `proxy` (**Kite [Out-Of-Process Proxy]**): Executes `/plaintext` and `/json` via Kite's reverse proxy forwarding over HTTP/1.1 Unix domain sockets to an external ASP.NET Core worker process (`Kite.TechEmpower.App`).

## Local Verification & Benchmarking

From a TechEmpower `FrameworkBenchmarks` checkout with this folder linked or copied to `frameworks/CSharp/kite`:

```bash
./tfb --test kite kite-ipc kite-proxy --mode verify
./tfb --test kite
./tfb --test kite-ipc kite-proxy --type plaintext json
```
