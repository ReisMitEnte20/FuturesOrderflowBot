# MNQ NewYork 26.–30.10.2026 — ausgeführte Tagesläufe

Stand: 2026-11-06. Bestehender TradingBot.OrderflowRun → OrderflowMultiStrategyRunner → BacktestStrategyRunner / BacktestEngine. Keine Optimierung, keine neuen Aufnahmen, keine ATAS-Bedienung.

Collection: F:\ATAS_Data\Collections\MNQ_NewYork_0930-1200_America-New-York_2026-10-26_bis_2026-10-30\collection_manifest.json

Täglich 09:30–12:00 America/New_York = 13:30–16:00 UTC, [start,end). 500 Ticks/Bar, max. 1 Kontrakt, getrennte Tages-/Kandidaten-Ledger, Startkapital je 10.000 USD. Bestehende Config: 0,39 USD pro Seite, 1 Tick Slippage.

| Tag | Ticks | Volumen | Volle Bars | Rest-Ticks | Lauf-ID |
|---|---:|---:|---:|---:|---|
| 2026-10-26 | 1011851 | 1141585 | 2023 | 351 | orderflow-20261106-165646-cf46b44d |
| 2026-10-27 | 823530 | 962024 | 1647 | 30 | orderflow-20261106-165746-cf46b44d |
| 2026-10-28 | 659481 | 809075 | 1318 | 481 | orderflow-20261106-165826-cf46b44d |
| 2026-10-29 | 1192766 | 1346551 | 2385 | 266 | orderflow-20261106-165906-cf46b44d |
| 2026-10-30 | 1059247 | 1248894 | 2118 | 247 | orderflow-20261106-165946-cf46b44d |

Summe: 4.746.875 Ticks, 5.508.129 Volumen. Buy/Sell/Total je Tag exakt mit Manifest abgeglichen; unbekanntes Volumen 0. Rest-Ticks werden in der Simulation berücksichtigt, bilden aber keine vollständige Signal-Bar.

| Tag | Kandidat | Trades | Realisiertes Netto USD | Offene Endposition | Unrealisiertes Brutto USD | Eintrittsfee offene Position USD |
|---|---|---:|---:|---|---:|---:|
| 2026-10-26 | Momentum(Price) | 788 | -9153.14 | Long 1 | -1.50 | 0.39 |
| 2026-10-26 | DeltaFlow(OF) | 387 | -3070.86 | Flat 0 | 0.00 | 0.00 |
| 2026-10-26 | Fade(Price) | 788 | 6347.86 | Short 1 | 0.50 | 0.39 |
| 2026-10-27 | Momentum(Price) | 641 | -7136.48 | Short 1 | -0.50 | 0.39 |
| 2026-10-27 | DeltaFlow(OF) | 335 | -2252.30 | Short 1 | -0.50 | 0.39 |
| 2026-10-27 | Fade(Price) | 641 | 4854.52 | Long 1 | -0.50 | 0.39 |
| 2026-10-28 | Momentum(Price) | 521 | -5894.88 | Long 1 | -7.50 | 0.39 |
| 2026-10-28 | DeltaFlow(OF) | 285 | -1610.30 | Short 1 | 14.50 | 0.39 |
| 2026-10-28 | Fade(Price) | 521 | 4040.12 | Short 1 | 6.50 | 0.39 |
| 2026-10-29 | Momentum(Price) | 940 | -12606.20 | Long 1 | 8.00 | 0.39 |
| 2026-10-29 | DeltaFlow(OF) | 432 | -4454.46 | Flat 0 | 0.00 | 0.00 |
| 2026-10-29 | Fade(Price) | 940 | 9259.80 | Short 1 | -9.00 | 0.39 |
| 2026-10-30 | Momentum(Price) | 841 | -10774.48 | Short 1 | -8.00 | 0.39 |
| 2026-10-30 | DeltaFlow(OF) | 436 | -3948.58 | Flat 0 | 0.00 | 0.00 |
| 2026-10-30 | Fade(Price) | 841 | 7780.52 | Long 1 | 7.00 | 0.39 |

Kein künstlicher Endschluss / keine hypothetische Exit-Fee. Alle 15 Kandidatenausführungen Completed. Keine Strategieparameter geändert. Ergebnisse sind rückblickende technische Research-Läufe, kein OOS-/Holdout-Nachweis. Historische Kontrakt-/Serienidentität bleibt unbestätigt.

Original-CSV SHA256 vorher/nachher: CF46B44DEBDC78DA1D957911A5C5D5A147ABD7CE7398FDDCD5BD51877FD5389E. Alle zehn bisherigen Dashboard-Laufdateien, der Fallback und beide Collector-EXEs per Hash unverändert. Index enthält nun 15 Läufe. Keine Commits/Push/Merges.

Browserprüfung: alle fünf Tage auf http://127.0.0.1:5899/research über die Orderflow-Auswahl geladen; Session, Importvolumen, Datendetails und Charts geprüft. Drei Kandidaten am 26.10. gewechselt; offene Long-/Short-Endpositionen sichtbar. Keine Browser-Konsolenfehler. Tape bleibt auf 5.000 exportierte Ticks begrenzt; Bars/Footprint/Indikatoren nutzen die volle Tagesbasis.

Validierung: .NET Build und Runner-Build erfolgreich; 759/759 .NET-Tests, 28/28 Frontend-Tests; TypeScript/Vite-Build erfolgreich (bekannte Chunkgrößenwarnung). git diff --check sauber. Tests behalten ihre temporären Fixtures (keine Löschungen).

Implementierung: AtasCollectionImporter.cs (streamender, fail-closed Tagesimport), Program.cs (--collection/--day; alte Laufdateien erhalten), ResearchWorkspace.tsx (Sessionnamen und Importnachweis), 8 Importtests und Runner-README. Engine-Zeiten werden auf 100 ns abgerundet; Nanosekunden-Fensterprüfung und originale Reihenfolge bleiben erhalten.

Lokale Logs, Hashlisten und Ergebnisse: F:\ATAS_Data\Research\MNQ_NewYork_20261026_30_codex\. Hindsight nicht verfügbar, nicht beschrieben; CodeMunch für Guide, Repo-/Codeprüfung verwendet (teilweise veralteter Index, gezielte lokale Ergänzung).
