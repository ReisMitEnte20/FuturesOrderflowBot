# atas-live-bridge/ – ATAS-Indikator „OF Live Bridge (read-only)“

Liveweg: **ATAS → Bridge (dieser Indikator) → Backend (`/api/marketdata/live`) → Dashboard (Marktdaten → Live)**.

- **Nur lesend.** Ein `Indicator`, keine `ChartStrategy`.
  - Kein `TradingManager`, keine Order-, Konto- oder Portfolio-Methoden. Ein Test prüft das im Quelltext.
  - Verbindung ausschließlich zu `127.0.0.1` (Standardport 5036). Nichts wird an ATAS zurückgesendet.
- **Unabhängig vom Recorder.**
  - Er nutzt dieselben, dort bewährten SDK-Callbacks: `OnNewTrade`, `OnCumulativeTrade`/`OnUpdateCumulativeTrade`,
    `OnBestBidAskChanged`, `MarketDepthChanged(s)`, `GetCandle` + `GetAllPriceLevels`.
  - Neu ist nur `MarketDepthInfo.GetMarketDepthSnapshot()` beim Verbindungsaufbau.
  - Aufnahme- und Archivpfad des Recorders (`../atas-recorder`) bleiben unverändert.

## Bauen und installieren

```
powershell -ExecutionPolicy Bypass -File research\atas-live-bridge\build.ps1
```

- Der Build läuft gegen die installierten ATAS-Bibliotheken (geprüft mit ATAS 8.0.15.306) und schreibt nach `out\`
  (gitignored). Er kopiert nichts nach ATAS.
- Zum Installieren `out\OrderFlowLiveBridge.dll` nach `%APPDATA%\ATAS\Indicators` kopieren. Unter Umständen lädt ATAS
  den Indikator erst nach einem Neustart.
- Danach im MNQ-Chart unter Indikatoren „OF Live Bridge (read-only)“ hinzufügen.
- Das Backend muss laufen (`dotnet run --project src\TradingBot.DevDashboard ...`).

## Protokoll `atas-live-1` (NDJSON, eine Zeile je Nachricht)

| type | Inhalt |
|---|---|
| `hello` | Quelle (`atas` bzw. `test`), Instrument und Börse laut `InstrumentInfo`, TickSize, Charttyp/Zeitrahmen, ATAS-Version, Sitzungs-ID |
| `trade` | Marktzeit `t`, Empfang `r`, Preis, Volumen, Aggressor (`Buy`/`Sell`/`Unknown`) |
| `bbo` / `dom` | Seite, Preis, Volumen (Volumen 0 = Level entfernt) |
| `domSnapshot` | volles Buch beim Verbindungsaufbau (neu nach jeder Wiederverbindung) |
| `cum` | kumulierter Trade: `id`, `ev` = new/update, kumuliertes Volumen. Das Backend **ersetzt** je `id` den Stand und zählt nie doppelt |
| `bar` | Kerze der Chartinstanz (geschlossen oder laufend): OHLC, Volumen, Bid/Ask, Delta, Ticks, Kerzen-VWAP, Footprint je Preis |
| `hb` | Heartbeat je Sekunde mit verworfenen Ereignissen je Stream (Puffer voll oder Backend offline) |

Sequenznummern sind lokal und monoton. Das Backend zählt Lücken; verlorene Ereignisse werden nie still verworfen.

## Ohne ATAS prüfen: `testfeed.py`

```
python research\atas-live-bridge\testfeed.py --seconds 150 [--disconnect-after 30] [--gap-every 500]
```

Der Testfeed spricht dasselbe Protokoll, meldet sich aber als `source=test` mit Instrument `MNQ-TEST`. Das Dashboard
kennzeichnet dann alles als **TESTDATEN**. Seine Kerzen baut er aus den eigenen Trades; der Kerzenvergleich zeigt dabei
nur, dass der Datenweg stimmt. Er ist **kein** Vergleich mit ATAS.

## Grenzen (Stand dieser Version)

- **Nicht in ATAS geladen.**
  - Die DLL ist gebaut, aber noch nicht in ATAS installiert und nie mit einem echten Feed gelaufen. Die Live-Abnahme steht aus.
  - Live- oder Replay-Modus wird nur aus Zeitstempeln abgeleitet (Empfang − Marktzeit). Ein ATAS-Moduskennzeichen ist
    nicht ausgelesen.
- **Nicht zugänglich über die öffentliche Indikator-API:**
  - die eigene VWAP-Linie (andere Indikatorinstanzen lassen sich nicht auflisten),
  - die Werte des CumulativeDelta-Indikators,
  - TPO/Market Profile,
  - die Filter des Smart-Tape-Moduls.
- **Kontrakt:** Er steht so im Dashboard, wie ATAS ihn als `InstrumentInfo.Instrument` meldet. Nichts wird aus dem Datum
  abgeleitet.
