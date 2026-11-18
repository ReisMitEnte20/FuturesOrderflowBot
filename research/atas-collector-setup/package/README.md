# ATAS Session Collector 1.3.9 – All-in-one-Paket (Vorabversion)

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

## Was in 1.3.9 geändert ist (Datum über Monatsgrenzen, X am Replay-Ende)

Lokaler Befund 18.11.2026 (1.3.8, Lauf 05.01.–17.11.2026): Für den 05.01. stand der Start richtig auf 05.01.2026
15:30, das Ende dreimal auf 05.11.2026 00:00; der Lauf brach vor dem Laden ab (keine Daten gespeichert).

- **Datumsdialog (Phase 6):** Am echten Dialog protokolliert: ATAS übernimmt jeden Abschnitt (Tag, Monat, Jahr, Stunde,
  Minute) sofort und hält Start < Ende ein, indem es das andere Feld mitschiebt. Ein per Strg+A/Entf geleertes Feld wird
  nach zwei Ziffern mit „Tag.aktueller Monat.Jahr 00:00“ vorbelegt – dadurch schob der Start das bereits richtige Ende
  auf den 05.11. Jetzt: Felder nicht mehr leeren, sondern überschreiben; Reihenfolge Start := Zieltag 00:00 → Ende →
  Start. Nach jedem Schreibvorgang werden beide Felder protokolliert, die Rückleseprüfung bleibt. Am echten Dialog
  geprüft: 11.11. → 05.11., November → 05.01., 05.01. → 06.01., 06.01. → 17.11., 17.11. → 02.01. (jeweils 15:30–18:00).
- **X der Replay-Leiste am Ende:** Während „Module laden…“ liegt ein Fortschrittsbalken unter den Symbolen; die
  Bildsuche hielt sein rechtes Ende für das X (kein Tooltip → kein Klick), ATAS spielte den Tag danach erneut ab. Die
  Suche gilt jetzt nur der Symbolzeile (aus den Beschriftungen „… auswählen“/„100x“), einzelne OCR-Buchstaben (das X
  wird als „x“ gelesen) gelten nicht mehr als Text. Tooltip „Schließen“ und Prüfung unter dem Zeiger bleiben.

**Testumfang:** lokal mit echtem ATAS ein Tageslauf 05.01.2026 (NewYork 09:30–12:00 ET, 100×) über die Probe bestanden:
Ladebereich laut ATAS 14:30–17:00 UTC, X einmal geklickt, Replay verlassen, 485.894 MNQ-Trades, 0 außerhalb des
Fensters, danach 0 aktive Recorder. Die neue X-Bildsuche am Fehler-Screenshot offline geprüft (im Live-Lauf bot ATAS
das X per UIA an). **Mehrtägiger Lauf über die Oberfläche und Windows-VM mit 1.3.9 noch ungeprüft.**

## Was in 1.3.8 geändert ist (Laden am Folgetag)

VM-Befund 18.11.2026 (1.3.7): Der 26.10. lief vollständig durch. Am 27.10. waren Laden und Play bereit, es entstand aber
keine Recorder-Instanz. Die Steuerung sendete wegen „Indikatoren aktiv“ Strg+Alt+R und wartete danach bis zum Ladelimit.

Lokal nachgestellt und im ATAS-Protokoll belegt: Nach dem Wechsel zurück zur Echtzeit meldet ATAS
`Reload: keeping previously captured chart state instead of snapshotting the cleared chart`. Beim nächsten Laden stellt
ATAS dann einen Chart-Stand von **vor** dem Anhängen des Recorders wieder her; der Recorder fällt vom Chart.

- **Kein Tastenkürzel mehr in Phase 7.** „Indikatoren aktiv“ ist nach dem Laden kein Beleg gegen die Replay-Ansicht;
  eine UIA-Zeitüberschreitung wird als „unbekannt“ protokolliert.
