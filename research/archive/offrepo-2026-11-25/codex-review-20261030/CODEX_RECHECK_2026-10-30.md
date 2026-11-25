# Codex-Nachprüfung — 30.10.2026

**Ergebnis: Die fünf Findings sind nicht vollständig geschlossen.** Die Korrekturen sind wirksam, aber weiterhin nur ein ausdrücklich unvollständiger Zwischenstand. Keine Merge- oder Live-Freigabe.

| Finding | Stand | Konkreter Beleg | Restbedarf |
|---|---|---|---|
| 1 Enderkennung | teilweise | Ende−1s, heutiger Live-Cumulative und ferner Preload werden abgelehnt; verspäteter Preload innerhalb des Filters besteht weiterhin | Herkunft des Fortschritts und verlässliche Grenzüberschreitung |
| 2 Cleanup | teilweise | UI-Aktionen innerhalb try/finally; alle zugeordneten Ordner werden mit clean/runId/Sperre geprüft; Rest-Sperren verhindern Erfolg | Fehlerfestigkeit des Cleanup selbst, reale Instanz-/Handle-Prüfung; Add/Remove fehlt |
| 3 Writer/Lifecycle | teilweise | Gemeinsamer Produktionskern, 7/7 Tests; periodischer Flush-Fehler jetzt dauerhaft faulted | Producer-Barriere-Timeout nicht als Fehler behandelt; Indicator-Dispose vor Core-Erzeugung nicht terminal |
| 4 Chart-Zuordnung | offen als Herkunftsnachweis | Token-Vergleich funktioniert, aber zwei Recorder lesen denselben globalen Token; im Test sogar gleicher Aufnahmeordner | Chartgebundener, exklusiver Handshake und unabhängige Instanz-ID |
| 5 Exportintegrität | teilweise | ns/Offset/Grenztests bestanden; Legacy korrekt check; Änderungen während NDJSON-Lesen erkannt | Snapshot über alle verwendeten Bytes; atomare Einheit aus Parquet+Manifest und Konkurrenzschutz |

## Prüfstand und Grenzen

Branch `feature/atas-replay-control-probe`, HEAD `16f5d76b5c584763c404786365fdbf273021bf24`; unverändert untracked: `research/`, `tools/`, `dashboard/dist-demo/`. Tracked diff leer, untracked Quellen und externer Recorder ausdrücklich geprüft.

Arbeitsgrundlage: CODEX_REVIEW_2026-10-30.md und CLAUDE_FIXES_2026-10-30.md vollständig, aktuelle AGENTS.md/relevante CLAUDE.md-Regeln, referenzierte Baseline-Skripte/-Ergebnisse und After-Logs, Build-Response-Datei, aktuelle relevante Quellfunktionen, vollständige Tests, reale Manifest-/Parquet-Nachweise. Keine neuen Erkenntnisse aus einem alten CodeMunch-Index abgeleitet.

Pfadkürzel im Bericht:
- **P** = `A:\Projects\FuturesOrderflowBot\research\atas-replay-probe\Program.cs`
- **C** = `A:\Projects\FuturesOrderflowBot\research\recorder-core\RecorderCore.cs`
- **I** = `F:\ATAS_Data\Recorder\OrderFlowReplayRecorder.cs`
- **B** = `A:\Projects\FuturesOrderflowBot\research\data-bridge\atas_bridge.py`
- **R** = `F:\ATAS_Data\Codex_Review_Context\Recheck_79cba668b4f44ce38e5d2d026f7412f3`

Ausschließlich Offline-Nachprüfung. Keine Quelländerung, ATAS-Aktion, Aufnahme, Deployment, Installation, Order, Commit, Push, Merge oder Änderung vorhandener Evidence. Alle eigenen Testdateien und Ausgaben liegen unter R.

## Höchstens fünf wichtigste verbleibende Befunde

