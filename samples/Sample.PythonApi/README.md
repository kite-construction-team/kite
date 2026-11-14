# Sample.PythonApi

Zero-dependency Python 3 HTTP & SSE sample hosted via Kite's `OutOfProcessProxy` supervisor.

## Endpoints
- `GET /` — Returns Python runtime metadata (`service`, `pythonVersion`, `pid`, forwarded headers)
- `GET /healthz` — Lightweight readiness/liveness probe
- `GET /api/info` — Platform, uptime, working directory, and PID
- `GET /sse/stream` — Chunked `text/event-stream` pass-through demo
- `POST /api/echo` — Echoes request body back as JSON

## Hosting with Kite
1. Create or configure an Application Pool (e.g. `python-pool` from `kite.pool.json` with `PYTHONUNBUFFERED=1`).
2. In **Sites & App Pools** (`Inspecting Site`), set:
   - **Workload Hosting Mode**: `OutOfProcessProxy`
   - **Physical Storage Root Path**: `<repo>/samples/Sample.PythonApi`
   - **Process Executable**: `python` (or full path such as `C:\Python39\python.exe` / `.venv\Scripts\python.exe`)
   - **Process Arguments**: `app.py` (or `-m uvicorn main:app --host 127.0.0.1 --port {port}` for ASGI frameworks)
   - Enable **Unbuffered SSE / gRPC / Streaming Pass-Through** if testing `/sse/stream`.
3. Alternatively run standalone: `python app.py`.
