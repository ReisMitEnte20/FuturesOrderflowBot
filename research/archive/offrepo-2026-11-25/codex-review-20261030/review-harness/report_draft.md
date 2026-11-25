# Unabhängige Review — 30.10.2026

## Einschätzung

**Wegen konkreter Fehler nicht bereit für die behauptete Abnahme.** Weitere Review der ausdrücklich unvollständigen Arbeit ist sinnvoll; keine Freigabe als behobene A1/A3/A4-Lieferung.

| Bereich | Urteil | Implementierungs-/Nachweisstand |
|---|---|---|
| A. Recorder/Automation | Wegen konkreter Fehler nicht bereit | A1/A3/A4 nur teilweise korrekt; A2/A5 Teilimplementierung. Kein neuer End-to-End-Nachweis. |
| B. Offline-Datenbrücke | Wegen konkreter Datenintegritätsfehler nicht bereit als qualifizierter Export | Minutenbeispiel reproduzierbar, aber Zeitpräzision, Eingabestabilität und Veröffentlichung nicht abgesichert. |
| C. Unbeaufsichtigter Serienbetrieb | Nicht bereit | Eindeutige Auftragszuordnung, verlässlicher Abschluss und autonomes Add/Remove fehlen. |

Review ausschließlich offline/lesend gegenüber Produktion. Keine Fixes, ATAS-UI-Aktionen, Orders, Deployments, Installation, Netzwerktests, Commits, Pushes oder Merges.

## Geprüfter Stand

- Repository: `A:\Projects\FuturesOrderflowBot`, Branch `feature/atas-replay-control-probe`, HEAD `16f5d76b5c584763c404786365fdbf273021bf24`.
- Status vor/nach Review: `?? dashboard/dist-demo/`, `?? research/`, `?? tools/`. Tracked diff leer, die relevanten untracked Dateien wurden ausdrücklich gelesen. Recorder-Quelle liegt außerhalb Git auf F:.
- Gelesen: HANDOFF.md, CLAUDE_DELIVERY_2026-10-30.md, aktuelle AGENTS.md und relevante CLAUDE.md-Regeln; Program.cs einschließlich SelfTest, Cleanup, Zuordnung und Dateilesern; csproj; Recorder-Callbacks, Writer und Abschluss; Konverter, gesamter pytest-Testbestand, README, ausgeliefertes Manifest/Parquet, Build-Response-Datei und Evidence-Inventar. Keine tieferen AGENTS.md unter research/tools gefunden.
- Alle angegebenen Code-, Aufnahme- und Bridge-Pfade existieren. Jüngste vorhandene Aufnahmen stammen weiterhin vom alten Lauf; im geprüften Evidence-Baum kein neuer Nachweis der Änderungen. Das ist keine Behauptung über unbekannte Ablagen.
- Eigene Nachweise ausschließlich unter `F:\ATAS_Data\Codex_Review_Context\Review_cb71abd5503e4eb3b6d1e7fa8f9d5135` (nachfolgend REVIEW).

## Die fünf wichtigsten Findings

### 1. P1 — belegter Codefehler: Fremde Zeitstempel und Ende minus eine Sekunde gelten als Replay-Ende

**Datei/Funktion/Zeilen:** `A:\Projects\FuturesOrderflowBot\research\atas-replay-probe\Program.cs`, `MarketProgress` 772–777, `EndReached` 814–815, `AutoRun` 1003–1025, `FolderStreams` 2455–2466.

`MarketProgress` nimmt den größten letzten tMarket aus DOM/BBO/Cumulative/Ticks. Es prüft weder Replay-Herkunft noch Auftrag, Instrument, geladenes Intervall oder Fortschritt seit Play. `EndReached` prüft weiterhin `>= end - 1s`. Die frühere Zwe Sekunden-Toleranz wurde verkleinert, nicht beseitigt. Der Erfolgstest steht vor dem Test auf gestopptes Replay.

**Reproduziert:** Auf der frisch aus unveränderter Quelle kompilierten Probe-DLL, ausschließlich deren Dateileser/Entscheidungsfunktionen aufgerufen: historische Ticks nur bis 08:00:03 plus synthetischer Live-Cumulative-Zeitstempel vom 30.10. ergeben für das Ziel 26.10., 08:01 `EndReached=True`. Auch ein späterer synthetischer Preload-Zeitstempel 08:03 sowie exakt Ende minus eine Sekunde ergeben True. `REVIEW/ProbeChecks.cs` und `ProbeChecks_results.txt`.

