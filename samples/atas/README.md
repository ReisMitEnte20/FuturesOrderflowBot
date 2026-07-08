# samples/atas/ — Ablage für ATAS-CSV-Exporte (sekundär)

**Status: noch KEINE echten ATAS-Exporte vorhanden.** Dieser Ordner ist die Vorbereitung
(Phase 12E-A). Erst wenn hier echte, geprüfte CSVs liegen, folgt der eigentliche Import.

> **Hinweis:** Haupt-Test-Quelle ist jetzt **Sierra Chart** (siehe
> [../sierra/README.md](../sierra/README.md) und
> [../../docs/MARKET_DATA_SOURCE_GUIDE.md](../../docs/MARKET_DATA_SOURCE_GUIDE.md)). ATAS nur
> nutzen, **falls passende CSV-Exporte möglich** sind — es ist primär ein Analyse-/Charting-Tool,
> keine garantierte Exportquelle.

## Wohin die Dateien

Lege ATAS-CSV-Exporte unter **`samples/atas/raw/`** ab, z. B.:

```
samples/atas/raw/nq-ticks-2026-07-08.csv
samples/atas/raw/nq-orderflow-bars-2026-07-08.csv
samples/atas/raw/nq-footprint-2026-07-08.csv
samples/atas/raw/nq-volume-profile-2026-07-08.csv
```

## Sicherheits-/Datenschutzregeln (bitte einhalten)

- **Keine Accountdaten**, keine Kontonummern, keine Broker-Logins.
- **Keine API-Keys / Secrets / Passwörter** in den Dateien.
- **Keine privaten Daten** (Namen, E-Mails, IDs).
- Nur reine **Markt-/Orderflow-Daten** (Zeit, Preis, Volumen, Bid/Ask, Delta …).

## Erst klein anfangen

- Zuerst **kleine Samples**: Header + die ersten **5–20 Datenzeilen** reichen, um das
  Spalten-**Mapping** zu kalibrieren (siehe [../../config/import-profiles/](../../config/import-profiles)).
- Große Voll-Exporte erst, wenn das Mapping steht.

## Ablauf

1. Export aus ATAS (siehe [docs/ATAS_EXPORT_GUIDE.md](../../docs/ATAS_EXPORT_GUIDE.md)).
2. Kleine Datei unter `raw/` ablegen.
3. Mir **Header + erste ~10 Zeilen** schicken → ich passe das passende
   `config/import-profiles/atas-*.example.json` an deinen echten Header an.
4. Danach erst Import/Datenqualitätsprüfung.

> Solange keine echten, klassifizierten Daten vorliegen, meldet das System ehrlich
> `InsufficientData` und erzeugt **keine** Fake-Orderflow-Daten.
