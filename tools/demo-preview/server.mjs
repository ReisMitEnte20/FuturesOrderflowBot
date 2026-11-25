// Schreibgeschützte Same-Origin-Demo-Vorschau des Research-Dashboards.
//
// Zweck: EIN geschützter Ursprung bedient sowohl das gebaute Dashboard (statisch) als auch die WENIGEN
// notwendigen Demo-LESE-/Compute-Routen der lokalen Backend-API (:5034). Alle schreibenden Endpunkte
// (neue Research-Läufe, Holdout-Auswertung/-Verbrauch, Jobs, Broker/Rithmic, Konfiguration) werden
// SERVERSEITIG mit 403 gesperrt — nicht nur im UI versteckt. Das Frontend ruft ausschließlich relative
// /api-Pfade auf (Build mit VITE_QUANT_API=/api/quant, VITE_BACKTEST_API=/api/backtest), also KEINE
// localhost:5034-Aufrufe aus dem Browser.
//
// Dieser Proxy ist NUR für die (später vorgeschaltete) authentifizierte Vorschau gedacht. Er bindet an
// 127.0.0.1 — er wird NICHT direkt veröffentlicht; davor gehört Cloudflare Tunnel + Access.
//
// Start:  node tools/demo-preview/server.mjs
// Env:    DEMO_PORT (Default 8088), BACKEND (Default http://localhost:5034), DIST (Default dashboard/dist-demo)

import http from "node:http";
import { readFile, stat } from "node:fs/promises";
import { extname, join, normalize } from "node:path";
import { fileURLToPath } from "node:url";

const HERE = fileURLToPath(new URL(".", import.meta.url));
const PORT = parseInt(process.env.DEMO_PORT || "8088", 10);
const HOST = "127.0.0.1";
const BACKEND = process.env.BACKEND || "http://localhost:5034";
const DIST = process.env.DIST || join(HERE, "..", "..", "dashboard", "dist-demo");

// Nur diese API-Routen sind erlaubt (Methode + exakte Pfad-Regex). Alles andere → 403.
const ALLOW = [
  ["GET", /^\/api\/quant\/status$/],
  ["GET", /^\/api\/quant\/benchmarks$/],
  ["GET", /^\/api\/quant\/research(\?.*)?$/],
  ["GET", /^\/api\/quant\/research\/[^/]+$/],
  ["GET", /^\/api\/quant\/campaigns$/],
  ["GET", /^\/api\/quant\/trials(\?.*)?$/],
  ["GET", /^\/api\/quant\/trials\/[^/]+$/],
  ["GET", /^\/api\/quant\/campaigns\/[^/]+\/holdout$/], // NUR Lesen des Holdout-Status (GET); evaluate/consume sind POST → gesperrt
  ["GET", /^\/api\/quant\/papers$/],
  ["GET", /^\/api\/backtest\/strategies$/],
  ["GET", /^\/api\/backtest\/instruments$/],
  ["GET", /^\/api\/backtest\/data-sources$/],
  // Reine Berechnung für den Kerzenchart/Trade-Fokus — kein Registerschreiben, keine Orders:
  ["POST", /^\/api\/backtest\/run$/],
  ["POST", /^\/api\/backtest\/candles$/],
];

const MIME = {
  ".html": "text/html; charset=utf-8", ".js": "text/javascript; charset=utf-8", ".css": "text/css; charset=utf-8",
  ".json": "application/json; charset=utf-8", ".svg": "image/svg+xml", ".png": "image/png", ".jpg": "image/jpeg",
  ".ico": "image/x-icon", ".woff": "font/woff", ".woff2": "font/woff2", ".map": "application/json; charset=utf-8",
};

const BANNER = `
<div id="demo-preview-banner" style="position:fixed;top:0;left:0;right:0;z-index:99999;background:#e8c069;color:#1a1a1a;font:600 12px/1.4 system-ui,sans-serif;padding:6px 12px;text-align:center;box-shadow:0 1px 4px rgba(0,0,0,.4)">
  DEMO-VORSCHAU · schreibgeschützt · nur gespeicherte Ergebnisse · keine neuen Läufe, kein Holdout-Verbrauch, keine Orders
</div>
<style>body{padding-top:30px !important}</style>
`;