- **Recorder nach dem Laden erneut anhängen:** Ist nach dem Laden keine Recorder-Instanz aktiv, hängt die Steuerung den
  Recorder **einmal** nach dem Laden (Replay am Startpunkt, vor Play) über den bekannten Dialog an. Steht er noch in der
  Liste, wird nichts doppelt hinzugefügt. Übernommen wird nur genau eine danach neu entstandene, aktive Instanz ohne
  Trades im Replay-Bereich. Sonst klarer Abbruch mit Grund; der Recorder wird entfernt.
- Verwendet ATAS die in Schritt 3 angelegte Instanz weiter, wird sie nur übernommen, wenn sie die einzige aktive ist und
  höchstens Live-Trades enthält. Die Bridge zählt Live-Trades am Dateianfang (`liveTicksBeforeReplay`), exportiert sie
  nicht und wertet sie nicht als Loop-Rücksprung; Phase 9 wertet sie weder als Fortschritt noch als Fensterende.
- **X der Leiste:** Liegt das schwebende Leistenfenster hinter dem Hauptfenster, wird es nach vorn geholt. Findet UIA
  keine Schließen-Schaltfläche, wird das rechteste Symbol im Leistenfenster genommen, nur mit Tooltip „Schließen“ und
  Fenster-Treffertest unmittelbar vor dem Klick.
- **Recorder entfernen:** UIA-Zeilen ohne Bildschirmposition führen zur Texterkennung der Liste statt zum Abbruch. Hat ATAS
  den Recorder beim Wechsel zur Echtzeit schon selbst vom Chart genommen (Liste leer, keine aktive Instanz, Writer
  beendet), gilt das als bestätigt „kein Recorder aktiv“.
- Ist Play 45 s nach dem Laden nicht bereit, bricht die Steuerung ab, statt bis zum Ladelimit zu warten.

Lokal geprüft (ATAS 8.0.15, Vulkan): zwei Handelstage am Stück (26.–27.10.2026, 14:30–14:32 Berlin, 100×) ohne Eingriff,
28.203 und 26.956 MNQ-Trades, nur im jeweiligen UTC-Fenster, keine Doppelten, danach kein Recorder aktiv; „Fortsetzen“
überspringt beide Tage, CSV unverändert. Auf der VM noch nicht geprüft.

## Was in 1.3.7 geändert ist (Ablauf nach dem Replay-Ende)

VM-Befund 18.11.2026 (1.3.6): Chart-Erkennung, Laden und Play liefen. Nach dem erkannten Ende sendete die Steuerung
21-mal das Stopp-Kürzel. Nach dem **ersten** zeigte der Fenstertitel „Wiedergabe gestoppt“; die Bestätigung kannte aber
nur Stopp-Zeilen der Replay-Quelle und wertete den Titel nicht. Der VM-Lauf selbst enthält keine ATAS-Logzeile zum Stopp.
Aus separaten, lokalen ATAS-Logs ist bekannt, dass ATAS einen Stopp während des Ladens auch unter der Chart-Quelle
protokolliert (`MNQ 5m Chart: Replay chart state: LoadingModules -> Stopped`); diese Zeilen werden jetzt mit ausgewertet.

- **Höchstens ein Stopp-Kürzel**, nur wenn die Wiedergabe laut Fenstertitel noch läuft oder lädt. Die Wirkung wird über
  den Titel („gestoppt“) und das Protokoll geprüft, auch über Zeilen der Chart-Quelle. Kein zweites Kürzel.
- **Replay-Leiste schließen:** Die Leiste wird nach vorn geholt (eigenes Fenster, kein Klick in den Chart). Ihr X wird
  frisch ermittelt (UIA-Schaltfläche der Leiste, sonst Windows-Treffertest am Leistenfenster) und genau einmal
  geklickt. Unmittelbar davor wird geprüft, dass unter dem Mauszeiger dieses Schließen-Symbol liegt. Ist es verdeckt
  oder unklar, wird nicht geklickt; es entsteht ein Diagnosebild.
