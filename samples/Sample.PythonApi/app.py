from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
import platform
import sys
import time
from urllib.parse import urlparse


def resolve_bind_address():
    host = os.environ.get("HOST") or "127.0.0.1"
    port = int(os.environ.get("PORT", "0") or 0)

    args = sys.argv[1:]
    i = 0
    while i < len(args):
        if args[i] == "--urls" and i + 1 < len(args):
            parsed = urlparse(args[i + 1])
            if parsed.hostname:
                host = parsed.hostname
            if parsed.port:
                port = parsed.port
            i += 2
            continue
        if args[i] == "--port" and i + 1 < len(args):
            port = int(args[i + 1])
            i += 2
            continue
        i += 1

    if port <= 0:
        port = 5102

    return host, port


BIND_HOST, BIND_PORT = resolve_bind_address()
STARTED_AT = time.monotonic()


class KitePythonHandler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, fmt, *args):
        # Keep console logs structured and concise
        sys.stdout.write(f"[Sample.PythonApi pid={os.getpid()}] {fmt % args}\n")
        sys.stdout.flush()

    def _send_json(self, status_code: int, payload: dict):
        raw = json.dumps(payload).encode("utf-8")
        self.send_response(status_code)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(raw)))
        self.send_header("X-Kite-Hosting-Mode", "OutOfProcessProxy")
        self.send_header("X-Worker-Runtime", f"python-{platform.python_version()}")
        self.send_header("X-Worker-Pid", str(os.getpid()))
        self.end_headers()
        self.wfile.write(raw)

    def do_GET(self):
        parsed = urlparse(self.path)

        if parsed.path == "/healthz":
            self._send_json(200, {
                "status": "healthy",
                "service": "Sample.PythonApi",
                "pid": os.getpid()
            })
            return

        if parsed.path == "/api/info":
            self._send_json(200, {
                "service": "Sample.PythonApi",
                "runtime": "python",
                "pythonVersion": platform.python_version(),
                "platform": platform.platform(),
                "pid": os.getpid(),
                "uptimeSeconds": round(time.monotonic() - STARTED_AT, 2),
                "cwd": os.getcwd()
            })
            return

        if parsed.path == "/sse/stream":
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream; charset=utf-8")
            self.send_header("Cache-Control", "no-cache")
            self.send_header("Connection", "close")
            self.send_header("X-Worker-Pid", str(os.getpid()))
            self.end_headers()
            self.close_connection = True
            for idx in range(1, 4):
                frame = f"event: tick\ndata: {json.dumps({'service': 'Sample.PythonApi', 'tick': idx, 'pid': os.getpid()})}\n\n"
                self.wfile.write(frame.encode("utf-8"))
                self.wfile.flush()
                time.sleep(0.05)
            return

        self._send_json(200, {
            "service": "Sample.PythonApi",
            "runtime": "python",
            "pythonVersion": platform.python_version(),
            "method": "GET",
            "path": parsed.path,
            "query": parsed.query,
            "pid": os.getpid(),
            "port": BIND_PORT,
            "forwardedFor": self.headers.get("X-Forwarded-For"),
            "forwardedProto": self.headers.get("X-Forwarded-Proto")
        })

    def do_POST(self):
        parsed = urlparse(self.path)
        content_length = int(self.headers.get("Content-Length", "0") or 0)
        body_bytes = self.rfile.read(content_length) if content_length > 0 else b""
        self._send_json(200, {
            "service": "Sample.PythonApi",
            "method": "POST",
            "path": parsed.path,
            "pid": os.getpid(),
            "echo": body_bytes.decode("utf-8", errors="replace")
        })


if __name__ == "__main__":
    httpd = ThreadingHTTPServer((BIND_HOST, BIND_PORT), KitePythonHandler)
    print(f"Sample.PythonApi listening on http://{BIND_HOST}:{BIND_PORT} (pid={os.getpid()})", flush=True)
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        httpd.server_close()