### 1. P1 — belegter Codefehler: Zeitnähe und Baseline beweisen keine abgespielte Strecke

**P, ReplayProgress/GenuineEndReached, Zeilen 775–799; AutoRun 1125–1139.**

Der Filter akzeptiert letzte Stream-Zeiten in `[Start−5s, Ende+5s]`. GenuineEndReached verwirft nur eine bereits am Ende liegende Baseline; eine null-Baseline wird akzeptiert. Die tatsächliche Baseline entsteht erst **2,5 Sekunden nach Invoke(Play)**, nicht vor Play und nicht an einem bestätigten Replay-Zustandsübergang. Weder Sequenz-/Byteposition noch Replay-/Preload-Herkunft werden gebunden. Ende wird weiterhin vor dem Replay-Stopp-Zustand geprüft.

**Offline reproduziert mit echten neu kompilierten FolderStreams/ReplayProgress/GenuineEndReached:** Baseline aus Ticks bis 08:00:03, danach verspätetes BBO mit 08:01:00 → reached=True; dasselbe bei null-Baseline → True. Das kann Preload ebenso wie echten Fortschritt repräsentieren: der Code unterscheidet es nicht. **Ob ATAS genau diesen Preload-Ablauf liefert, ist eine offene Laufzeitfrage, kein behaupteter Live-Vorfall.**

Umgekehrt: erster verwertbarer Fortschritt 08:01:06 → reached=False, obwohl die Grenze überschritten ist. Weil nur die letzte Zeile jedes Streams zählt, kann der Algorithmus nach einer Poll-Verzögerung auch einen zuvor vorhandenen Endbeleg verlieren. Ein zu frühes Endsignal in der Baseline verhindert späteren Erfolg dauerhaft. `R/ProgressChecks.cs`, `progress_extra.txt`.

**Kleinster Fix:** Fortschritt an bestätigten Auftrag/Replay-Zustand und nach Play entstandene Sequenzposition binden; Preload explizit ausschließen. Eine belegte Grenzüberschreitung behalten, statt sie nach Ende+5s wieder zu verwerfen. Fehlende Baseline darf kein automatischer Herkunftsnachweis sein.

**Nachweis:** verzögerten Preload, null-Baseline, Endüberschreitung +6s und verzögertes Polling testen; danach kurze reale Verifikation der Herkunft. Kein Trade exakt auf der Grenze nötig. Ohne falsches Endsignal führen die bestehenden Timeout/Stall/Stopp-Pfade nun nachweislich im Code zu ABBRUCH und Nichtnull-Exitcode.

### 2. P2 — belegter Codefehler / unzureichende Ablaufabdeckung: Cleanup kann selbst abbrechen

**P, AutoRun finally 1179–1187; CleanupAfterRun 945–977; Main-Exceptionbehandlung.**

Die bisherige Kontrollflusslücke ist wesentlich korrigiert: ab erster UI-Aktion gilt try/finally; auch EnsureModule-Abbruch und Exceptions erreichen den Cleanup-Aufruf. Der Return vor try betrifft nur die F:-Vorbedingung, also keine zuvor ausgeführte UI-Aktion. Ein zugeordneter Ordner wird jetzt sofort registriert (1111), nicht erst nach erfolgreichem Fenster.

Allerdings ist `CleanupAfterRun(...)` innerhalb finally nicht geschützt. Beispielsweise kann Directory.GetDirectories in Zeile 975 eine IOException/UnauthorizedAccessException auslösen. Das überspringt die restlichen Abschluss-/Statusausgaben und ersetzt den vorgesehenen Abort-Code durch Main-Fehlercode 2. Eine frühere Cleanup-Exception kann zudem die Prüfung weiterer Ordner verhindern. **Kein falscher Exitcode 0 aus diesem konkreten Pfad**, aber der vollständige Abschluss ist nicht garantiert; der ursprüngliche Fehler wird nicht strukturiert zusammen mit dem Cleanup-Fehler zurückgegeben. Statisch belegt, nicht durch UI-Aufruf getestet.

