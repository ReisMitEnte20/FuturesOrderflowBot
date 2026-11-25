# Unabhängiger Recheck 2 — 30.10.2026

**Ergebnis:** Die Offline-Datenbrücke ist für den geprüften lokalen Betrieb unabhängig abnahmefähig. Die konkreten Core-Lebenszyklusfehler sind geschlossen. Die ATAS-Automation bleibt durch belegte Herkunftsfehler blockiert; beim Cleanup bleibt eine begrenzte Schrittkapselung offen. Keine Freigabe für unbeaufsichtigten Serienbetrieb.

Geprüft: CODEX_RECHECK_2026-10-30.md, CLAUDE_FIXES_RECHECK_2026-10-30.md, zugehörige Evidence, aktuelle Produktionsquellen und Tests. Branch `feature/atas-replay-control-probe`, HEAD `16f5d76b5c584763c404786365fdbf273021bf24`. Working Tree unverändert: `?? research/`, `?? tools/`, `?? dashboard/dist-demo/`. Untracked Research-Code und externe Quelle `F:\ATAS_Data\Recorder\OrderFlowReplayRecorder.cs` ausdrücklich einbezogen. Hindsight war in dieser Sitzung nicht verfügbar.

Alle neuen Testartefakte liegen in **R = F:\ATAS_Data\Codex_Review_Context\Recheck2_ec6278830b0a4d2abe4e0aa3062b3781**. Keine Produktionsänderung, ATAS-UI-Aktion, Replay-Ausführung, Installation, DLL-Deployment, Order oder Git-Mutation.

| Bisheriges Finding | Status | Konkreter Beleg |
|---|---|---|
| 1 Enderkennung | **Weiterhin belegter Fehler** | Echter `PlayedEndReached` akzeptiert ausschließlich synthetischen, nach Play empfangenen Preload als Erfolg. `tRecv` bindet Empfangszeit, keine Replay-Herkunft. |
| 2 Cleanup | **Teilweise geschlossen** | Exceptions bei Finalisierung/Restscan verlassen den Abschluss nicht mehr unkontrolliert; Fehler fließen in Gesamtergebnis ein. Pause und Replay-Verlassen teilen jedoch weiterhin einen try-Block: Pause-Exception überspringt Replay-Verlassen. |
| 3 RecorderCore/CoreGate | **Geschlossen für die konkreten Offline-Fehler** | Barrier-Timeout setzt dauerhaft `clean=false`; terminales Gate verhindert Erzeugung nach Dispose. 16 Checks bestehen. SDK-Laufzeitnachweis bleibt separat offen. |
| 4 Token-Handshake | **Weiterhin belegter Fehler** | Echter exklusiver Consume: fremde Instanz erhält Auftragstoken, Zielinstanz erhält null; echte Attribution bewertet den fremden Token-Träger als `Unique`. |
| 5 Exportveröffentlichung | **Geschlossen im geprüften Offline-Betrieb** | 24 pytest-Tests, vollständige Snapshot-Prüfung, vorbereitete gemeinsame Generation, Fehler-/Rollback- und Konkurrenztests auf F:. Reale Baseline stimmt. |

## Verbleibende Fehler und kleinste nächste Schritte

**1. Empfangszeit wird weiterhin als Herkunft behandelt.**

Datei `A:\Projects\FuturesOrderflowBot\research\atas-replay-probe\Program.cs`, `PlayedEndReached`, Z. 2730–2749; Verwendung im AutoRun Z. 1186–1199. Ein BBO mit `tMarket=2026-10-26T08:01:00Z`, `tRecv=2026-10-30T12:00:01Z`, Play-Zeit `2026-10-30T12:00:00Z` genügt: **reached=True**, obwohl die Fixture keinerlei abgespielte Strecke enthält. Das ist ein zulässiger Fehl-Erfolg des Codes, nicht bloß ein fehlender Live-Test. Ob ATAS diesen konkreten Empfangsablauf erzeugt, wurde nicht live behauptet oder geprüft.

