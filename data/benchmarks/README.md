# Benchmarkdaten (lokal, nicht im Repository)

Der Benchmarkvergleich nutzt einen **austauschbaren Datenadapter**. Voreingestellt ist eine
CSV-Quelle, die dieses Verzeichnis liest. Ohne echte Datei gibt es **keine Vergleichskurve** — es
wird ausdrücklich keine ersatzweise erzeugt.

## Dateiformat

Eine Datei je Benchmark, benannt nach ihrer Kennung:

```
data/benchmarks/<kennung>.csv
```

Inhalt: zwei Spalten, Trennzeichen `,` oder `;`, Kommentarzeilen mit `#`, Kopfzeile optional.

```
date,close
2020-01-02,6800.12
2020-01-03,6785.44
```

- `date` — ISO-Datum oder Zeitstempel. Ohne Zeitzonenangabe wird **UTC** angenommen.
- `close` — Indexstand (> 0).

## Wichtig für einen redlichen Vergleich

1. **Total Return verwenden.** Der langfristige Vergleichsmaßstab ist der
   **S&P 500 Total Return** (Dividenden reinvestiert), nicht der Kursindex. Eine reine Kursreihe
   unterschätzt die Vergleichsrendite und verzerrt das Ergebnis zugunsten der Strategie.
   Der Adapter kann das nicht selbst feststellen; solange es nicht bestätigt ist, gibt der
   Vergleich eine entsprechende Warnung aus.
2. **Gleiche Währung.** Abweichende Währungen werden gemeldet, aber nicht umgerechnet.
3. **Kapitalbasis.** Futures werden auf Margin gehandelt. Eine auf die hinterlegte Margin bezogene
   Rendite ist **keine** Gesamtkapitalrendite und darf nicht mit einem Index verglichen werden.
   Im Dashboard ist dafür die Bestätigung „voll finanziertes Konto" zu setzen; fehlt sie, warnt
   der Vergleich.
4. **Gemeinsame Zeitbasis.** Verglichen wird ausschließlich auf Zeitpunkten, die in beiden Reihen
   vorkommen. Fehlende Benchmarkwerte werden weder fortgeschrieben noch interpoliert.

## Herkunft

Die Dateien stammen vom Nutzer und werden **nicht** ins Repository aufgenommen
(siehe `.gitignore`). Die Herkunftsangabe (Dateiname, Anzahl Beobachtungen, Lesezeitpunkt)
erscheint in jedem Vergleichsbericht.