Bars sind ausdrücklich **nicht** in MarketProgress enthalten: Ein bars.ndjson-Preload allein löst diesen konkreten Fehler nicht aus. DOM/BBO/Cumulative dagegen schon, sofern entsprechende Zeitstempel ankommen. Ob ATAS sie in einem bestimmten Lauf liefert, wurde nicht live behauptet oder getestet.

**Auswirkung:** Sehr kurze historische Trade-Abdeckung kann als erreicht gelten; danach helfen weder der vorhandene Tick-Nachweis noch die 15-Minuten-Startabweichungsprüfung. Ohne verfälschtes Fortschrittssignal werden Timeout/Stillstand/vorzeitiger Stopp jetzt tatsächlich abgelehnt (Zeile 1023).

**Kleinster Fix:** Nur ein dem ausgewählten Replay-Auftrag zugeordnetes, nach Play fortschreitendes Signal akzeptieren; Live-/Preload-Ereignisse ausschließen und Endtoleranz entfernen. Gewünschtes Fenster, verifizierter Replay-Fortschritt und Trade-Abdeckung getrennt belassen. Kein Trade exakt auf der Endgrenze erforderlich.

**Fix-Test:** Gemischte Live-/Replay- und Preload-Fixtures müssen trotz vorhandener historischer Ticks scheitern; zugeordneter Replay-Fortschritt bis Ende muss ohne Trade auf der Grenze bestehen. Ende−1s darf nicht bestehen.

### 2. P1 — belegter Codefehler: Cleanup wird umgangen und ignoriert den clean-Status

**Datei/Funktion/Zeilen:** dieselbe `Program.cs`, `AutoRun` 914, 983, 1027, 1047; `CleanupAfterRun` 834–865; `Main` 61–66.

Cleanup ist ein gewöhnlicher Aufruf am Methodenende, kein finally/äquivalenter garantierter Pfad. `EnsureModule`-Fehler gibt nach möglichen UI-Zustandsänderungen direkt 5 zurück. Exceptions springen in Main und liefern 2, ohne Cleanup. `folderPrev` wird erst nach erfolgreichem Fenster gesetzt: Ein bereits zugeordneter Recorder des fehlgeschlagenen Fensters wird nicht als solcher final geprüft; beim ersten Fehler bleibt lastFolder null.

Zusätzlich wird `cleanClose` berechnet, aber `finalized = hasSummary && !locked` ignoriert ihn. Auch eine alte Summary ohne clean oder eine neue mit clean=false genügt. runId wird dabei nicht verglichen. Aktive Rest-/Live-Instanzen nach Replay-Verlassen sind nur ein Hinweis und beeinflussen das Ergebnis nicht. Zwischen den Fenstern gibt es keine verbindliche Prüfung jedes vorherigen sauberen Abschlusses.

**Auswirkung:** Nichtnull-Exitcode bedeutet nicht bereinigten Zustand; umgekehrt kann ein Gesamterfolg trotz unqualifizierter Summary oder verbleibendem Recorder entstehen. Das beobachtete Release-Verhalten von ATAS wird hier nicht als live bewiesen ausgegeben.

**Kleinster Fix:** Ab erster Zustandsänderung garantierter Abschluss mit Erfassung des aktuellen und aller erzeugten Recorder; Abschluss je Auftrag/runId und clean/Writer-Ende verbindlich prüfen. Fehler/Rest-Writer müssen Gesamterfolg verhindern. Add/Remove bleibt separat unvollständig.

**Fix-Test:** Fehler unmittelbar nach Replay-Öffnung, nach Zuordnung und während Playback injizieren; Cleanup muss jedes Mal laufen. Alte, fremde oder clean=false-Summary und verbleibende Writer müssen scheitern. Diese Kontrollflussprüfung ist statisch; keine UI-Fehler wurden live injiziert.

### 3. P1 — belegter Codefehler: Recorder-Lebenszyklus bleibt nebenläufig unsicher; Flush-Fehler können clean werden

**Datei/Funktion/Zeilen:** `F:\ATAS_Data\Recorder\OrderFlowReplayRecorder.cs`, `StartIfNeeded` 95–118, `WriterLoop` 122–150, `Enqueue` 167–188, `OnDispose` 381–395, `WriteSummary` 398–437; Callbacks etwa 195–217 und 223–248.

