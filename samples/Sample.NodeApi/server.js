import http from 'node:http';
import { URL } from 'node:url';

function resolveBindAddress() {
  let host = process.env.HOST || '127.0.0.1';
  let port = Number.parseInt(process.env.PORT || '', 10);

  const args = process.argv.slice(2);
  for (let i = 0; i < args.length; i++) {
    if (args[i] === '--urls' && args[i + 1]) {
      try {
        const parsed = new URL(args[i + 1]);
        host = parsed.hostname || host;
        if (parsed.port) {
          port = Number.parseInt(parsed.port, 10);
        }
      } catch {
        // Ignore malformed --urls and fall back to env/default
      }
    } else if (args[i] === '--port' && args[i + 1]) {
      port = Number.parseInt(args[i + 1], 10);
    }
  }

  if (!Number.isFinite(port) || port <= 0) {
    port = 5101;
  }

  return { host, port };
}

const { host, port } = resolveBindAddress();

function writeJson(res, statusCode, payload) {
  const body = JSON.stringify(payload);
  res.writeHead(statusCode, {
    'Content-Type': 'application/json; charset=utf-8',
    'Content-Length': Buffer.byteLength(body),
    'X-Kite-Hosting-Mode': 'OutOfProcessProxy',
    'X-Worker-Runtime': `node-${process.version}`,
    'X-Worker-Pid': String(process.pid)
  });
  res.end(body);
}

const server = http.createServer((req, res) => {
  const reqUrl = new URL(req.url || '/', `http://${req.headers.host || '127.0.0.1'}`);

  if (reqUrl.pathname === '/healthz') {
    writeJson(res, 200, { status: 'healthy', service: 'Sample.NodeApi', pid: process.pid });
    return;
  }

  if (reqUrl.pathname === '/api/telemetry') {
    const mem = process.memoryUsage();
    writeJson(res, 200, {
      service: 'Sample.NodeApi',
      nodeVersion: process.version,
      pid: process.pid,
      uptimeSeconds: Number(process.uptime().toFixed(2)),
      rssBytes: mem.rss,
      heapUsedBytes: mem.heapUsed
    });
    return;
  }

  if (reqUrl.pathname === '/sse/events') {
    res.writeHead(200, {
      'Content-Type': 'text/event-stream; charset=utf-8',
      'Cache-Control': 'no-cache',
      'Connection': 'keep-alive',
      'X-Worker-Pid': String(process.pid)
    });
    res.write(`event: ready\ndata: ${JSON.stringify({ service: 'Sample.NodeApi', pid: process.pid })}\n\n`);
    let count = 0;
    const timer = setInterval(() => {
      count += 1;
      res.write(`event: tick\ndata: ${JSON.stringify({ tick: count, ts: new Date().toISOString() })}\n\n`);
      if (count >= 3) {
        clearInterval(timer);
        res.end();
      }
    }, 100);
    req.on('close', () => clearInterval(timer));
    return;
  }

  if (req.method === 'POST' && reqUrl.pathname === '/api/echo') {
    const chunks = [];
    req.on('data', chunk => chunks.push(chunk));
    req.on('end', () => {
      const bodyText = Buffer.concat(chunks).toString('utf8');
      writeJson(res, 200, {
        service: 'Sample.NodeApi',
        method: req.method,
        path: reqUrl.pathname,
        pid: process.pid,
        echo: bodyText
      });
    });
    return;
  }

  writeJson(res, 200, {
    service: 'Sample.NodeApi',
    runtime: 'nodejs',
    nodeVersion: process.version,
    method: req.method || 'GET',
    path: reqUrl.pathname,
    query: reqUrl.search,
    pid: process.pid,
    port,
    forwardedFor: req.headers['x-forwarded-for'] || null,
    forwardedProto: req.headers['x-forwarded-proto'] || null
  });
});

function shutdown() {
  server.close(() => process.exit(0));
  setTimeout(() => process.exit(0), 2000).unref();
}

process.on('SIGINT', shutdown);
process.on('SIGTERM', shutdown);

server.listen(port, host, () => {
  console.log(`Sample.NodeApi listening on http://${host}:${port} (pid=${process.pid})`);
});