Die sechs A2-Checks übergeben vorbereitete bool/string-Werte an RecorderFinalizedClean; sie rufen weder AutoRun noch CleanupAfterRun auf. Ein globaler meta-Sperren-Scan verwechselt fremde Instanzen nicht mit dem registrierten runId-Auftrag, kann aber wegen fremder Writer den eigenen Lauf konservativ ablehnen. Er beweist nicht, dass keine neue Instanz ohne geschriebenes meta.json aktiv ist oder kurz nach dem Scan startet. Recorder-Entfernung fehlt weiterhin.

**Kleinster Fix:** Cleanup-Schritte unabhängig absichern, Primärfehler und Cleanup-Fehler erhalten, alle registrierten Instanzen prüfen; Fehlschlag stets Nichtnull. Instanzabschluss über tatsächliche Recorder-Lebensdauer bestätigen, nicht allein über eine momentane meta-Sperre.

**Nachweis:** Offline-Ablauftests mit ersetzbaren UI-/Dateisystemzugriffen: Fehler vor/nach Zuordnung sowie Fehler mitten im Cleanup; alle übrigen Abschlussschritte müssen versucht und Fehler eindeutig gemeldet werden. Reale Add/Remove-/Writer-Ende-Evidenz fehlt separat.

### 3. P1 — belegter Codefehler: Producer-Timeout kann clean werden; Indicator ist vor erstem Callback nicht terminal

**C, Finish 220–243 (insbesondere 231, 241); I, Core 71–81 und OnDispose 278–281.**

Finish wartet maximal BarrierMs auf `_inFlight`, prüft nach Ablauf aber nicht, ob Producer verblieben sind. Es schließt die Queue und berechnet clean allein aus Writer-Join und WriterFaulted. Ein Producer kann nach erfolgreichem TryAdd vor Zähleraktualisierung/Decrement angehalten sein; der Writer kann fertig werden, während Producer-/Summary-Zähler noch nicht abgeschlossen sind.

**Gezielte Fault-Injection am unveränderten Produktionskern:** `_inFlight=1` modelliert einen angehaltenen Enqueue-Producer, BarrierMs auf 30 ms verkürzt; echtes Finish schreibt `clean=True`, obwohl `_inFlight` noch 1 ist. Das testet den konkreten Timeout-Zweig, nicht einen zufällig im Scheduler beobachteten Race. Der 500-Events-Test ist vollständig sequenziell und deckt diese Barriere nicht ab.

Zusätzlich setzt I.OnDispose bei `_core==null` keinen terminalen Indicator-Zustand. Ein späterer erster Callback erzeugt über Core() einen neuen Running-Kern. **Reproduziert** mit unverändert extrahierten Core/ReadRunToken/OnDispose-Methoden des Indicators, Stub-Basis und echtem RecorderCore: Dispose vor erstem Callback → later_running=True. Keine ATAS-SDK-Laufzeit simuliert oder geladen. Reale Wahrscheinlichkeit solcher Callback-Reihenfolgen bleibt offen.

**Kleinster Fix:** Barriere-Timeout als eigenen fehlgeschlagenen Abschluss behandeln, niemals clean ausgeben, solange angenommene Producer nicht abgeschlossen sind; Abschluss/Count synchronisieren. Im Indicator Dispose und erstmalige Core-Erzeugung durch denselben terminalen Zustand/Gate koordinieren.

**Nachweis:** echte Producer-Barrieren um TryAdd/Count/Dispose, inklusive Timeout; Dispose vor erstem Callback und während Initialisierung. `R/CoreChecks.cs`, `core_extra.txt` dokumentieren die begrenzte Reproduktion.

