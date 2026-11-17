# ATAS Session Collector 1.3.1 – All-in-one-Paket (Vorabversion)

Zeichnet ATAS-Replay-Trades lokal auf (Research/Simulation-only: keine Orders, keine Broker-Anbindung, keine
Kontoänderungen). Dieses Paket enthält alles, was der Collector braucht – außer ATAS selbst.

## In drei Schritten

1. **ZIP vollständig entpacken** (nicht aus dem ZIP heraus starten).
2. **ATAS schließen** und **`Setup.exe` starten**; Ordner bestätigen oder ändern, „Installieren“ klicken.
3. Wenn „Fertig eingerichtet“ erscheint: ATAS starten, anmelden, **genau einen MNQ-Chart** öffnen und den Collector
   über die Verknüpfung **„ATAS Session Collector“** starten.

Es sind keine Downloads, kein Python, kein .NET, keine DLL-Kopien und keine Eingaben in ATAS nötig.

## Voraussetzungen

- Windows 10/11 x64 (für die Texterkennung der Steuerung mindestens Windows 10 Build 19041).
- **ATAS ist installiert** und wurde einmal gestartet. ATAS-Lizenz und Datenanmeldung bleiben beim Benutzer und sind
  nicht Teil dieses Pakets. Fehlt ATAS, meldet das Setup das und installiert nichts.
- Keine Administratorrechte nötig (Installation pro Benutzer). Windows SmartScreen kann warnen, weil die Programme
  nicht signiert sind.

## Was das Setup tut

