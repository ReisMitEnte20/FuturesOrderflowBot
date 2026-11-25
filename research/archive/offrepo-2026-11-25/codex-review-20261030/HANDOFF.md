Erstelle F:\ATAS_Data\Codex_Review_Context und speichere den folgenden
Projektkontext dort als HANDOFF.md. Bestehende Dateien nicht überschreiben.

Deine Rolle:
Du bist des Nutzers unabhängiger Reviewer. Claude implementiert lokal.
Prüfe tatsächlichen Code und Nachweise, nicht nur Claudes Berichte.
Zunächst ausschließlich lesen; keine Codeänderungen, ATAS-Bedienung,
Aufnahmen, Commits, Pushes oder Merges.

PROJEKT UND PFADE
- Repository: ReisMitEnte20/FuturesOrderflowBot
- Lokal: A:\Projects\FuturesOrderflowBot
- Automatisierung: research\atas-replay-probe\
  insbesondere Program.cs und AtasReplayProbe.csproj
- Recorder: F:\ATAS_Data\Recorder\OrderFlowReplayRecorder.cs
- Aufnahmen: F:\ATAS_Data\Recordings
- Nachweise: F:\ATAS_Data\Evidence
- Letzter berichteter Branch: feature/atas-replay-control-probe.
  Frischen Git-Status prüfen; aktueller Quelltext ist maßgeblich.
- Automatisierung war zuletzt uncommittet/untracked.

ZIEL
ATAS-Replay soll über ein eigenständig laufendes C#-Programm automatisch
historische Abschnitte aufnehmen. Keine KI-Entscheidung zwischen jedem
Klick und keine endlosen Diagnose-/Reload-Schleifen.
Später Betrieb auf einer Windows-VM eines Linux-Servers mit ca. 30 TB.
Jetzt nur lokale Automatisierung und unabhängige Review.

Geplante Zeitfenster, ausdrücklich deutsche Zeit Europe/Berlin:
- 09:00–12:00
- 15:30–18:00
- zunächst 26. und 27. Oktober 2026.
Zeitzonen je Datum korrekt umrechnen; ATAS-Dialogzeit separat bestätigen.
Noch keine großen Serien oder Serverinstallation starten.

TECHNISCHER ANSATZ
Installierte ATAS-Version laut Bericht: 8.0.15.306.
Keine dokumentierte öffentliche Replay-Steuerungs-API nachgewiesen.
Private Host-Interfaces wurden gefunden, sind aber kein freigegebener
Integrationsweg. Keine Injection oder Reflection in ATAS' DI-System.
Vorhandene Lösung verwendet Windows UI Automation plus nötige,
frisch lokalisierte Maus-/Tastatureingaben.

Erwarteter Ablauf:
1. Zielchart eindeutig erkennen.
2. Replay verlassen, wenn für Indikatoränderungen erforderlich.
   Auch pausierter Replay kann den Indikator-Dialog sperren.
3. Recorder prüfen; wenn fehlend:
   Katalogzeile selektieren/aktivieren, dann doppelklicken,
   in „Hinzugefügt“ verifizieren und übernehmen.
4. Replay öffnen, Zielmodul wählen, Datumauswahl aktivieren.
5. Zeitraum setzen und zurücklesen; tatsächliche Bereitschaft abwarten.
6. Recorder-Ordner eindeutig dem Lauf zuordnen.
7. Geschwindigkeit setzen, Play, Marktzeit und Tick-Zuwachs beobachten.
8. Am Zielende pausieren, Replay kontrolliert verlassen,
   Recorder entfernen/übernehmen, summary.json und Writer-Ende prüfen.
9. Erst danach nächstes Zeitfenster.

Wichtige UI-Erkenntnisse aus Berichten, am Code zu prüfen:
- Replay-Controls können unter anderen ATAS-Fenstern angedockt sein.
  Nicht ausschließlich im Fenster mit Titel „Replay“ suchen.
- Suche auf ATAS-Prozess und richtigen Chart begrenzen.
- PlayButton nach Zustandswechseln frisch auflösen.
- „Modul auswählen“ kann erst einen Aktivierungsklick benötigen;
  anschließend Zielchart auswählen.
- Auswahlmodus kann „Bar auswählen“ statt „Datum auswählen“ sein.
- Datum-Menüeintrag wurde als CheckBox mit InvokePattern gefunden,
  AutomationId BarCheckItemLink2.
- Datumseingabe hatte eine Maske ddMMyyyyHHmm;
  ValuePattern allein übernahm Werte nicht zuverlässig.
- Feste Bildschirmkoordinaten sind kein robuster Dauermechanismus.
- Keine Trading-Buttons bedienen; „Schließen“ kann Position schließen.
- Benutzer hat bei früheren Versuchen parallel die Oberfläche verändert.
  Deshalb frühere Ursachenbehauptungen nicht ungeprüft übernehmen.

