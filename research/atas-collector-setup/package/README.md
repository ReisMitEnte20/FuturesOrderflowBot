# ATAS Session Collector 1.3.3 – All-in-one-Paket (Vorabversion)

Zeichnet ATAS-Replay-Trades lokal auf (Research/Simulation-only: keine Orders, keine Broker-Anbindung, keine
Kontoänderungen). Das Paket enthält alles außer ATAS selbst.

## Installieren oder aktualisieren

1. ZIP vollständig entpacken (nicht aus dem ZIP heraus starten).
2. **ATAS schließen**, `Setup.exe` starten, „Installieren“. Bei einem Update schlägt das Setup die bisherigen Ordner
   vor; Einstellungen, Collections und Aufnahmen bleiben erhalten.
3. ATAS starten, genau einen MNQ-Chart öffnen, Collector über die Verknüpfung „ATAS Session Collector“ starten.

Keine Downloads, kein Python, kein .NET, keine DLL-Kopien, keine Eingaben in ATAS. Keine Administratorrechte nötig.

## Kurzer Test mit eigenem Zeitraum

1. Session **„Benutzerdefiniert (Uhrzeit + Zeitzone)“** wählen.
2. Von = Bis = gewünschtes Datum, z. B. 26.10.2026; Start `14:30:00`, Ende `14:32:00`, Zeitzone `Europe/Berlin`;
   Geschwindigkeit `100x`.
3. Vorschau prüfen (Spalten Lokal, Berlin, UTC), dann **Start**.