**Tatsächlich verbessert:** kein Restart eines bereits existierenden fertiggestellten Kerns; dauerhaftes WriterFault bei transientem Flush; getrenntes Flush/Dispose; normaler Writer-Join-Timeout ist clean=false; wiederholtes Finish des fertigen Kerns sicher. Ein zusätzlicher Test wartete ausdrücklich auf einen **periodischen** Flush-Fehler und bestätigte clean=False/faulted=True. Der neue ausgelieferte Transient-Test ruft sofort Finish auf und kann stattdessen nur den finalen Flush treffen. Mehrfaches Finish während Disposing kehrt sofort zurück und ist kein weiterer Abschluss-Wait. Rejected nach fertiger Summary erhöhen nur den In-Memory-Zähler; Test 7 prüft genau diesen, keine nachträgliche Summary-Aktualisierung. Summary-Schreibfehler werden jetzt über Property/Marker statt totem Enqueue-Fallback gemeldet; ein eigener Summary-Fehlertest fehlt.

### 4. P1 — belegter Codefehler: Token ist global mehrfach lesbar und nicht an den Chart gebunden

**P, AutoRun 1010, 1066–1084, 1158, 1182; EnsureModule 1237–1266. I, ReadRunToken 87–100. C, Start 103–118.**

Die Datei `<Recordings>\.of_run_token` enthält nur den Token-String. Jeder neue Recorder mit demselben OutputRoot liest sie unverändert; es gibt keinen exklusiven Konsum, keine Zielchart-ID, kein Ablaufdatum und keine Eigentümersicherung. Der Token bleibt bis Fenstererfolg/finally verfügbar. Ein anderer MNQ-Recorder kann deshalb denselben Token übernehmen und der einzige passende Kandidat sein. Bereits laufende Recorder lesen ihn nicht erneut; `before` schließt vorinitialisierte Ordner weiterhin aus.

**Offline reproduziert** mit zwei Indicator-Methodenausschnitten und echtem Core: beide erhalten dieselbe runId, Token-Datei bleibt bestehen; Start in derselben Sekunde ergibt sogar denselben Ordner, da C nur Symbol+Sekunde+erste sechs Tokenzeichen verwendet. Damit kann auch die Annahme „zwei Recorder → zwei Kandidaten → Ambiguous“ scheitern. Keine ATAS-Instanzen gestartet.

EnsureModule hat die größte Fläche in seinem Auswahlzweig tatsächlich ersetzt, aber Zeile 1239 kehrt bei aktivem Datumsbutton weiterhin vor jeder Zielchart-Prüfung zurück. Header-X-Nähe ist außerdem keine Recorder-Identität. In den separaten manuellen Kommandos RClickChart (1790) und SelectModule (2238) existiert Flächensortierung noch; AutoRun ruft diese nicht auf. Daher kein unbelegter Vorwurf, dieser alte Fallback liege noch direkt im AutoRun-Zweig.

Token-Schreibfehler sind nur Warnungen; Lesen kann während WriteAllText einen leeren/partiellen Stand sehen. Abbruch löscht die globale Datei ohne Vergleich ihres aktuellen Eigentümers und kann so ein fremdes Auftragstoken entfernen; nach Prozessabbruch kann sie umgekehrt veraltet liegenbleiben.

**Kleinster Fix:** Zielchart auch im vorbereiteten Zustand zwingend verifizieren. Token ausschließlich dem identifizierten Recorder zustellen/bestätigen, mit atomarem und eigentümergesichertem Lebenszyklus; unabhängig davon eine echte Instanz-ID für Ordner verwenden. Ein exklusiv konsumiertes globales Token allein genügt nicht, wenn der falsche Chart es zuerst nehmen kann.

**Nachweis:** fremder Chart mit Zugang zum selben OutputRoot, zwei zeitgleiche Recorder, vorinitialisierter Zielrecorder, Schreibfehler/Retry und fremder Eigentümerwechsel. Die vier vorhandenen Token-Tests erzeugen unterschiedliche oder gleiche Strings direkt in meta-Dateien; sie testen nicht ReadRunToken oder den Auftrag→Chart-Weg.

