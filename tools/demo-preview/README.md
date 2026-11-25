# Geschützte Demo-Vorschau des Research-Dashboards

Schreibgeschützte, Same-Origin-Vorschau des Dashboards für einen externen Betrachter (z. B. ChatGPT),
**hinter Cloudflare Access**. Verändert weder den Dev-Betrieb (:5899/:5034) noch echte Register.

## Was bereits vorbereitet ist (lokal, nicht veröffentlicht)

- **`dashboard/dist-demo/`** — Produktionsbuild mit RELATIVEN API-Pfaden (`/api/quant`, `/api/backtest`),
  d. h. keine `localhost:5034`-Aufrufe aus dem Browser.
- **`server.mjs`** — schreibgeschützter Same-Origin-Proxy auf `127.0.0.1:8088`:
  - bedient das gebaute Dashboard statisch,
  - reicht NUR eine Whitelist von Demo-**Lese**-/Compute-Routen an `:5034` weiter,
  - blockt alle übrigen `/api/*` mit **403** (neue Research-Läufe, Holdout-Auswertung/-Verbrauch, Jobs,
    Rithmic/Broker, Konfiguration) — serverseitig, nicht nur im UI,
  - beantwortet `/api/rithmic/status` kanonisch mit „deaktiviert",
  - blendet ein sichtbares **DEMO-Banner** ein.

Start des Proxys:  `node tools/demo-preview/server.mjs`  (Backend :5034 muss laufen)

## Noch erforderlich (nur durch dich — account-/downloadgebunden)

1. **Voraussetzungen prüfen:** Cloudflare-Account, eine bei Cloudflare verwaltete Domain, Zero Trust (Access) aktiv.
2. **cloudflared installieren:** `winget install --id Cloudflare.cloudflared` (oder offizieller Download).
3. **Anmelden:** `cloudflared tunnel login` → Browser → Domain wählen (schreibt `cert.pem` nach `~/.cloudflared`).
4. **Tunnel anlegen:** `cloudflared tunnel create research-demo`
5. **Access-Application ZUERST anlegen** (Zero Trust → Access → Applications → Add → Self-hosted):
   - Application domain: `research-demo.<deine-domain>`
   - Policy: *Allow*, Include → deine E-Mail(s); Login per Einmal-PIN (E-Mail) oder deinem IdP.
   - Erst danach den Tunnel live schalten, damit der Ursprung nie ungeschützt erreichbar ist.
6. **Config:** `cloudflared.config.example.yml` nach `~/.cloudflared/config.yml` kopieren, `<TUNNEL-ID>`/Domain setzen.
7. **DNS-Route:** `cloudflared tunnel route dns research-demo research-demo.<deine-domain>`
8. **Starten:** `cloudflared tunnel run research-demo`
9. **Prüfen (extern):** ohne Login blockiert Access Seite UND API; nach Login lädt `/research` mit den
   gespeicherten Demo-Ergebnissen; MC-Animation + Trade-Fokus funktionieren.

## Abschalten

- Tunnel stoppen: im `cloudflared`-Fenster **Strg+C**.
- DNS entfernen: `cloudflared tunnel route dns --overwrite-dns` bzw. den CNAME im Cloudflare-DNS löschen;
  optional `cloudflared tunnel delete research-demo`.
- Access-Application im Zero-Trust-Dashboard löschen.
- Lokalen Proxy stoppen (Node-Prozess auf :8088 beenden).
- `dashboard/dist-demo/` kann gelöscht werden (reines Build-Artefakt).
