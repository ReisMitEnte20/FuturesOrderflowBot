# Marktdaten – was der Bot aus ATAS sieht

**Hauptweg:** Marktdaten (Startseite `/`) → **Live** | **Aufzeichnung**.

- Keine Orders, keine Strategie, kein Training auf dieser Ansicht.
- Backtesting bleibt separat unter „Backtesting“.
- Der Paper-Desk liegt jetzt unter `/desk`, die Rithmic-Kerzen unter `/market`; beide stehen in der Navigation unter „Weitere“.
- Alle bisherigen Direktlinks (`/backtesting?...`, `/research`, `/backtest`, `/market`) funktionieren weiter.

## Datenweg (nur lesend)

```
ATAS (Chart mit „OF Live Bridge (read-only)“) ──TCP 127.0.0.1:5036, NDJSON atas-live-1──▶ Backend AtasLiveListener/AtasLiveFeed
   ──GET /api/marketdata/live/state (+ /live/compare)──▶ Dashboard Marktdaten → Live (gedrosselte Abfrage, 400 ms)
```

- **Bridge:** `research/atas-live-bridge/`, ein eigener ATAS-Indikator und unabhängig vom Recorder.
- **Backend:** `src/TradingBot.DevDashboard/Services/Live/`. Dieselben strukturierten Werte stehen späteren Modellen zur
  Verfügung, ohne Bildschirmbilder.
- **Der Browser greift nie auf ATAS zu** – kein Mausklick, kein OCR.
- **Aufzeichnung:**
  - Sie öffnet einen vorhandenen London- oder New-York-Tag im selben Chart wie Backtesting (`DayReplay`), ohne
    Lauf-Überlagerung.
  - Die Herkunft der Aufnahme kommt aus dem lokalen Datenqualitäts-Audit (`artifacts/data-audit/`, gitignored), abrufbar
    über `GET /api/marketdata/recordings/{id}/provenance`.

## Herkunft – jeder Wert genau eine Klasse

| Klasse im Dashboard | Bedeutung |
|---|---|
| **LIVE · ATAS** | live von ATAS empfangen (SDK-Callback) oder direkt aus der laufenden Chartinstanz gelesen (`GetCandle`) |
| **ATAS-Minutenhistorie** | stammt aus einer historischen ATAS-Minutenkerze (Aufzeichnungen im Replay-Typ `CandlesAndGeneratedBestPrices`) |
| **generiert/berechnet** | im ATAS-Replay generiert (Tickfolge innerhalb der Minute, Bid/Ask, DOM) oder von uns berechnet (Minutenkerzen, CVD, VWAP) |
| **nicht verfügbar/ungeprüft** | mit diesem Feed bzw. dieser ATAS-Version nicht zugänglich oder ohne Beleg |
| **TESTDATEN** | `testfeed.py` – keine Marktdaten |

Wertzustände:

- `wert`: Bei 0 ist es eine echte Null.
- `noch_kein_wert`
- `veraltet`: verbunden, aber seit über 3 s keine Nachricht.
- `feed_getrennt`
- `quelle_ungeklaert`

## Was sieht der Bot tatsächlich? (Stand 2026-11-25)

| Daten | Live (Bridge) | Aufzeichnungen (Collections) | Nachweis |
|---|---|---|---|
| Einzelne Trades: Marktzeit, Preis, Volumen, Aggressor | per `OnNewTrade` vorgesehen | **generierte Tickfolge** aus ATAS-Minutenkerzen | Live: **ungeprüft** (Bridge noch nicht in ATAS geladen). Aufzeichnung: Audit (gleichverteilte Zeitstempel, Replay-Typ im Protokoll) |
| Minutenwerte OHLC, Volumen, Bid/Ask-Volumen | Kerze der Chartinstanz + unsere Berechnung | Minutenaggregate laut ATAS-Server-Kerze | Aufzeichnung: ATAS-Chartkerze = unsere Berechnung (gleiche Quelle, **keine** unabhängige Bestätigung) |
| Best Bid/Ask | per `OnBestBidAskChanged` vorgesehen | generiert | Live: ungeprüft |
| DOM (Markttiefe) | Snapshot + Updates vorgesehen | generiert, **1 Level je Seite** | Live: ungeprüft; ob der Feed mehr als Top-of-Book liefert, zeigt „beobachtete Level je Seite“ |
| Kumulierte Trades (Smart-Tape-Grunddaten) | per `OnCumulativeTrade` vorgesehen, ohne Doppelzählung | generiert (aus generierten Trades) | Live: ungeprüft |
| Kerzen-Delta, Footprint | aus der Chartinstanz (`GetCandle`) + unsere Berechnung | von uns aus Replay-Ereignissen | Vergleich ATAS ↔ Dashboard eingebaut; mit echten Daten **ausstehend** |
| Deine VWAP-Linie, CumulativeDelta-Werte, TPO/Market Profile, Smart-Tape-Filter | nicht zuverlässig zugänglich (öffentliche Indikator-API) | – | Reflection der installierten ATAS-Bibliotheken 8.0.15.306 |

Heute sieht der Bot **nachweislich nichts live aus ATAS**. ATAS lief während der Umsetzung nicht, und die Bridge ist
gebaut, aber noch nicht installiert. Belegt ist nur der Weg Backend → Dashboard, mit klar gekennzeichneten Testdaten.

## Stabilität (mit Testdaten geprüft)

- **Verbindungsabbruch:**
  - Der Status wechselt auf „getrennt“.
  - Das Buch wird verworfen (0 Level), Bid/Ask zeigt „Feed getrennt“.
  - Nach der Wiederverbindung bleibt das Buch „unvollständig“, bis ein neuer Snapshot eintrifft.
- **Stille Verbindung:** Status „veraltet“; das Buch wird nicht als aktuell gezeigt (Unit-Test).
- **Quellenwechsel:**
  - Eine neue Bridge-Sitzung oder ein anderes Instrument setzt Generation, Kerzen, Tape, Buch und Zähler zurück.
  - Ein Wechsel Live ↔ Aufzeichnung sowie London ↔ New York baut die Ansicht neu auf.
- **Zähler:**
  - empfangen, verarbeitet, Sequenzlücken, Backend-Puffer verworfen, Bridge verworfen (Puffer/offline), Parse-Fehler.
  - Die Anzeige fragt seltener ab, als Daten eintreffen; nichts geht still verloren.
- **Kerzenvergleich:**
  - Er läuft nur für Kerzen, deren Trades ab Kerzenbeginn lückenlos empfangen wurden; sonst „nicht vergleichbar“ mit Grund.
  - Vergleichsfelder: OHLC, Volumen, Buy/Sell, Delta, VWAP mit Kerzenanker und eine Footprint-Preisstufe.

## Noch offen (Live-Abnahme)

1. `OrderFlowLiveBridge.dll` nach `%APPDATA%\ATAS\Indicators` kopieren (gegebenenfalls ATAS neu starten) und an einen
   MNQ-Chart hängen.
2. Prüfen: Ein echter MNQ-Trade erscheint mit Marktzeit, Preis, Volumen und Aggressor; der Kontrakt steht so da, wie ATAS
   ihn meldet.
3. An einer abgeschlossenen Kerze den Vergleich ATAS ↔ Dashboard ablesen (Diagnose → Vergleich). Bei DOM und Tape ein
   zeitlich zuordenbares Update verfolgen.