- `_running=false` ist gleichzeitig Annahmestopp und Startbedingung. Jeder Callback ruft StartIfNeeded auf; es fehlt ein terminaler Disposed-Zustand. Ein Callback während/nach Dispose kann Queue/Thread/Ordner/ID ersetzen. Alte Dictionaries und Writer werden nicht zurückgesetzt. Bei gleicher Sekunde kann derselbe Ordner wiederverwendet werden.
- Guard, TryAdd, Zähleränderung und CompleteAdding sind nicht gemeinsam synchronisiert. Ein Producer kann vor dem Stop den Guard passieren, danach noch hinzufügen; oder nach erfolgreichem TryAdd vor `_count` pausieren. Writer-Join wartet nicht auf diese Producer, daher können Summary-Zähler hinter bereits geschriebenen Events zurückbleiben. Bei zwei Enqueues pro Trade können CSV/NDJSON am Abschluss auseinanderlaufen. Ein an CompleteAdding abgewiesenes Event wird ohne Drop-/Reject-Zähler verworfen; es war noch nicht erfolgreich akzeptiert, ist aber auch nicht bilanziert.
- Periodisches Flush (141) verschluckt Fehler ohne `_writerFaulted`. Ein später erfolgreicher Flush/Join kann daher clean=true produzieren. Beim finalen Flush-Fehler liegt Dispose im selben try und wird übersprungen: Join allein beweist dann keine geschlossenen Handles.

**Reproduziert:** `WriterHarness.cs` enthält unverändert extrahierte Produktionsfelder und Start/Enqueue/Writer/Dispose/Summary-Methoden; lediglich ATAS-Basis und Instrumentname sind lokale Stubs. Kein ATAS geladen. Ergebnis: `callback_after_dispose_restarts_writer=True`; injizierter einmaliger Flush-Fehler ergibt `summary_clean=True, writerFaulted=False`. Das belegt diese Logik, nicht ATAS' tatsächliche Callback-Reihenfolge. Die zusätzliche Zähler-Race ist aus dem Code abgeleitet, nicht zeitgesteuert reproduziert.

**Kleinster Fix:** Terminalen Lifecycle-Zustand und synchronisierten Annahme-/Zählabschluss einführen; laufende Producer vor Summary abschließen. Jeden Flush-/Schreibfehler dauerhaft merken, Handles unabhängig vom Flush-Erfolg schließen. Summary-Schreibfehler ausdrücklich melden: Der derzeitige Enqueue-Fallback (437) wird nach `_running=false` wirkungslos.

**Fix-Test:** Barrieren zwischen Guard/TryAdd/Count und Dispose; später Callback darf nicht neu starten; transienter Flush-Fehler darf nicht clean werden; finaler Flush-Fehler muss trotzdem schließen. Join-Timeout und wiederholtes Dispose prüfen.

**Was korrekt ist:** Ohne die genannten Races wird nach CompleteAdding gejoint und erst danach Summary geschrieben. Ein normaler Join-Timeout setzt joinTimedOut=true/clean=false, während Zähler weiterlaufen können; es gibt keinen Beleg, dass dieser einfache Timeout-Pfad allein clean=true erzeugt. Mehrfaches Dispose ohne Callback-Neustart kehrt früh zurück; eine Timeout-Summary wird später nicht aktualisiert. Volle Queue zählt Drops, getrennt von Writer-Fault; clean kann dabei true bleiben und bedeutet folglich keine verlustfreie Aufnahme.

### 4. P1 — belegter Codefehler: Ein einziger fremder MNQ-Recorder kann als Auftrag gelten

**Datei/Funktion/Zeilen:** `Program.cs`, `AutoRun/Cands` 942–984, `EnsureModule` 1100–1119; Recorder `StartIfNeeded` 102–104.

Der Filter verlangt neuen gesperrten Ordner, Startzeit ab **jetzt minus zehn Sekunden** und Instrument enthält MNQ. Er enthält keine Auftrags-/Chart-ID. runId wird erst aus dem gewählten Ordner gelesen und geloggt. Der Recorder nimmt eine Umgebungsvariable seines ATAS-Prozesses oder erzeugt eine GUID; autorun erzeugt/übermittelt keinen passenden vorab festgelegten Auftrag. Eine Variable im separat laufenden Steuerungsprozess würde die Umgebung des bereits laufenden ATAS nicht nachträglich ändern.