### 5. P1 — belegte Codefehler: Snapshot endet vor dem CSV-Lesen; Veröffentlichung bleibt zweiteilig

**B, convert 287–305, otherStreams 391–401, Publish 425–446.**

**Reproduziert:**

- CSV-Änderung unmittelbar vor dem echten csv_data_rows-Aufruf (305), also **nach** post_hashes (287): Manifest meldet zwei CSV-Zeilen und inputSnapshotStable=True, speichert aber den Hash der vorherigen Ein-Zeilen-Datei. Status ist immerhin check. DOM/BBO/Cumulative/Bars werden erst später gehasht und sind nicht Teil von `_INPUT_NAMES`; das globale Stable-Label deckt auch sie nicht ab.
- Fehler beim **zweiten** os.replace mit force=True: neues Parquet mit zwei Trades steht neben altem Manifest mit einem Trade. Der finally entfernt nur das Tempverzeichnis, nicht die widersprüchliche Veröffentlichung. Bei frischem Ziel kann entsprechend Parquet ohne Manifest zurückbleiben.
- Konkurrenz zwischen letzter exists-Prüfung und erstem replace: eine dort neu angelegte fremde Zieldatei wurde trotz force=False überschrieben. Beide Aufrufe verwenden zudem dasselbe `.tmp_publish`, das ohne Eigentümerprüfung gelöscht wird; parallele Exporte sind damit nicht isoliert.

Die Szenarien liefen ausschließlich auf synthetischen Dateien unter R; `bridge_recheck.py` / `bridge_recheck.json`. Das sind Fehler an den vorherigen Integritätsanforderungen, keine neue allgemeine Analyse.

**Kleinster Fix:** Alle tatsächlich ausgewerteten Eingaben innerhalb derselben Stabilitätsprüfung erfassen; Hash/Statistik aus demselben gelesenen Stand ableiten. Export in ein exklusives, eindeutiges Staging-Verzeichnis schreiben und als **eine Generation** veröffentlichen (atomarer Verzeichnis-/Referenzwechsel); existierende Ziele ohne Ersetzungsfreigabe mit atomarer Eigentümerprüfung ablehnen. Keine zwei einzelnen Datei-renames als Transaktion ausweisen.

**Nachweis:** CSV-/OtherStream-Änderung nach Tick-Lesen, Fehler zwischen beiden bisherigen Veröffentlichungsoperationen und gleichzeitiger fremder Zielbesitz. Fix muss konsistentes Alt- oder Neuergebnis liefern, nie eine Mischung.

**Geschlossene Teilprobleme:** ns-Parsing und fraktionale Filterung einschließlich +01:00-Offset erhalten 100/200 ns; identische Prints behalten Reihenfolge. Die Sekundenbasis benutzt zwar datetime.timestamp()/float, jedoch nur ganze moderne Epochsekunden; in den geprüften Daten geht dadurch keine Nanosekunde verloren. Die Nachkommastellen und Arrow-Spalten durchlaufen keine Float-/µs-Zwischenstufe. Legacy ohne clean, bad JSON und getestete Zähldiskrepanzen sind check statt complete; pre/post erkennt die getestete Änderung während NDJSON-Lesen; Abbruch während temporärem Parquet-Schreiben schützt das vorhandene Ziel. Die vorhandenen Tests hören vor der kritischen Publish-Phase auf.

`main()` gibt für check weiterhin Exitcode 0 zurück. Im gezielt durchsuchten src/tools/research-Code wurde kein konsumierender Parquet-Pfad gefunden, der diesen Status bewertet; CLI und Tests sind die sichtbaren Nutzer. Somit ist check ein sichtbares Qualitätslabel, **kein nachgewiesenes nachgelagertes Gate**. Kein imaginärer Dashboard-Konsument wurde unterstellt. Speicherbegrenzung/Skalierung war ausdrücklich nicht Gegenstand neuer Tests.