LETZTER BERICHTETER ERFOLG – NOCH NICHT UNABHÄNGIG VERIFIZIERT
„autorun“ v10 lief in 197 Sekunden:
27 s UI, 46 s Laden, 124 s Wiedergabe.
Zwei Ein-Minuten-Fenster am 26.10.2026:
- MNQ_20261029_232000Z:
  2491 Ticks, Marktzeit 08:00:00.003–08:01:00.584 UTC.
- MNQ_20261029_232135Z:
  8795 Ticks, Marktzeit 14:30:00.004–14:31:00.626 UTC.
Berichtet: Tick-CSV/NDJSON/Summary-Zahlen stimmen, keine unbekannten
Aggressoren, keine Drops, Preise im 0,25-Raster, Summary geschrieben.

WICHTIGE OFFENE REVIEW-PUNKTE
1. Erfolg war aus vorbereitetem Zustand:
   Recorder bereits angehängt, Replay auf Datumauswahl eingestellt.
   Vollständiger Kaltstart ist damit nicht bewiesen.
2. v10 lädt offenbar bis Zielende + 3 Minuten und pausiert anhand
   der Marktzeit. Beide Aufnahmen enthalten ca. 0,6 s Nachlauf.
   Rohdaten erhalten; das gewünschte Fenster muss für spätere Nutzung
   eindeutig als [Start, Ende) abgegrenzt sein.
3. C:-Prüfung verglich offenbar nur neu angelegte Ordner.
   Das erkennt keine weiteren Schreibzugriffe in vorhandenen Ordnern.
4. Shell-Wrapper meldete früher Exitcode 0 trotz internem Fehlercode 5.
   Fehler müssen bis zum aufrufenden Prozess durchgereicht werden.
5. Dateisperre oder neuester Ordner allein beweisen weder Chart-Zuordnung
   noch erfolgreiche Aufnahme. Keine alten Summaries als neuen Erfolg.
6. Zustandswechsel können Recorder-Instanzen neu erzeugen.
   Keine unbeabsichtigten Live-/Hintergrund-Writer hinterlassen.
7. Zeitbegrenzte Wartebedingungen und begrenzte Wiederholungen prüfen.
   Kein blindes Klicken oder unbegrenztes Wiederholen.
8. „Keine lokalen Drops“ beweist keine vollständigen Anbieterdaten.
   DOM-Daten sind noch nicht fachlich validiert.
9. Sichtbares #MNQZ6-Label beweist nicht die historische Quellserie.

SPEICHERREGEL
Neue Projektdaten ausschließlich auf F:, Repository bleibt A:.
Kein stiller C:-Fallback.
ATAS-Installation und notwendiger Addon-Ladeordner in AppData auf C:
dürfen verbleiben; nicht die gesamte Installation/AppData verschieben.

Frühere Aufnahmen/Recorder/Nachweise wurden laut Claude nach Hashprüfung
von C: nach F: migriert. Später wurde noch ein alter C:-Ordner gemeldet:
...\ATAS_OF_Recordings\MNQ_20261029_223429Z.
Nicht ungeprüft löschen oder verschieben. Erst tatsächlichen Zustand lesen.

BEREITS ABGESCHLOSSEN – NICHT NEU AUFROLLEN
- React auf :5899 ist die einzige UI.
- .NET auf :5034 ist API-only; alte Blazor-UI entfernt.
- PR #16 wurde laut Bericht mit Merge 16f5d76b abgeschlossen.
- Bestehenden Backtest-/Orderflow-Pfad wiederverwenden.
- ATAS-Daten bevorzugt; keine neue Sierra-Runde.
- Keine neue Engine, Strategien oder Dashboard-Ausbaurunde.

ARBEITSREGELN
- Hindsight nur lesend, wenn vorhanden. Keine retain/sync_retain-Writes.
- CodeMunch optional, aktueller lokaler Code ist maßgeblich.
- Keine Geheimnisse oder Rohmarktdaten in Git/Review-Berichte übernehmen.
- Bestehende Änderungen und Aufnahmen erhalten.
- Nicht parallel mit Claude dieselben Dateien bearbeiten.
- Keine Wiederholung großer Testreihen ohne konkreten Anlass.

DEIN ERSTER AUFTRAG
Nachdem HANDOFF.md gespeichert ist:
1. Prüfe die genannten Pfade sowie Git-Branch/Status lesend.
2. Lies tatsächlichen Automatisierungs- und Recorder-Code.
3. Lies gezielt den jüngsten erfolgreichen autorun-Nachweis.
4. Berichte maximal fünf konkrete, priorisierte Befunde mit Datei und
   Zeile, Auswirkung und kleinstem sinnvollen Fix.
5. Trenne belegte Fehler, fehlende Nachweise und optionale Verbesserungen.
   Wenn kein relevanter Fehler gefunden wird, sage das ausdrücklich.
Keine Änderungen oder neuen Replay-Läufe starten.