Der neue Preload-Test Z. 828–839 setzt ausdrücklich `tRecv < Play`; er prüft früheren Empfang und späteres Einlesen, nicht verspäteten Empfang nach Play. Play und Recorder verwenden beide `DateTime.UtcNow` auf demselben Host; die Zeitbasis passt, beweist aber keine Herkunft. Eine Bindung des Ereignisses an Zielchart, Recorder-Instanz und aktiven Replay-Zustand fehlt.

Grenzübertritt Ende+6s funktioniert jetzt; ein Trade exakt am Ende ist nicht erforderlich, BBO/DOM/CumTrades können die Grenze liefern. Ohne irgendein geeignetes Grenzereignis bleibt Erfolg unbewiesen. Zusätzlich stimmt die angenommene geladene Spanne nicht: AutoRun Z. 1188 erlaubt Ende+15min, während das Morgenfenster Z. 1061 nur bis Ende+3min geladen wird. Der gezielte Test Ende+10min liefert ebenfalls True.

**Kleinster Schritt:** Solange kein verifiziertes, auftrag-/instanzgebundenes Replay-Fortschrittssignal vorhanden ist, daraus keinen `played_end_reached`-Erfolg ableiten. Danach die obere Grenze aus dem tatsächlich geladenen Fenster ableiten. Den nach Play empfangenen Preload als Negativtest festhalten. Keine weitere allgemeine Review nötig.

**2. Pause-Fehler überspringt noch Replay-Verlassen.**

Dieselbe Datei, `CleanupAfterRun`, Z. 980–994. Wirft `Invoke(pb)` beim Pausieren, springt die Ausführung direkt in den gemeinsamen catch; der anschließende Versuch über `RibbonReplayButton` entfällt. Finalisierungsprüfung und Restscan werden danach versucht. **Kleinster Schritt:** Pause und Replay-Verlassen separat kapseln, Fehlerstatus beibehalten, durch injizierten Pause-Fehler den dennoch erfolgenden Exit-Versuch prüfen.

Der ursprüngliche Fehler ist ansonsten wesentlich behoben: AutoRun Z. 1238–1259 bewahrt ursprünglichen Abbruchgrund/Exitcode, protokolliert Cleanup-Fehler und verhindert Erfolg bei `cleanupOk=false` oder `cClean=false`; ohne vorherigen Abbruch wird Exitcode 8 verwendet. `AllFinalizedClean` prüft nach einem Ordnerfehler weitere Aufträge. Die neuen Tests prüfen diese Hilfsfunktion und Ergebnislogik, **nicht** den tatsächlichen Aufruf aus `AutoRun.finally`. Die Integration wurde statisch verfolgt; kein UI-Fehlerlauf wurde ausgeführt.

**4. Exklusiver Gewinner ist nicht als Zielchart authentifiziert.**

`A:\Projects\FuturesOrderflowBot\research\recorder-core\TokenHandshake.cs`, `Consume`, Z. 36–50; Adapter `F:\ATAS_Data\Recorder\OrderFlowReplayRecorder.cs`, `ReadRunToken`, Z. 79–82; Probe `VerifyTargetChartUnique`, Z. 1305–1314, und Attribution Z. 942 ff./1159.

Gezielter Ablauf mit den echten Funktionen: Publish → fremder Consume → Ziel-Consume → Attribution. Ergebnis: **foreign=expected-job; target=NULL; verdict=Unique**. Eine vorher erfolgreiche UI-Headerprüfung ändert den späteren gemeinsamen Tokenpfad nicht. Consume erhält keine verifizierte Zielchart-Identität. Die Headerprüfung erzwingt auch nicht, dass global nur der beauftragte Recorder initialisieren kann. Bei sonst passenden Kandidatenmerkmalen wird deshalb der falsche Token-Träger akzeptiert.