- **Wirkung prüfen:** Leiste weg, kein neuer Durchlauf, Chart wieder editierbar. Blendet das X nur die Leiste aus, steht
  genau das im Protokoll; der Tag wird dann nicht angehängt und kein weiterer Tag gestartet.
- Danach wie bisher: Recorder entfernen, „Übernehmen“, Writer prüfen, einmal an die gemeinsame CSV anhängen, nächster Tag.
- **Neuer Befehl `xdiag`** (nur lesen): findet die Replay-Leiste und ihr X und zeigt das Ziel als Bild. Kein Klick,
  keine Mausbewegung.
- Chart-Erkennung: `#` vor dem Instrumenttitel wird beim Abgleich entfernt. Ein Titel in der Titelzeile, der keinem
  Panel zuordenbar ist, zählt nicht mehr als Widerspruch.

## Was in 1.3.6 geändert ist (Chart-Erkennung in Phase 1)

Befunde: Am 17.11.2026 enthielt der UIA-Baum das Panel `CustomLayoutPanel` mit dem **Namen** „MNQ 5m Chart“ und darin
die namenlose Zeichenfläche `ChartView`. Nach einem ATAS-Neustart am 18.11. fehlten beide; an der Chart-Position standen
nur zwei namenlose, deckungsgleiche WinForms-Flächen. Der Lauf stoppte in Schritt 1.

- **Rohansicht:** Neben der UIA-Kontrollansicht wird jetzt auch die Rohansicht durchsucht (Elemente, die die
  Kontrollansicht herausfiltert); Anzahl, Fehler und Treffer stehen im Protokoll.
- **Deckungsgleiche Fenster:** gelten nur dann als *eine* Fläche, wenn sie verschachtelt sind (Host + Zeichenfläche).
  Liegen sie nebeneinander (z. B. Charts in Tabs), ist die Lage unklar und der Lauf bricht ab.
- **Instrumentbezug**, in dieser Reihenfolge: (a) UIA-Name eines umgebenden, chartgroßen Panels (z. B. „MNQ 5m Chart“);
  (b) Texterkennung **in der Fläche und in der Titelzeile darüber**. Ein Titel in der Fläche wird per Windows-Treffertest
  zugeordnet, ein Titel in der Titelzeile nur, wenn er per UIA im selben Panel wie die Fläche liegt. Gleiche Koordinaten
  allein reichen nicht. Das ATAS-Protokoll (Chart-Namen seit dem ATAS-Start) darf nicht widersprechen.
- **Nur eindeutig:** genau eine große Fläche; bei mehreren oder unklaren Flächen kein Ersatz und Abbruch. Es wird nichts
  angeklickt.
- `chartdiag` nennt das Ergebnis („GENAU EIN MNQ-CHART … woran“) und legt ein Bild der Fläche daneben.
- Zielinstrument ist `MNQ` (änderbar über die Umgebungsvariable `ATAS_TARGET_INSTRUMENT`).

## Was in 1.3.5 geändert ist (Hänger vor Play)

VM-Befund 17.11.2026: ATAS meldete das Laden als abgeschlossen („Initialized“), es entstand aber keine neue
Recorder-Instanz. Die Steuerung wartete darauf bis zu 450 s **ohne jede Ausgabe** und drückte nie Play.

- **Statuszeile alle 10 s** während des Ladens: neue Instanz, Play bereit, geladen, Chart in der Replay-Ansicht.
- **Ladeende aus dem ATAS-Protokoll** dieses Ladevorgangs; danach höchstens 15 s Nachfrist für die neue Instanz.
- **Chart folgt dem Replay nicht** (Indikatoren weiter aktiv): einmal das ATAS-Kürzel „DisableEnableMarketReplay“,
  danach unabhängig geprüft (Replay-Ansicht + neue Instanz).
