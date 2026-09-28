const http = require('http');

const PORT = 3001;
// Reine lokale Weiterleitung an das .NET-DevDashboard (R|Protocol-Client, nur Marktdaten).
// Dieser Proxy spricht selbst NIE mit Rithmic und meldet niemals selbst "connected".
const BACKEND = { hostname: '127.0.0.1', port: Number(process.env.BACKEND_PORT || 5034) };

function sendJson(res, status, body) {
  res.writeHead(status, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify(body));
}

const server = http.createServer((req, res) => {
  const url = new URL(req.url, `http://localhost:${PORT}`);
  if (!url.pathname.startsWith('/api/rithmic/')) {
    return sendJson(res, 404, { success: false, message: 'Not found' });
  }

  const headers = { ...req.headers, host: `${BACKEND.hostname}:${BACKEND.port}` };
  const upstream = http.request(
    { ...BACKEND, path: url.pathname + url.search, method: req.method, headers },
    (r) => {
      res.writeHead(r.statusCode, r.headers);
      r.pipe(res);
    },
  );
  upstream.on('error', () =>
    sendJson(res, 502, {
      success: false,
      message: `.NET-Backend auf Port ${BACKEND.port} nicht erreichbar (dotnet run --project src/TradingBot.DevDashboard).`,
    }),
  );
  req.pipe(upstream);
});

server.listen(PORT, '127.0.0.1', () => {
  console.log(`Rithmic proxy: http://localhost:${PORT} -> http://${BACKEND.hostname}:${BACKEND.port}`);
});