EnsureModule akzeptiert weiterhin irgendeinen aktiven Datumsbutton, andernfalls bleibt die größte VulkanChartWpf-Fläche erreichbar. Ein fremder frisch gestarteter MNQ-Chart kann daher der einzige Kandidat sein und alle Filter passieren. Das ist ein konkretes Gegenbeispiel der Filterlogik, kein live ausgelöster Fremdchart-Test. Der kleine Reflection-Test `AttributionVerdict(1)=Unique` testet nur den Zählhelfer und wird nicht als Integrationstest ausgegeben.

**Auswirkung:** Mehrdeutigkeit im Filter ist sicher abgelehnt, eindeutige Herkunft aber nicht bewiesen. Ein legitimer bereits vor der `before`-Momentaufnahme angelegter Recorder wird unabhängig von seiner runId ausgeschlossen. Sekundengenaue Ordnernamen können kollidieren; Startzeit und Dateisichtbarkeit ersetzen keine Identität.

**Kleinster Fix:** Auftrag vor dem Laden mit eindeutigem Zielchart und vom Zielrecorder bestätigtem Token verbinden; Rücklesen desselben Tokens für Events/Meta/Summary. Bis dahin keine Eindeutigkeit behaupten und unbewiesene Zuordnung ablehnen. Größte-Fläche-Fallback entfernen.

**Fix-Test:** Einziger fremder MNQ-Recorder im Zeitfenster muss abgelehnt werden; richtiger vorinitialisierter Recorder muss anhand der vereinbarten Identität erkennbar sein; zwei Kandidaten, verspätete Metadaten und Namenskollisionen prüfen.

### 5. P1 — belegte Codefehler: Die Brücke veröffentlicht keinen verlässlich verlustarmen, konsistenten Datenstand

**Datei/Funktion/Zeilen:** `A:\Projects\FuturesOrderflowBot\research\data-bridge\atas_bridge.py`, `parse_iso` 45–55; `convert` 153–217, 228–245, 297–331.

**Reproduzierte Integritätsprobleme (REVIEW/bridge_checks.py und bridge_checks.json):**

1. Python datetime schneidet die siebte Nachkommastelle der Recorder-Zeit ab; das spätere Arrow-ns-Schema kann sie nicht zurückholen. Synthetische Zeiten .0000001 und .0000002 werden beide .0000000. Bei Start .0000002 werden drei statt der korrekten zwei Trades exportiert. Das betrifft t_market und t_recv; der reale Beispielmanifest zeigt denselben Präzisionsverlust. Bei den geprüften ganzsekündigen Grenzen stimmen die 2464/27-Zahlen dennoch.
2. Eingaben werden weder auf Finalisierung noch auf stabilen Snapshot geprüft. Hashes werden **nach** Lesen/Konvertieren berechnet. Kontrollierte Änderung der synthetischen Quelle beim Hash-Aufruf: Parquet enthält einen Trade, der Manifest-Hash gehört zur nun zwei Trades umfassenden Quelle. Status bleibt complete.
3. `qualityFlags.status=complete` hängt nur von vorhandenen Fenster-Trades und Preisraster ab. Fehlende Summary/clean=null sowie eine kaputte JSON-Zeile ergeben weiterhin complete; CSV-/Summary-Diskrepanzen sind zwar einzeln sichtbar, beeinflussen diesen Status aber nicht.
4. Existierende Ziele werden ungefragt wiederverwendet und Parquet direkt überschrieben. Ein simuliertes Abbrechen während dieses Schreibens lässt eine partielle Parquet-Datei neben dem alten Manifest stehen. Alle beschädigten Testdateien liegen ausschließlich im neuen REVIEW-Verzeichnis.

**Auswirkung:** Verbraucher erhalten verlorene Zeitinformation oder falsche Fensterzuordnung; Provenienz kann eine andere Eingabe beschreiben als den Export. Ein bereits vorhandener Export kann bei Fehler widersprüchlich werden. Der Hinweis 'keine Anbieter-Vollständigkeit' ist richtig, behebt diese lokale Integritätsprobleme aber nicht.

**Kleinster Fix:** Zeitstempel/Gr enzen verlustfrei als ganzzahlige ns bzw. Recorder-Ticks parsen; stabilen Eingabestand vor/nach Konvertierung sichern und prüfen. Unqualifizierten Altbestand explizit als solchen ausweisen; Fehler/Diskrepanzen in den Exportstatus einbeziehen. In neues temporäres Ziel schreiben, validieren und atomar veröffentlichen; bestehendes Ziel standardmäßig ablehnen.