function isAllowed(method, path) {
  return ALLOW.some(([m, re]) => m === method && re.test(path));
}

async function serveStatic(res, urlPath) {
  // SPA: unbekannte Nicht-Datei-Pfade → index.html.
  let rel = decodeURIComponent(urlPath.split("?")[0]);
  if (rel === "/" || rel === "") rel = "/index.html";
  let full = normalize(join(DIST, rel));
  if (!full.startsWith(normalize(DIST))) { res.writeHead(403).end("forbidden"); return; }

  let isFile = false;
  try { isFile = (await stat(full)).isFile(); } catch { isFile = false; }
  if (!isFile) { full = join(DIST, "index.html"); rel = "/index.html"; }

  try {
    let body = await readFile(full);
    const type = MIME[extname(full)] || "application/octet-stream";
    if (extname(full) === ".html") {
      body = Buffer.from(readFileHtmlWithBanner(body.toString("utf8")), "utf8");
    }
    res.writeHead(200, { "Content-Type": type, "Cache-Control": "no-store" }).end(body);
  } catch {
    res.writeHead(404).end("not found");
  }
}

function readFileHtmlWithBanner(html) {
  return html.includes("<body>") ? html.replace("<body>", "<body>" + BANNER) : BANNER + html;
}

function readBody(req) {
  return new Promise((resolve) => {
    const chunks = [];
    req.on("data", (c) => chunks.push(c));
    req.on("end", () => resolve(Buffer.concat(chunks)));
    req.on("error", () => resolve(Buffer.alloc(0)));
  });
}

const server = http.createServer(async (req, res) => {
  const { method } = req;
  const path = req.url || "/";

  if (path.startsWith("/api/")) {
    // Der app-weite Verbindungs-Poller fragt /api/rithmic/status ab. In der Demo NICHT an den Broker-Bereich
    // weiterreichen — eine kanonische "deaktiviert"-Antwort liefern (kein Broker-Call, keine Konsolenfehler).
    if (method === "GET" && /^\/api\/rithmic\/status$/.test(path)) {
      res.writeHead(200, { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" })
        .end(JSON.stringify({ enabled: false, isConnected: false, lastError: null, message: "Demo-Vorschau: Rithmic deaktiviert." }));
      return;
    }
    if (!isAllowed(method, path)) {
      res.writeHead(403, { "Content-Type": "application/json; charset=utf-8" })
        .end(JSON.stringify({ ok: false, error: "DEMO_READONLY: In der Demo-Vorschau ist dieser Endpunkt gesperrt." }));
      return;
    }
    try {
      const body = method === "GET" || method === "HEAD" ? undefined : await readBody(req);
      const upstream = await fetch(BACKEND + path, {
        method,
        headers: { "Content-Type": req.headers["content-type"] || "application/json", Accept: "application/json" },
        body,
      });
      const buf = Buffer.from(await upstream.arrayBuffer());
      res.writeHead(upstream.status, {
        "Content-Type": upstream.headers.get("content-type") || "application/json; charset=utf-8",
        "Cache-Control": "no-store",
      }).end(buf);
    } catch (e) {
      res.writeHead(502, { "Content-Type": "application/json; charset=utf-8" })
        .end(JSON.stringify({ ok: false, error: "Backend nicht erreichbar: " + (e?.message || String(e)) }));
    }
    return;
  }

  await serveStatic(res, path);
});

server.listen(PORT, HOST, () => {
  console.log(`[demo-preview] read-only same-origin preview on http://${HOST}:${PORT}`);
  console.log(`[demo-preview] static: ${DIST}`);
  console.log(`[demo-preview] backend: ${BACKEND} (only whitelisted read/compute routes proxied; all writes -> 403)`);
});
