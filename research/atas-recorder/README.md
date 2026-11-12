# atas-recorder/ – Quelltext des ATAS-Indikators „OF Replay Recorder (read-only)“

Nur-lesender ATAS-`Indicator`, der aus den SDK-Callbacks Trades, kumulierte Trades, BBO, DOM und Bars in einen
Aufnahmeordner schreibt. Keine Orders, keine Feed- oder Kontoänderungen. Gesichert am 2026-11-12 aus
`F:\ATAS_Data\Recorder` (Entwicklungsrechner); dort lag der Quelltext bis dahin außerhalb von Git.

## Ausgelieferte DLL und Quellstand sind zwei verschiedene Dinge

| | Ausgelieferte Recorder-DLL | Quellstand in diesem Ordner |
|---|---|---|
| Kennung in `meta.json` (`recorder`) | `OrderFlowReplayRecorder/proto-2-bufferfix` | `OrderFlowReplayRecorder/proto-4-recheck` |
| Datei | `recorder\OrderFlowReplayRecorder.dll` in den Collector-Paketen, 22.016 Bytes, gebaut 2026-10-30 16:59 | `OrderFlowReplayRecorder.cs`, zuletzt geändert 2026-10-30 18:24 |
| SHA-256 | `9065007A3B48950319241ECFACBF81D930D37A7EFE76052EA7C2CFEC53DE5E1D` | `808278A03D511F6E5D4F82AFBDF52CD9695B1594987F75100C1FF8B927DDC15F` |
| Aufbau | eigenständig, eigener Writer (`OFRecorderWriter`), liest kein Token | nutzt `../recorder-core` (`RecorderCore`, `CoreGate`, `TokenHandshake`), konsumiert ein Lauf-Token |
| Einsatz | in ATAS installiert; mit ihr wurde die Woche 26.–30.10.2026 aufgenommen | **nie in ATAS installiert oder für eine Aufnahme benutzt**; nur kompiliert und über die Konsolentests von `recorder-core` geprüft |

**`OrderFlowReplayRecorder.cs` ist nachweislich nicht der Quelltext der ausgelieferten DLL** (andere Kennung,
anderer Aufbau, jünger als die DLL).

### Sicherung `proto-2-bufferfix-sicherung/`

`proto-2-bufferfix-sicherung/OrderFlowReplayRecorder.cs` (SHA-256
`E2F54176831135E347DA390C3AA426E02B3CF815D2C5CE5C19AE6459D48DFD03`) ist eine Kopie des Quelltexts, die am
2026-10-30 17:37 vor den Recheck-Änderungen angelegt wurde – also 38 Minuten **nach** dem Build der ausgelieferten
DLL. Sie trägt dieselbe Kennung `proto-2-bufferfix`.

Geprüft am 2026-11-12 durch Neubau mit `build.ps1 -Variant proto2`:

- gleiche Dateigröße (22.016 Bytes),
- identische Mengen an Zeichenketten und Bezeichnern (Typen, Member, Literale) in beiden DLLs,
- **aber nicht byte-identisch** (rund 7.000 abweichende Bytes; SHA-256 des Neubaus `50C5370C…38C6A`).

Das ist mit „gleicher Quelltext“ vereinbar, **beweist ihn aber nicht**: Der ursprüngliche Compileraufruf ist nicht
überliefert, der Build ist nicht deterministisch eingestellt, und ein Vergleich des IL-Codes Methode für Methode
hat nicht stattgefunden. Die Sicherung ist deshalb als *wahrscheinlicher, nicht nachgewiesener* Quellstand der
ausgelieferten DLL zu lesen. Die DLL selbst liegt nicht im Repository, nur als Bestandteil der Release-Pakete.

## Dateien

