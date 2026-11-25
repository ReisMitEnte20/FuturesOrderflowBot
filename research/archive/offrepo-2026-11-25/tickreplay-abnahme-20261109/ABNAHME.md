# Tick-Replay mit 5-Minuten-Kerzen – Abnahme 2026-11-09

Collection (nur gelesen): `F:\ATAS_Data\Collections\MNQ_NewYork_0930-1200_America-New-York_2026-10-26_bis_2026-10-30`
trades.csv SHA256 vorher/nachher: `cf46b44debdc78da1d957911a5c5d5a147abd7ce7398fddcd5bd51877fd5389e` (unverändert).

## Unabhängiger Vollabgleich (Original-CSV, exakte Nanosekunden) vs. API + Dashboard-Engine

| Tag | Trades | Kerzen | Volumen | Ergebnis |
|---|---:|---:|---:|---|
| 2026-10-26 | 1.011.851 | 30/30 | 1.141.585 | identisch |
| 2026-10-27 | 823.530 | 30/30 | 962.024 | identisch |
| 2026-10-28 | 659.481 | 30/30 | 809.075 | identisch |
| 2026-10-29 | 1.192.766 | 30/30 | 1.346.551 | identisch |
| 2026-10-30 | 1.059.247 | 30/30 | 1.248.894 | identisch |
| Summe | 4.746.875 | 150 | 5.508.129 | = Baseline |

Verglichen je Kerze: Intervall, Open, High, Low, Close, Volumen, Buy-/Sell-Volumen, Trade-Anzahl.
Skripte: `pruefskripte/independent.py` (unabhängig, liest Byte-Bereich des Tages laut Manifest),
`pruefskripte/engine_candles.mjs` (echte API portionsweise + Engine), `pruefskripte/compare.sh`.

## Browser (echtes Abspielen in /research)
- 26.10. abgespielt bis 10:55 ET: 17 abgeschlossene Kerzen + laufende Kerze; mehrere 5-Min-Wechsel live beobachtet.
- 1× (Uhr 1:1 zur Wandzeit) → S1; Reset + 100× darüber hinaus + Rücksprung auf exakt S1-Zeit → S2; Reset + Direktsprung → S3:
  S1 ≡ S2 ≡ S3 (byte-identischer Zustand inkl. aller Kerzen). Gleiches nach 5 Kerzenwechseln (S4 ≡ S5 ≡ S6).
  Die im Browser angezeigten Kerzen 09:30–09:50 stimmen exakt mit der unabhängigen Aggregation überein.
- Einzelschritt: genau +1 Trade. −1 min / „Gehe zu 10:35:00 ET“ korrekt (EDT + UTC angezeigt).
- Echter Grenzfall: Trade exakt 14:35:00.000000000Z eröffnet die Kerze 10:35–10:40; zwei Trades mit identischem
  Zeitstempel 14:34:59.9772582Z bleiben in der alten Kerze.
- Datumswechsel während 100×-Wiedergabe 26 → 29 → 27: Anfrage für 29 abgebrochen (ERR_ABORTED), nur 27 übernommen.
- Echtes Chromium (Edge headless, 1600×1000): ~73 fps während der Wiedergabe, Geschwindigkeit 1,00× / 9,97× / 100,02×,
  keine Long-Tasks, keine Konsolenfehler. (Das in Claude eingebettete Browser-Pane schafft mit Viewport-Emulation
  generell nur ~5 fps bei jeder DOM-Änderung – Umgebungsgrenze, nicht die App.)

## Tests
.NET 765/765 (inkl. 6 neue TickReplayService-Tests), Frontend 42/42 (inkl. 14 neue Engine-Tests),
TypeScript + Vite-Build ok (bekannte Chunk-Größenwarnung), git diff --check sauber.

## Artefakte
`tick_replay_aufnahme.gif` (Bildschirmaufnahme: 10× über 10:20-Grenze, 100× über 10:45/10:50/10:55),
`01…05_*.png` (Screenshots), `aufnahme_frames/` (Einzelbilder).