Ergebnis: `<Datenordner>\Collections\MNQ_Custom_143000-143200_Europe-Berlin_2026-10-26_bis_2026-10-26\`
mit `trades.csv`, `collection_manifest.json`, `coverage_report.txt`, `run_protocol.txt`. Rohaufnahmen liegen unter
`<Datenordner>\Recordings\`. Uhrzeiten gelten je Kalendertag; Wochenenden werden übersprungen, einen Feiertagskalender
gibt es für benutzerdefinierte Fenster nicht. Das Ende muss am selben Tag nach dem Start liegen; Ortszeiten, die wegen
der Zeitumstellung nicht existieren oder doppelt vorkommen, werden abgelehnt.

## Ablauf je Tag

Recorder hinzufügen → Zeitraum laden → Ladebereich prüfen → abspielen → ersten vollständigen Durchlauf erkennen →
sofort stoppen → Recorder entfernen → Writer-Abschluss prüfen → Daten prüfen und an die gemeinsame CSV anhängen →
nächster Tag. Ein nicht vollständig abgeschlossener Tag stoppt den Lauf; abgeschlossene Tage bleiben erhalten,
„Fortsetzen“ überspringt sie.

## Was in 1.3.3 geändert ist

- **Zeitbezüge:** Die Soll-Grenzen werden in UTC geführt. Die Eingabe im ATAS-Datumsdialog wird aus der Zeitzone
  berechnet, die ATAS verwendet (ATAS-Einstellung bzw. Windows-Zeitzone, mit Sommerzeit). Der von ATAS bestätigte
  Ladebereich wird vor bzw. sofort bei Play gegen das Soll geprüft; bei Abweichung Abbruch ohne Veröffentlichung.
  Keine feste Berlin-Annahme, keine ±1-Stunden-Korrektur.
- **Replay-Ende:** Aus dem ATAS-Protokoll wird der Abschluss der eigenen Wiedergabe erkannt (Stopp + „Data processing
  finished“ + Auto-Rewind) und festgehalten; der anschließende Auto-Loop löscht ihn nicht. Abfrage alle 200 ms statt
  alle 3 s, danach wird die Wiedergabe sofort beendet. Das Protokoll nennt die auslösenden Originalzeilen und die Regel.
- **Fehlerklassifikation:** Nur Fehler der Replay-Quellen gelten als Replay-Fehler. Fehler- und Disconnect-Zeilen
  anderer Module (Konto, Websocket, Cache) erscheinen nur als Hinweis. Stillstands- und Gesamtzeitlimits bleiben.
- **Loop-Duplikate:** ATAS legt für den Auto-Loop eine neue Recorder-Instanz an; nur die zugeordnete Instanz wird
  angehängt. Fällt die Marktzeit innerhalb einer Aufnahme dennoch zurück, wird nur der erste Durchlauf übernommen bzw.
  bei unvollständigem erstem Durchlauf nichts veröffentlicht. Identische echte Trades werden nicht dedupliziert.
- **Recorder entfernen:** Die Kopfzahl „Hinzugefügt (N)“ ist maßgeblich. Liefert UI Automation weniger Zeilen, wird
  die Liste per Texterkennung eines frischen Bildschirmfotos gelesen; „Liste nicht lesbar“ wird nie als „leer“
  gewertet. Papierkorb nur innerhalb der Recorder-Zeile (UIA oder Symbol mit Löschen-Tooltip), danach Prüfung, dass
  genau diese Zeile fehlt, erst dann „Übernehmen“; sonst „Abbrechen“ mit Bildschirmfoto und Klickziel in `Logs`.
  Bleibt eine Instanz aktiv, nennt das Protokoll Ordner, runId und Kennung.
- **Tempo:** Feste Wartezeiten beim Hinzufügen, Dialogöffnen und Replay-Verlassen durch zustandsabhängiges Warten ersetzt.
- **Geschwindigkeit:** Kann 100× nicht zurückgelesen werden, steht im Protokoll „NICHT verifiziert“.
- **Startprotokoll:** Programmversion mit Commit und Kennung/SHA-256 der installierten Recorder-DLL.
- **Deinstallation:** `Uninstall.exe` im Programmordner und Eintrag unter „Installierte Apps“.

## Diagnose ohne Aufnahme

ATAS offen, Chart geöffnet, **nicht** in der Replay-Ansicht; andere Fenster mit privaten Inhalten vorher schließen
(das Paket enthält ein Bildschirmfoto des Hauptbildschirms):

```
"%LOCALAPPDATA%\Programs\ATAS Session Collector\AtasReplayProbe.exe" diagpack
```

Legt auf dem Desktop `ATAS-Diagnose-<Zeit>` und eine gleichnamige ZIP-Datei an: Prozesse, Fenster, DPI,
Render-Engine, Chart-Kandidaten, maskierter Auszug des ATAS-Protokolls (Replay/Chart/Fehler), UIA-Baum und Bilder des
Indikator-Dialogs (wird nur geöffnet und mit „Abbrechen“ geschlossen), Listenzustand „Hinzugefügt“,
Recorder-Metadaten der letzten Aufnahmen und die letzten Laufprotokolle. Konto-IDs und E-Mail-Adressen werden
maskiert; ATAS-Konfigurationsdateien werden nicht kopiert.

Einzelbefehle: `chartdiag <datei>` (nur Chart-Erkennung), `rmrec <datei>` (nur den vorhandenen Recorder entfernen).

## Deinstallieren

„Einstellungen → Apps → Installierte Apps → ATAS Session Collector → Deinstallieren“ oder `Uninstall.exe` im
Programmordner (ATAS und Collector vorher schließen). Entfernt werden nur die installierten Programmdateien, die
eigenen Verknüpfungen und der Registrierungseintrag. Die Recorder-DLL wird nur entfernt, wenn sie unverändert die
installierte ist; eine beim Setup gesicherte fremde Vorgänger-DLL wird zurückkopiert. **Aufnahmen, Collections und
Logs bleiben erhalten.** Fremde Dateien im Programmordner bleiben liegen.

## Was geprüft ist und was nicht

**Geprüft (Entwicklungsrechner, ohne ATAS):**
- Paket frisch entpackt (`test-package.ps1`): Installation ohne Python/.NET im Suchpfad, Pfade mit Leerzeichen und
  Umlauten, ohne Laufwerk `F:`; Fehlerfälle; erneutes Setup ohne Doppelinstallation; Datenordnerwechsel;
  Deinstallation mit Wiederherstellung der Vorgänger-DLL, Daten unverändert.
- Offline-Gegenbeispiele für die geänderte Logik: Ende/Fehler-Klassifikation mit Zeilen im Format des echten
  ATAS-Protokolls (natürliches Ende + Auto-Loop, fremde Exceptions, eigener Stopp, Echtzeitwechsel, anderer Bereich),
  Zeitzonen inkl. Sommerzeit, Listenzustand leer/nicht lesbar, OCR-Zeilen, Maskierung, Loop-Grenze in der Bridge,
  benutzerdefinierte Fenster (Sekunden, Zeitzone, Zeitumstellung, Ordnername).

**Nicht geprüft – nur mit ATAS möglich (VM):**
- Alles, was ATAS bedient: Recorder hinzufügen/entfernen im echten Dialog (inkl. Texterkennung der Liste und
  Tooltip-Prüfung des Papierkorbs), Enderkennung und sofortiger Stopp am echten Auto-Loop, Ladebereichsprüfung,
  zwei Fenster nacheinander mit Append, Resume, ATAS-Neustart, RDP-Trennung.
- Die Ursache des „Errored“ im VM-Protokoll (Abbruch bei 15:00:56 UTC) ist nicht belegt; die neue Regel und das
  Diagnosepaket zeigen künftig die auslösende Zeile.

Empfohlene Reihenfolge auf der VM: `diagpack` → `rmrec` (falls noch ein Recorder hängt) → ein Custom-Fenster von
2 Minuten → zwei Custom-Fenster an zwei Tagen → „Fortsetzen“ (muss beide überspringen).

Versionen, Prüfsummen und Herkunft der Komponenten: `VERSION-UND-HASHES.txt`. Lizenzen: `LIZENZHINWEISE.txt`.