| Datei | Inhalt |
|---|---|
| `OrderFlowReplayRecorder.cs` | aktueller Quellstand `proto-4-recheck`; braucht `../recorder-core/{RecorderCore,CoreGate,TokenHandshake}.cs` |
| `proto-2-bufferfix-sicherung/OrderFlowReplayRecorder.cs` | Sicherung `proto-2-bufferfix`, eine Datei ohne weitere Quellen |
| `build.ps1` | Build beider Varianten nach `out\` (gitignored) |
| `validate_recording.py` | Gegenprüfungen einer Aufnahme und Vergleich 1× gegen beschleunigt (nur Standardbibliothek) |

Nicht übernommen aus `F:\ATAS_Data\Recorder`: die DLLs und `.bak`-Dateien (Binaries), `build.rsp`/`build_f.rsp`
(erzeugte Compiler-Antwortdateien mit festen lokalen Pfaden; `build.ps1` erzeugt sie neu), `build.csproj` (nur
Referenzliste, mit dem .NET-8-SDK nicht baubar), `multistrat\` (eigenes Experiment mit erzeugten Ergebnisdateien)
und `HANDOFF.md` (lokale Arbeitsnotiz mit Benutzerpfaden; das Wesentliche steht hier).

## Bauen

Voraussetzungen: ein installiertes ATAS (`C:\Program Files (x86)\ATAS Platform`), die **.NET-10-Laufzeit**
inklusive Windows Desktop und ein beliebiges .NET-SDK als Quelle für den
Roslyn-Compiler. Ein .NET-10-SDK ist nicht nötig. Die ATAS-Bibliotheken werden nur referenziert, nicht kopiert.

```powershell
powershell -ExecutionPolicy Bypass -File research\atas-recorder\build.ps1
powershell -ExecutionPolicy Bypass -File research\atas-recorder\build.ps1 -Variant proto2
```

Abweichende Installationspfade: `-AtasDir`, `-DotnetRoot`, `-OutDir`. Geprüft am 2026-11-12 mit ATAS-Bibliotheken
des Entwicklungsrechners, Laufzeit 10.0.3 und Roslyn aus SDK 8.0.422: beide Varianten bauen ohne Fehler
(`current` 27.136 Bytes, `proto2` 22.016 Bytes; je eine Nullable-Warnung bei `current`). Der Build ist nicht
deterministisch – die Prüfsumme ändert sich von Lauf zu Lauf.

Installation (nicht Teil des Builds): bei **geschlossenem** ATAS die DLL nach `%APPDATA%\ATAS\Indicators\` kopieren.

## Ausgabeordner (`OutputRoot`) – bekannte Grenze

Beide Stände haben `F:\ATAS_Data\Recordings` als fest eingebauten Standard; ist das Feld „Output-Ordner“ leer,
wird dorthin geschrieben, und fehlt das Laufwerk, bricht der Recorder ab (kein Ausweichen auf `C:`).

**Das Feld in ATAS von Hand zu setzen ist kein bestätigter Weg zu einem anderen Ordner.** Die Replay-Steuerung
(`../atas-replay-probe`) fügt den Recorder je Aufnahmefenster als **neue Instanz** hinzu und entfernt ihn danach.
Der Recorder liest `OutputRoot` nur einmal beim Start der Instanz. In früheren Tests fiel der Pfad beim
Instanzwechsel auf `F:\ATAS_Data\Recordings` zurück. Ob eine in ATAS gespeicherte Indikator-Vorlage den Pfad
zuverlässig an jede neue Instanz vererbt, ist nicht nachgewiesen. Ein Test auf einem anderen Rechner muss den
tatsächlichen Schreibpfad deshalb über **mindestens zwei aufeinanderfolgende Aufnahmefenster** prüfen.

Die einzige geprüfte Konfiguration ist: Laufwerk `F:` vorhanden, Datenordner `F:\ATAS_Data`.

## Weitere bekannte Punkte (Stand der lokalen Notizen, Oktober 2026)

- `bars.ndjson` (nativer Footprint) lieferte im Replay degenerierte Bars; der Puffer-Fix in `proto-2-bufferfix`
  ist nicht verifiziert. Der Collector benutzt nur den Tick-Strom.
- DOM-Daten sind nicht validiert (einzelne Ausreißerpreise) und werden nicht als Strategieeingabe benutzt.
- Lückenlose lokale Sequenznummern und null Drops belegen nicht die Vollständigkeit beim Datenanbieter.
