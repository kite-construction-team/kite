# Sample.NodeApi

Zero-dependency Node.js HTTP & SSE sample hosted via Kite's `OutOfProcessProxy` supervisor.

## Endpoints
- `GET /` — Returns runtime metadata (`service`, `nodeVersion`, `pid`, forwarded headers)
- `GET /healthz` — Lightweight readiness/liveness probe
- `GET /api/telemetry` — Node.js `process.memoryUsage()` and uptime
- `GET /sse/events` — Chunked `text/event-stream` pass-through demo
- `POST /api/echo` — Echoes request body back as JSON

## Hosting with Kite
1. Create or configure an Application Pool (e.g. `node-pool` from `kite.pool.json`).
2. In **Sites & App Pools** (`Inspecting Site`), set:
   - **Workload Hosting Mode**: `OutOfProcessProxy`
   - **Physical Storage Root Path**: `<repo>/samples/Sample.NodeApi`
   - **Process Executable**: `node`
   - **Process Arguments**: `server.js`
   - Enable **Unbuffered SSE / gRPC / Streaming Pass-Through** if testing `/sse/events`.
3. Alternatively run standalone: `npm start`.