- **ATAS behält die Instanz** vom Hinzufügen: wird nur übernommen, wenn sie die einzige aktive ist und noch **keine**
  Daten enthält (sonst würden Echtzeitdaten vor die Replay-Daten gemischt). Nach Play erneut geprüft.
- Sonst **sofortiger Abbruch mit Diagnose** (alle Recorder-Ordner seit Auftragsbeginn mit Zustand) statt stummem Warten.
- Wurde die Wiedergabe vor dem Play der Steuerung schon gestartet (z. B. manuell), bricht der Auftrag klar ab
  statt mit einem weiteren Klick zu pausieren.

## Was in 1.3.4 geändert ist (Stoppen nach dem Replay-Ende)

1.3.3 erkannte das Ende, blieb danach aber im Stop-/Exit-Pfad hängen, während ATAS im Auto-Rewind weiterlief. Der alte
Pfad suchte „Zur Echtzeit wechseln“ nur per UI Automation (beim custom-gerenderten Transport nicht vorhanden) und
nutzte ungebremste UIA-Suchen über alle ATAS-Fenster, die während „Module laden“ blockieren können.

- **Stoppen:** über das ATAS-Tastenkürzel **„StopMarketReplay“** (Standard Strg+Alt+D, aus der ATAS-Konfiguration
  gelesen; ein Stop, kein Umschalter). Bestätigt nur, wenn das ATAS-Protokoll danach „→ Stopped“ zeigt **und** in den
  folgenden 7 s kein neuer Durchlauf beginnt; startet ATAS doch neu, wird erneut gestoppt (höchstens dreimal).
- **Replay verlassen:** UIA-Schaltfläche, sonst per Tooltip bestätigtes Transportsymbol „Zur Echtzeit“, sonst einmal
  das Kürzel „DisableEnableMarketReplay“ – nur bei frisch bestätigtem Replay-Zustand; bestätigt über das Protokoll
  bzw. wieder aktive „Indikatoren“.
- **Begrenzt und protokolliert:** jeder Schritt nach „Wiedergabe jetzt beenden“ mit Beginn, Ende, Dauer und Ergebnis;
  UIA-Aufrufe mit Zeitgrenze; Transportzustand über den Fenstertitel (blockiert nicht). Gesamtdauer begrenzt; danach
  Abbruch mit Bildschirmfoto statt Endlosschleife.
- **Kontrolliert stoppen** (Oberfläche) wirkt jetzt sofort, auch während der Wiedergabe: Stop, Replay verlassen,
  Recorder entfernen, Writer abschließen; danach kein weiterer Tag.
- **Geschwindigkeit:** wird zusätzlich aus der Aufnahme gemessen (Marktzeit/Empfangszeit). Weicht sie mehr als 30 % vom
  angeforderten Wert ab, steht das im Protokoll.
- **Auto-Wiederholung:** ATAS speichert sie im Replay-Player als „IsLoopEnabled“. Die Steuerung schaltet sie nur aus,
  wenn ihr Zustand per UI Automation lesbar ist, und liest ihn zurück; sonst wird sie nur gemeldet und nicht verändert.
- **Neuer Befehl `stoptest`:** beobachtet 10 s den aktuellen Zustand (nur lesen), beendet dann die Wiedergabe über den
  neuen Stop-Pfad und bestätigt, dass kein weiterer Durchlauf beginnt. Keine Aufnahme, kein Laden, kein Play.

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

Einzelbefehle: `chartdiag <datei>` (nur Chart-Erkennung), `rmrec <datei>` (nur den vorhandenen Recorder entfernen),
`stoptest` (Ende-Ablauf mit dem X der Leiste an einer laufenden Wiedergabe testen; Protokoll auf dem Desktop),
`xdiag` (nur lesen: Replay-Leiste und ihr X finden, Ziel als Bild).

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