**Fix-Test:** 100-ns-Grenzen, identische Prints, Quelle verändert sich während Export, fehlende/fehlerhafte Summary, ungültige Zeile sowie Unterbrechung bei vorhandenem Ziel. Obige Fixtures müssen erwartete Ablehnung oder exakte Werte liefern.

**Weitere geprüfte Eigenschaften dieser Brücke:** NDJSON ist maßgeblich; CSV wird nur zeilenweise gezählt, es gibt keinen riskanten Join nach Zeit/Preis/Volumen. Gleiche Prints bleiben samt Reihenfolge und IDs erhalten (drei synthetische Prints, inklusive identischem Paar, bleiben drei). Aggressor wird aus direction übernommen oder Unknown. Venue/Serie bleiben unknown; t_recv ist korrekt als Empfangswanduhr dokumentiert, ohne Latenzspalte; kein Dashboard-Cap. tickSize und beobachteter Abstand sind getrennt. observedPriceGrid berechnet den kleinsten Nachbarabstand, **kein GCD** und keine Instrumentspezifikation. Für MNQ-Viertelpreise/integrale Mengen ist das reale Beispiel exakt; generell verwendet der Code float64 für Preis und int() für Volumen/OI, also keine Garantie für beliebige Decimal-Quellen. Ungültiges JSON wird gezählt und übersprungen; ungültige Feldtypen/Zeitstempel können Exceptions auslösen. Lesen ist zeilenweise, **Gesamtspeicher ist nicht begrenzt**: sämtliche Fenster-Spalten plus Preisliste, sortiertes Set und Arrow-Tabelle werden materialisiert (160–212, 66). Minutenbeispiel bestanden; lange Fenster/30-TB-Betrieb dadurch nicht qualifiziert. Batch-/Rowgroup-Verarbeitung wäre Voraussetzung vor Skalierung, kein in dieser Runde ausgeführter Lasttest.

## Welche früheren Befunde sind geschlossen?

Keiner der fünf ursprünglichen Befunde ist als Gesamtanforderung vollständig geschlossen.

- A1: Abbruch bei reachedEnd=false und getrennte Trade-/Fortschrittsausgabe implementiert; Endbeweis selbst falsch. Zwei-Sekunden-Toleranz durch eine Sekunde ersetzt.
- A2: Cleanup bei regulärem Durchlaufen und Schleifen-breaks ergänzt; Early Returns/Exceptions und Statusprüfung offen. cleanupOk beeinflusst den regulären Exitcode, ist aber zu schwach.
- A3: Grundreihenfolge Drain/Join/Summary und einfache Fault/Timeout-Flags verbessert; Nebenläufigkeit und Fehlerpfade offen.
- A4: Null/mehrere gefilterte Kandidaten werden abgelehnt; Herkunft nicht bewiesen.
- A5: Keine verifizierte autonome Recorder-Prüfung/Installation in autorun, nur spätes Scheitern bei fehlendem Kandidaten. 'Kein Doppel-Add' ist kein getesteter Add-Pfad: autorun fügt überhaupt nicht hinzu. Add/Remove ist **Funktionslücke**, nicht vollständig implementiert ohne Live-Nachweis. Custom-WPF begründet nur eine Grenze des bisherigen Selektors, keine allgemeine Unmöglichkeit.
- C:-Prüfung bleibt auf neue Ordner begrenzt; bestehende Schreibzugriffe damit unbewiesen. Main reicht seine Rückgabewerte weiter; früherer externer Shell-Wrapper ist durch diese Prüfung nicht nachgewiesen behoben.

## Ausgeführte Prüfungen und ihre Grenzen