Geschlossen sind doppelter Konsum desselben veröffentlichten Tokens und der zuvor reproduzierte gemeinsame Instanzordner. Zwei verschiedene Ordner beweisen jedoch keine richtige Auftragszuordnung. `RetractIfOwner` schützt den unveränderten fremden Inhalt im sequentiellen Test; Lesen/Vergleichen/Löschen ist keine atomare Besitzprüfung. Publish überschreibt vorhandene Tokens, Tokens besitzen keine Ablaufprüfung. Nach Verbrauch und anschließendem Startfehler ist das Token nicht automatisch wieder verfügbar. Diese Behandlung liefert daher ebenfalls keinen Herkunftsnachweis.

**Kleinster Schritt:** Einen zuverlässig erzwungenen Ein-Recorder-Betrieb über Prüfung und Initialisierung hinweg sicherstellen oder Token-Zustellung an eine verifizierte Zielinstanz binden; Abweichungen müssen den Auftrag ablehnen. Fremd-zuerst muss ein Negativtest werden. Kein Ausbau auf beliebige Multi-Chart-Automation verlangt.

## Geschlossene Offline-Punkte und Grenzen

**Recorder-Kern:** `RecorderCore.Finish` Z. 228–254 setzt `_barrierTimedOut`, berücksichtigt es in `clean` und wechselt terminal nach Disposed. Die Queue wird nicht unter einem noch laufenden Producer disposed; ein verspäteter Add kann abgewiesen werden. Ein Timeout-Abschluss ist ausdrücklich fehlgeschlagen, keine vollständige erfolgreiche Schlusszählung. Fehlerflags bleiben erhalten. `CoreGate` koordiniert Erzeugung und Dispose unter demselben Lock; alle fünf Adapter-Callbackpfade berücksichtigen den fehlenden Core nach Dispose. Relevante Erzeugungs-/Dispose-Logik liegt jetzt im gemeinsam getesteten Gate.

Core-Tests, Probe-Projekt und Indicator-Buildliste referenzieren die vorgesehenen gemeinsamen Quellen. Die neue Barrier-Prüfung nutzt eine InFlight-Testnaht, keinen echten blockierenden SDK-Producer; die Gate-Prüfung testet den jetzt tatsächlich delegierten Adapterzustand. Alte Harnesses und neue Tests sind daher nicht pauschal identische Vorher-/Nachher-Läufe. Die konkreten früheren Fehlerbedingungen sind durch aktuelle Assertions plus Quellprüfung geschlossen. ATAS-Callback-Reihenfolge und reale Instanzlebensdauer bleiben Laufzeitnachweise.

**Brücke:** `A:\Projects\FuturesOrderflowBot\research\data-bridge\atas_bridge.py`, Snapshot Z. 188 ff./232–298, Veröffentlichung Z. 433–463. CSV wird jetzt vor dem Post-Hash ausgewertet; alle relevanten Eingaben einschließlich Meta/Summary und weiterer Streams sind einbezogen. Im geprüften stabilen Offline-Input gehören die Hashes zu den verarbeiteten Dateien. Dies ist eine Vorher-/Nachher-Stabilitätsprüfung, keine Dateisystemsperre gegen beliebige gleichzeitige Umschreibungen.

Parquet und Manifest werden vollständig im eindeutigen Geschwister-Staging-Verzeichnis erzeugt; die Veröffentlichung verschiebt das ganze Verzeichnis innerhalb desselben Volumes. F: meldet über DriveInfo **exFAT**, lokales Fixed-Laufwerk. Die Tests laufen tatsächlich dort. Auch ein erst unmittelbar vor Rename angelegtes **leeres** Fremdziel bleibt erhalten: Windows `FileExistsError`, WinError 183. Der bestehende Test deckt das nichtleere Fremdziel ab. Fehlerinjektionen treffen das tatsächliche `os.replace(staging, out_dir)`, nicht einen weggefallenen Einzeldatei-Rename. Bei geprüften Fehlern entsteht keine gültig wirkende gemischte Generation.