| Schritt | Ergebnis |
|---|---|
| Paket prüfen | Jede Installationsdatei wird gegen `payload\manifest.json` (Größe, SHA-256) geprüft. |
| ATAS erkennen | Programmordner und der Indikator-Ordner des angemeldeten Benutzers (`%APPDATA%\ATAS\Indicators`). |
| Ordner | Vorschlag Programm: `%LOCALAPPDATA%\Programs\ATAS Session Collector`; Daten: `%USERPROFILE%\ATAS Collector Daten`. Beides änderbar, kein bestimmter Laufwerksbuchstabe nötig. |
| Programme | Oberfläche, Aufnahme-Steuerung, Bridge und private Python-Laufzeit (mit pyarrow) in den Programmordner. |
| Gemeinsamer Datenordner | Oberfläche, Steuerung, Bridge **und Recorder** werden auf denselben Datenordner eingestellt. |
| Recorder | `OrderFlowReplayRecorder.dll` in den ATAS-Indikator-Ordner; eine vorhandene DLL wird vorher nach `%APPDATA%\AtasSessionCollector\recorder-backup\` gesichert. |
| Verknüpfungen | Startmenü und Desktop. |
| Abschlussprüfung | Dateivollständigkeit, Python-Laufzeit, alle Bridge-Importe, Start von Oberfläche und Steuerung, Schreibrechte, Recorder-Konfiguration. |

Schlägt irgendetwas fehl, zeigt das Setup die genaue Meldung rot an und meldet **nicht** „Fertig eingerichtet“.

**Erneut ausführen / aktualisieren:** Das Setup erkennt die vorhandene Installation, schlägt dieselben Ordner vor und
ersetzt nur geänderte Programmdateien. Einstellungen, Collections und Aufnahmen bleiben erhalten; es wird nichts
gelöscht. Den Datenordner kann man so auch nachträglich ändern (alte Daten bleiben am alten Ort liegen).

## Wie der Recorder seinen Ordner bekommt

Frühere Pakete verlangten, den Ausgabeordner in ATAS von Hand zu setzen; beim Hinzufügen einer neuen Recorder-Instanz
fiel er trotzdem auf `F:\ATAS_Data\Recordings` zurück. Das ist in diesem Paket anders gelöst:

- Setup und Oberfläche schreiben den Ordner in **eine** Datei: `%APPDATA%\AtasSessionCollector\recorder-output-root.txt`.
- Der mitgelieferte Recorder (`proto-2c-sharedroot`) liest diese Datei bei **jedem Start einer Instanz**, bevor er die
  erste Datei anlegt. Sie gilt damit auch nach Entfernen/Hinzufügen, erneutem Laden eines Replays und ATAS-Neustart.
- Der Recorder hat keinen eingebauten Standardpfad mehr. Fehlt die Datei oder das Laufwerk, bricht er mit einer
  klaren Meldung ab, statt woandershin zu schreiben.
- Vor jeder ATAS-Aktion prüft die Steuerung, dass Bridge und Python starten, dass der installierte Recorder diese
  Datei unterstützt und dass sie auf den Ordner des Laufs zeigt. Sonst bricht sie ab, ohne ATAS anzufassen.

## Wie der Zielchart erkannt wird (neu in 1.3.1)

1.3.0 brach auf einer VM mit „VulkanChartWpf-Treffer=0 / Zielchart nicht eindeutig identifizierbar“ ab: Die Erkennung
verlangte genau eine UI-Automation-Fläche mit dem Namen `VulkanChartWpf`, also den Namen der Vulkan-Renderfläche von
ATAS. Ab 1.3.1:

- Zuerst wie bisher die Vulkan-Fläche; gibt es keine, zählt eine sichtbare Fläche, deren Name, Klasse oder
  AutomationId sie als Chart-Renderfläche ausweist (z. B. bei einer anderen Render-Engine). Bedienelemente wie
  Chart-Trader, Symbolleisten oder Reiter zählen nicht.
- Weiter geht es nur bei **genau einem** sichtbaren Chart. Keine Auswahl nach Größe oder Reihenfolge.
- Schlägt die Erkennung fehl, steht im Protokoll, warum: kein ATAS-Prozess, ATAS-Fenster für UI Automation
  unsichtbar (z. B. ATAS als Administrator gestartet), kein Chart, mehrere Charts oder Chart minimiert/verdeckt –
  dazu die vollständige Diagnose. Es wird kein Recorder angehängt.

**Diagnose ohne Aufnahme** (ATAS offen, Chart geöffnet; liest nur, klickt nichts):

```
"%LOCALAPPDATA%\Programs\ATAS Session Collector\AtasReplayProbe.exe" chartdiag "%USERPROFILE%\Desktop\chartdiag.txt"
```

Die Datei `chartdiag.txt` auf dem Desktop enthält Prozesse, Fenster, Sitzung und Rechte, Render-Engine laut
ATAS-Protokoll, alle Chart-Kandidaten mit Name/Klasse/AutomationId/Sichtbarkeit sowie die tatsächlich installierte
Recorder-DLL (Kennung und SHA-256). Sie enthält keine Zugangsdaten.

## Was geprüft ist und was nicht

**Geprüft** (Entwicklungsrechner, mit dem frisch entpackten ZIP, Skript `test-package.ps1` im Repository):

- Installation ohne Python und ohne .NET im Suchpfad, in Programm- und Datenordner mit Leerzeichen und Umlauten, ohne Laufwerk `F:`.
- Fehlendes ATAS, unvollständiges Paket und nicht vorhandenes Laufwerk führen zu einem Abbruch mit genauer Meldung.
- Zweiter Setup-Lauf: keine doppelte Installation, bestehende Collections/Aufnahmen unverändert; Wechsel des Datenordners.
- Sicherung einer vorhandenen älteren Recorder-DLL.
- Die installierte Bridge hat mit der privaten Python-Laufzeit zwei echte Aufnahmefenster (26. und 27.10.2026,
  1.835.381 Trades) in eine neue Collection übernommen; Zeilen und Volumen stimmen exakt mit der Referenz überein,
  ein wiederholter Auftrag wird übersprungen (kein doppeltes Anhängen), die Originalaufnahmen sind unverändert.
- Die Pfadlogik des Recorders (neue Instanz mit leerem Feld, Instanzwechsel, veraltetes Feld, fehlendes Laufwerk)
  als Offline-Tests.

**Nicht geprüft – bitte auf der Test-VM prüfen:**

- **Der neue Recorder ist noch nie in ATAS gelaufen.** Dass ATAS ihn lädt und dass er tatsächlich in den gewählten
  Ordner schreibt, ist nur über Tests außerhalb von ATAS belegt. Gleiches gilt für Entfernen/Hinzufügen,
  Replay-Neuladen und ATAS-Neustart.
- Kein Aufnahmelauf mit Version 1.3.x. Der Aufnahmeablauf selbst ist gegenüber 1.2.x unverändert, aber die
  Kombination ist ungetestet.
- Betrieb in einer VM, RDP-Trennung, gesperrter Bildschirm, andere Auflösung/Skalierung.
- Das automatische Entfernen des Recorders nach einem frühen Abbruch ist neu und nur außerhalb von ATAS übersetzt, nicht live erprobt.
- Die neue Chart-Erkennung ist mit synthetischen Fällen getestet, aber nicht mit dem UI-Baum der VM. Ob die VM-Ursache
  ein anderer Renderer-Name ist, ist eine Vermutung; die Diagnose oben zeigt es.

**Bekannte, hier nicht behobene Punkte aus früheren VM-Läufen** (dafür lagen keine Protokolle vor):

- „Replay-Öffner nicht erkannt“: Die Steuerung prüft den Replay-Knopf jetzt, bevor sie einen Recorder anhängt, findet
  ihn aber auf demselben Weg wie bisher.
- Widerspruch zwischen erkanntem Replay-Ende und gespeichertem Zeitbereich: unverändert.
- Die Steuerung erwartet die deutsche ATAS-Oberfläche; nur MNQ ist getestet.

### Empfohlener VM-Test

Zwei Handelstage in einem Lauf aufnehmen (zwei Aufnahmefenster). Danach prüfen:

1. Im Laufprotokoll steht je Fenster eine Zeile `[3] Schreibpfad: …` mit dem gewählten Datenordner und
   `recorder='OrderFlowReplayRecorder/proto-2c-sharedroot'`.
2. Beide Aufnahmeordner liegen unter `<Datenordner>\Recordings`, beide Tage enden als „committed“, und in ATAS ist
   danach kein Recorder mehr am Chart.
3. „Fortsetzen“ mit demselben Bereich nimmt nichts erneut auf.
4. ATAS neu starten, einen weiteren Tag aufnehmen: wieder derselbe Schreibpfad.
5. RDP-Trennung getrennt davon prüfen; bis dahin nicht unbeaufsichtigt laufen lassen.

## Bedienung

- **Start** nimmt alle geplanten Tage auf. **Fortsetzen** überspringt bereits abgeschlossene Tage.
- **Kontrolliert stoppen** bricht an der nächsten Tagesgrenze sauber ab.
- **ALLES BEENDEN (Not-Aus)** beendet das Programm und seine Hintergrundprozesse sofort; ATAS bleibt offen.
- ATAS während eines Laufs nicht bedienen. Der Replay-Transport sollte im Modus „Datum auswählen“ stehen.

## Entfernen

Es gibt kein Deinstallationsprogramm. Zum Entfernen: Programmordner und Verknüpfungen löschen,
`%APPDATA%\ATAS\Indicators\OrderFlowReplayRecorder.dll` bei geschlossenem ATAS löschen (oder die Sicherung
zurückkopieren) und `%APPDATA%\AtasSessionCollector` löschen. Der Datenordner bleibt bestehen.

## Rückmeldung nach dem Test

Hilfreich sind: Windows- und ATAS-Version, ATAS-Sprache, Auflösung/Skalierung, das Setup-Protokoll (im Setup-Fenster
markieren und kopieren), aus dem Datenordner `Logs\` und aus der Collection `run_protocol.txt` und
`coverage_report.txt`. Bitte keine `trades.csv` und keine Zugangsdaten schicken.

Versionen, Prüfsummen und Herkunft der Komponenten: `VERSION-UND-HASHES.txt`. Lizenzen: `LIZENZHINWEISE.txt`.