## Ausgeführte Prüfungen, vorher/nachher und Aussagekraft

- **19/19 pytest** gegen aktuelle Brücke, einmal ausgeführt. Vorher sämtliche Tests und Schreibpfade geprüft. Bytecode/Plugin-Autoload/pytest-Cache deaktiviert; TEMP/TMP und basetemp unter R. Tests benutzen echte convert-Aufrufe/Parquet-Rücklesen.
- **8 neue Tests gegen gesicherte Pre-Fix-Brücke** separat einmal unter R ausgeführt: **8 failed, 11 deselected** bestätigt. Die alte Quelle stimmt per SHA256 mit der vorherigen Review-Baseline überein. Aber: nur vier sind Verhaltens-Assertions (ns, Grenze, bad-json, CSV-Status); vier scheitern an neuer API/Schema (SourceChangedError, TargetExistsError, statusReasons, force). „Derselbe Test vorher rot“ ist daher nicht in allen Fällen ein ausgeführtes Gegenbeispiel des alten Produktionsablaufs. Die zusätzlichen alten bridge_baseline-Szenarien belegen die ursprünglichen Defekte trotzdem; ihre Logs/Skripte wurden gelesen, nicht erneut in bestehender Evidence ausgeführt.
- **18/18 Probe regress** aus frisch kompiliertem aktuellem Program.cs, einmal. Datei-Fixtures verbessern A1/A4 gegenüber reinen Zahlenhelfern; A2 bleibt ein bool/string-Helfertest. Kein AutoRun, kein echter try/finally-Fehlerpfad, keine UI und kein Token-Konsum werden dadurch ausgeführt.
- **7/7 RecorderCore-Tests**, einmal, frisch zusammen mit exakt der aktuellen RecorderCore.cs kompiliert. csproj referenziert dieselbe Datei; der externe Indicator delegiert tatsächlich an diese Klasse, und Claudes Build-Response-Datei nennt beide Quellen. Gegenüber dem früheren extrahierten WriterHarness ist das bessere Produktionsnähe, aber **nicht derselbe unveränderte Test**. Kein paralleler Producer im 500-Events-Test; Dispose-Test mit Writer-Stub belegt Dispose-Aufruf, nicht automatisch jedes echte Windows-Handle-Verhalten.
- Zusätzliche gezielte Tests: ProgressChecks, CoreChecks (echter Core, dokumentierte Fault-Injection und unveränderte Indicator-Methodenausschnitte mit Stub-Basis), bridge_recheck. Resultate oben. Keine SDK-/ATAS-Laufzeit geladen.
- C# wurde mit vorhandenem csc SDK 8.0.422 und vorhandenen .NET-10.0.3-Referenzen ausschließlich nach R kompiliert; kein Restore/Install, kein Schreiben ins Repository. Beide Builds erfolgreich (Probe vier Warnungen). Das ist **kein erneut ausgeführter net8-csproj-Release-Build** und kein neuer Full-Indicator-Build. Vorhandene Build-Nachweise wurden gelesen.
- Reale Quelle `MNQ_20261029_232000Z`, einmal neu nach R konvertiert: **2491 Roh-Trades; 2464 Fenster-Trades; 27 Nachlauf; Fenstervolumen 2701; gesamtes Rohvolumen 2729**. Status check, Grund ausschließlich summary_not_clean_or_missing; inputSnapshotStable=True für diesen unveränderten Lauf. Präzise Bezugsmenge bestätigt.

## Unverändertheit und DLLs

95 vor/nach gehashte Originaldateien: **0 Änderungen**. Umfasst aktuelle relevante Quellen/Tests, Recorder-Quelle, beide bestehenden Recorder-DLLs, sieben Dateien der realen Aufnahme, vorhandene Fixes-Baseline/After-Evidence/Build-Artefakt sowie bestehende Berichte. Listen: R/before.json und R/after.json. Git-Status blieb gleich. Nicht gehashte übrige Bestände wurden nicht beschrieben; keine flächendeckende Prüfung aller Aufnahmen behauptet.