1. Vor Ausführung Quellcode der Tests, SelfTest und statische Initialisierungen geprüft. pytest: **11 passed**, Exitcode 0. `python -B -m pytest ... -q -p no:cacheprovider --basetemp REVIEW/pytest_tmp`; Plugin-Autoload und Bytecode-Schreiben deaktiviert, TEMP/TMP auf REVIEW. Tests führen echte Konvertierungen und Parquet-Rücklesen aus. Logischer Determinismus wird mit Table.equals geprüft, nicht nur Bytegleichheit. Die Tests prüfen jedoch keine Submikrosekunden, stabile Eingangssnapshots oder atomare Veröffentlichung.
2. Recorder-Quelle mit vorhandenem csc und vorhandenen Referenzen offline neu kompiliert, ausschließlich nach REVIEW: **Exitcode 0**. Keine ATAS-DLL ausgeführt, kein Deployment.
3. Probe-Quelle ebenfalls direkt mit csc neu kompiliert. Erster manueller Build scheiterte wegen falscher WindowsBase-Referenz in der übernommenen Referenzliste; nach Korrektur ausschließlich der REVIEW-Referenzdatei: **Exitcode 0**, vier Warnungen. Das war ein Harness-Konfigurationsfehler, kein Produktions-Build-Befund. Kein Restore/Netzwerk und kein regulärer Release-csproj-Build ausgeführt. Verwendet wurden installierte .NET-10.0.3-Referenzen; das ersetzt keinen Nachweis der net8 Release-Buildkonfiguration.
4. Aus dieser Probe-DLL `selftest` einmal erfolgreich ausgeführt: **13 Checks**, Exitcode 0. Der Lieferbericht nennt an einer Stelle 12, seine aufgelisteten Gruppen ergeben 13. Alle Tests betreffen nur EndReached, AttributionVerdict und SummaryCleanClose; kein Writer-/Queue-/Dispose-, Chart- oder Cleanup-Integrationstest. Der Test Ende−1s verlangt sogar ausdrücklich Erfolg. Ein vorheriger Aufruf nach dem gescheiterten manuellen Build konnte mangels DLL nicht starten; separat geloggt, kein ausgeführter Testlauf.
5. ProbeChecks: echte neu kompilierte FolderStreams/MarketProgress/EndReached offline per Reflection; keine Verbindung zu ATAS, keine Injection. WriterHarness: unveränderte extrahierte Lifecycle-Methoden mit Stub-Basis; keine Behauptung eines vollständigen SDK-Integrationstests. Ergebnisse oben.
6. Reale Aufnahme MNQ_20261029_232000Z neu in REVIEW konvertiert: **2491 Roh-Trades, 2464 im Fenster, 27 Nachlauf, Volumen 2701, summary_clean=null**. Ergebnis logisch identisch zum gelieferten Parquet. Alle Rohdateihashes vor/nach gleich. Keine Rohmarktzeilen im Bericht.
7. Vor-/Nachher-SHA256 von **16 Dateien, 0 Änderungen**: Program.cs/csproj, Bridge-Quelle/Testdatei, Recorder-Quelle, Original-DLL und deployte DLL, sämtliche Dateien der verwendeten realen Aufnahme und beide gelieferten Bridge-Ausgaben. Einzelheiten in baseline_hashes.json/final_hashes.json. Keine übrigen Aufnahmen/Evidence wurden beschrieben; für nicht gehashte Bestände wird keine flächendeckende Hashprüfung behauptet.

## Quelle, Build und Deployment

Original-DLL auf F: und Datei im ATAS-Addon-Ladeordner sind SHA256-identisch:
`9065007A3B48950319241ECFACBF81D930D37A7EFE76052EA7C2CFEC53DE5E1D`.

Damit ist die kopierte Datei bestätigt, **nicht** die im laufenden ATAS tatsächlich aktive Implementierung. Der unabhängige direkte csc-Build hat einen anderen Hash; andere Buildoptionen/Metadaten wurden nicht auf reproduzierbare Bytegleichheit normalisiert. Daraus folgt weder Quellgleichheit noch Quellabweichung der gelieferten DLL. Keine belastbare source→build→loaded-module-Kette mit aktuellem Live-Abschluss vorhanden. Vorhandene Response-Datei ist ein Buildrezept, kein historisches Ausführungsprotokoll. Hashes in REVIEW/binary_hashes.json.

Ohne Live-Test bleiben insbesondere ATAS-Callback-Reihenfolge, realer Kaltstart, konkreter Chart-/Recorder-Bezug, Replay-Dialogzeitzone der neuen Version, autonome Entfernung und tatsächliches Writer-Ende der geladenen DLL offen. Es wurde bewusst kein neuer Lauf gestartet. DOM/Quotes und historische Vertrags-/Anbieter-Vollständigkeit bleiben unqualifiziert.

Hindsight: aktuell kein Tool verfügbar, keine Writes. CodeMunch: in dieser Runde gemäß Nutzerauftrag optional nicht genutzt; aktuelle lokale Quellen sind maßgeblich. Frühere Indexinformation wurde nicht als aktueller Codebeleg verwendet.