Wichtige Präzisierung: `force=True` verwendet zwei Renames mit Rollback, keinen einzigen atomaren Austausch Alt→Neu. Dazwischen kann der Zielpfad fehlen; ein Prozessabbruch kann den alten Stand unter `.old_*` hinterlassen. Keine Behauptung von unterbrechungsfreier Sichtbarkeit oder Stromausfallsicherheit. Für den hier geprüften abgeschlossenen Offline-Export, insbesondere frische Ziele ohne force, ist dies kein verbleibender Mischgenerationsfehler. Die Brücke wird nicht wegen UI-Problemen blockiert.

## Tatsächlich ausgeführte Verifikation

- Aktuelle Probe einmal: **23/23 Checks**; aktuelle Core-Suite einmal: **16/16**. Frische direkte C#-Kompilation der aktuellen Quellen mit vorhandenem SDK-csc und .NET-10.0.3-Referenzen; keine Installation/Restore, kein erneuter vollständiger net8-Projekt- oder Indicator-Build. Logs: `R\probe_build.txt`, `probe_tests.txt`, `core_build.txt`, `core_tests.txt`.
- Brücke einmal: **24 pytest-Tests bestanden**, Bytecode/Cache und Plugin-Autoload deaktiviert, temporäre Ausgaben unter R; Log `R\pytest.txt`. Nanosekunden, halboffene Fenster und Dateireihenfolge bleiben grün.
- Zusätzliche gezielte Gegenbeispiele über echte Produktionsfunktionen: nach Play empfangener Preload, Ende+10min und fremder Token-Gewinner. Code/Log: `R\ProvenanceChecks.cs`, `provenance_checks.txt`. Keine UI-Methoden aufgerufen.
- Reale Konvertierung: **2.491 Roh-Trades, 2.464 Fenster-Trades, 27 Nachlauf-Trades, Fenstervolumen 2.701, Rohvolumen 2.729**. Status **check / summary_not_clean_or_missing**, sieben Snapshot-Dateien. Separater gezielter Konkurrenztest mit leerem Ziel; beide dokumentiert in `R\bridge_validation.py` und `bridge_validation.json`.

**Probe-Anzahl:** Aktuell 6 A1 + 6 A4 + 8 A2 + 3 A3 = **23**; Claude-Log und eigener Lauf stimmen überein. Voriger eigener Lauf hatte 5 + 4 + 6 + 3 = **18**. A1 wurde umgeschrieben, nicht einfach unverändert erweitert; alte Preload-/Mischstromprüfungen sind nicht als identische Herkunftsprüfungen erhalten. Genau diese Abdeckungslücke zeigt das neue Gegenbeispiel. Für die zwischenzeitliche Angabe 24 findet sich in den vorliegenden ausgeführten Logs kein 24. Check; sie ist als unbelegte Zählangabe zu korrigieren. Ein zwischenzeitlich gelöschter einzelner Test lässt sich ohne entsprechenden Zwischenstand nicht behaupten.

**Integrität:** Alle 167 vor dieser Prüfung erfassten Dateien sind SHA256-identisch geblieben (`R\before.json`, `after.json`). Zusätzlich beide ursprünglichen Recorder-DLLs, sieben Rohdateien und vorhandene Baseline-Exportdateien unverändert gegenüber der früheren Review (`historical_integrity.json`). Original-DLL-Hash weiterhin `9065007A3B48950319241ECFACBF81D930D37A7EFE76052EA7C2CFEC53DE5E1D`; keine DLL wurde ersetzt.

Autonomes Recorder-Add/Remove ist weiterhin nicht implementiert; das ist eine Funktionslücke, kein durch diese Tests behobener Cleanup-Exceptionfehler. Ebenso ersetzt ein Meta-Sperren-Scan keinen vollständigen Instanznachweis. Nächster Umfang: nur die oben begrenzten Herkunfts-/Cleanup-Korrekturen und anschließend gesondert autorisierte ATAS-Laufzeitnachweise. Keine allgemeine neue Korrekturrunde für Brücke oder Kern.