Zusätzlich historischer Vergleich gegen den **vorherigen Review-Hash**, nicht nur diese Runde:
- alle sieben Rohdateien unverändert;
- F:\ATAS_Data\Recorder\OrderFlowReplayRecorder.dll und `%APPDATA%\ATAS\Indicators\OrderFlowReplayRecorder.dll` unverändert, beide SHA256 `9065007A3B48950319241ECFACBF81D930D37A7EFE76052EA7C2CFEC53DE5E1D`;
- neues Fixes-Build-Artefakt: SHA256 `FA3CF6840540B428FF51037D326C78D6FE01A979F7AB9601A67540B255EECF4D`, also nicht die Datei im geprüften Ladeordner.

Damit ist **kein Austausch der geprüften DLL-Dateien** belegt. Die aktuell im ATAS-Prozess aktive Implementierung wurde nicht inspiziert; ein vorhandener Build-Hash beweist keine Laufzeitaktivierung. R/historical_hash_check.json enthält den Vergleich.

## Getrennte Bewertung und nächste Entscheidung

**Recorder/Automation:** wesentliche Offline-Korrekturen belegt, aber weiterhin konkrete Fehler in Herkunft, terminalem Adapterzustand und Barriere-Timeout. Cleanup-Teilkorrekturen echt; vollständige Ablaufabdeckung unzureichend. Reale ATAS-Laufzeit ungeprüft. Autonomes Recorder-Hinzufügen/-Entfernen weiterhin **nicht implementiert**, nicht bloß „Implementierung ohne Nachweis“.

**Offline-Brücke:** verlustfreie Zeitverarbeitung und ehrlicher Legacy-Status sind für die geprüften Fälle geschlossen; echte Konvertierung reproduziert. Snapshot-Umfang und transaktionale Veröffentlichung verhindern eine vollständige Integritätsabnahme.

1. **Als begrenzten Zwischenstand zur PR vorbereiten?** Ja, als ausdrücklich unvollständige **Draft-/Review-PR** mit diesen offenen Punkten und ohne Betriebsfreigabe. Nicht als „fünf Findings behoben“ oder mergebereite Lieferung. Vorher sicherstellen, dass auch der externe Indicator-Adapter und die gemeinsame Core-Abhängigkeit reviewbar/versioniert enthalten sind; allein ein PR der untracked research-Dateien deckt die F:-Änderung nicht ab. In dieser Runde wurde nichts committed oder veröffentlicht.
2. **Vor kontrolliertem kurzem Live-Nachweis:** zuerst End-/Token-/Lifecycle-Blocker korrigieren und gezielt offline belegen; eindeutiger Zielchart und alleiniger Testauftrag; Source→Build→freigegebenes Deployment dokumentieren; manueller, überprüfbarer Add/Remove-/Abbruchabschluss, solange Autonomie fehlt; neuer separater Nachweisordner auf F:. Anschließend separat autorisierter kurzer Lauf mit Herkunfts-, runId-, clean- und Writer-Ende-Beleg. Keine Freigabe oder Durchführung hier.
3. **Warum kein unbeaufsichtigter Serienbetrieb?** Offline-grüne Helfer-/Core-/Konvertertests beweisen weder UI-Zuordnung noch echte Replay-Herkunft, terminale SDK-Callbacks oder autonomes Add/Remove. Hinzu kommen die reproduzierten Fehler. Der letzte Live-Nachweis betrifft den alten Stand, die neue DLL ist nicht deployed. Serienbetrieb bleibt unbewiesen.

Hindsight: aktuell kein Tool verfügbar, keine Writes. CodeMunch: gemäß Nutzerauftrag optional ausgelassen; aktueller lokaler Quellcode maßgeblich.
