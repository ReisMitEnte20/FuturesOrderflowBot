# Herkunft: Projektquellen außerhalb des Repos (gesichert 2026-11-25)

Einzigartige Quell- und Dokumentationsdateien aus den Projektablagen auf `F:\ATAS_Data`, die in keiner Git-Version
enthalten waren (Prüfung per Git-Blob-Hash gegen `git rev-list --all --objects`). Nur gelesen und hierher KOPIERT —
es ist ein separater Archivstand, keine aktive Version wurde überschrieben.

- `recorder-build-alt/`: frühere Build-Projektdatei des Recorders (heute: `research/atas-recorder/build.ps1`) und Übergabenotiz.
  Die maschinenerzeugten Referenzlisten `build.rsp`/`build_f.rsp` sind NICHT übernommen (lokale Pfade, reproduzierbar).
- `multistrat/`: eigenes Mehrstrategie-Werkzeug (Quellen); Ergebnisdateien `results.*`/`view.html` nicht übernommen.
- `tickreplay-abnahme-20261109/`: Abnahmeprotokoll und Prüfskripte des Tick-Replays.
- `codex-review-20261030/`: Review-Berichte und Prüf-Harness des unabhängigen Codex-Reviews (Ergebnis-/Temp-Dateien nicht übernommen).

Nicht übernommen, weil bereits identisch in Git: `F:\ATAS_Data\Recorder\OrderFlowReplayRecorder.cs`, `validate_recording.py`,
alle 55 Quelldateien in `F:\ATAS_Data\Handover\collector-1.3.9-quellstand\research` (Stand 1.3.9 = Commit 9157163c).

„neutralisiert“ = persönlicher Windows-Benutzerpfad in der Kopie durch `%USERPROFILE%`/`<USER>` ersetzt (öffentliches Repo);
SHA-256 bezieht sich auf das ORIGINAL.

| Datei im Archiv | Original | Bytes | SHA-256 (Original) | neutralisiert |
|---|---|---:|---|---|
| `recorder-build-alt/build.csproj` | `F:\ATAS_Data\Recorder\build.csproj` | 1728 | `2f926806fbf79b8c33d04ede7aa52f1538750cc1a4e40f272ed4c86c1726b8f3` | nein |
| `recorder-build-alt/HANDOFF.md` | `F:\ATAS_Data\Recorder\HANDOFF.md` | 6407 | `553175e867d911e1376118ccf561a3551b124efe428bddee54ed25588af02ee0` | ja |
| `multistrat/multistrat.csproj` | `F:\ATAS_Data\Recorder\multistrat\multistrat.csproj` | 744 | `a00a7c643652b27b28fe47a06a598b9756056860c94c2b709de4ec2577b8f49f` | nein |
| `multistrat/Program.cs` | `F:\ATAS_Data\Recorder\multistrat\Program.cs` | 12270 | `25d823bf1fd7a1a376fe4efa3b2157c0496a98941b168c9cd7c21d5743d9a01a` | ja |
| `multistrat/RUN.md` | `F:\ATAS_Data\Recorder\multistrat\RUN.md` | 2514 | `39636946517c7e69b03fdac4e9b73a332d7397b66dc965a54e414aa789f1a519` | ja |
| `multistrat/template.html` | `F:\ATAS_Data\Recorder\multistrat\template.html` | 8750 | `fa4665de85d3fbb3f517d29d8a245f1cf7017b46e844b4d75d09d8cc96758d6b` | nein |
| `tickreplay-abnahme-20261109/ABNAHME.md` | `F:\ATAS_Data\Research\TickReplay_Abnahme_20261109\ABNAHME.md` | 2715 | `ad6cdd2ae6d9a7e96b97c7e35715e4ee86e9ca67233a79a7947ce4098011c770` | nein |
| `tickreplay-abnahme-20261109/pruefskripte/engine_candles.mjs` | `F:\ATAS_Data\Research\TickReplay_Abnahme_20261109\pruefskripte\engine_candles.mjs` | 921 | `1f165b3478bd40c9b3db4c3002d43583ca934ff46f7e409368fc9f30469be565` | nein |
| `tickreplay-abnahme-20261109/pruefskripte/independent.py` | `F:\ATAS_Data\Research\TickReplay_Abnahme_20261109\pruefskripte\independent.py` | 1647 | `2a8dd3f7e29b9cb5fb937729c4c87aa2d98f40421580502c2ee86d33d5d91218` | nein |
| `tickreplay-abnahme-20261109/pruefskripte/compare.sh` | `F:\ATAS_Data\Research\TickReplay_Abnahme_20261109\pruefskripte\compare.sh` | 785 | `d34f8d13ea3f157e89092f3d86510df5505fccd602cec83e691df52ac08d0423` | nein |
| `tickreplay-abnahme-20261109/pruefskripte/cdp.mjs` | `F:\ATAS_Data\Research\TickReplay_Abnahme_20261109\pruefskripte\cdp.mjs` | 5377 | `17abb26d0872ba3222969b6698c16775aef32b43b82bfb489fcbe96316dddddb` | nein |
| `tickreplay-abnahme-20261109/pruefskripte/cdp_rec.mjs` | `F:\ATAS_Data\Research\TickReplay_Abnahme_20261109\pruefskripte\cdp_rec.mjs` | 5335 | `a6c150f3691630387220a5fc5ed7ab3a604c7978a20483c6308d703a55d9793f` | nein |
| `codex-review-20261030/CLAUDE_DELIVERY_2026-10-30.md` | `F:\ATAS_Data\_Altbestand\2026-11-25\Codex_Review_Context\CLAUDE_DELIVERY_2026-10-30.md` | 8571 | `4a177e14cad92a4d58cea1295266b8b69a714479835e8d67b6510c44b35a051b` | nein |
| `codex-review-20261030/CLAUDE_FIXES_2026-10-30.md` | `F:\ATAS_Data\_Altbestand\2026-11-25\Codex_Review_Context\CLAUDE_FIXES_2026-10-30.md` | 14587 | `19c360c2821bb87e791a38c003f98ca816a7caa3d6b53ac7841a913ce1314e90` | nein |
| `codex-review-20261030/CLAUDE_FIXES_RECHECK2_2026-10-30.md` | `F:\ATAS_Data\_Altbestand\2026-11-25\Codex_Review_Context\CLAUDE_FIXES_RECHECK2_2026-10-30.md` | 11263 | `f6e905451640bce2a85143876b70342df29a7f1a6429d910d6a074b29b646fc6` | nein |
| `codex-review-20261030/CLAUDE_FIXES_RECHECK_2026-10-30.md` | `F:\ATAS_Data\_Altbestand\2026-11-25\Codex_Review_Context\CLAUDE_FIXES_RECHECK_2026-10-30.md` | 15709 | `34225e01c6773d353f82b4e1d6190dc82741e3e71b4beaa56ee97bedd3cf372c` | nein |
| `codex-review-20261030/CLAUDE_LIVEPROOF_2026-10-31.md` | `F:\ATAS_Data\_Altbestand\2026-11-25\Codex_Review_Context\CLAUDE_LIVEPROOF_2026-10-31.md` | 8519 | `b2cb8e17ada08780a91440c3bc17c00214c10717adb4c7392671eb98ed356cdd` | nein |
| `codex-review-20261030/CLAUDE_LIVEPROOF_HANDOVER_2026-10-31.md` | `F:\ATAS_Data\_Altbestand\2026-11-25\Codex_Review_Context\CLAUDE_LIVEPROOF_HANDOVER_2026-10-31.md` | 6228 | `7aab6b8fd8c3255badfe5a7a91023b668e28dccfe68a64539a99c2930d6a4e56` | nein |
| `codex-review-20261030/CLAUDE_LIVEPROOF_ROUND8_2026-10-31.md` | `F:\ATAS_Data\_Altbestand\2026-11-25\Codex_Review_Context\CLAUDE_LIVEPROOF_ROUND8_2026-10-31.md` | 5212 | `e43b43a341feac181eeae3fd0610755391e68751bc5791c88f902f5a2ffb9a3a` | nein |
| `codex-review-20261030/CODEX_RECHECK_2026-10-30.md` | `F:\ATAS_Data\_Altbestand\2026-11-25\Codex_Review_Context\CODEX_RECHECK_2026-10-30.md` | 22113 | `955ef0a7ac5773dff34fa7f80671f982681e02da75b45e3a6069ecc554a6f1c2` | nein |
| `codex-review-20261030/CODEX_RECHECK_2_2026-10-30.md` | `F:\ATAS_Data\_Altbestand\2026-11-25\Codex_Review_Context\CODEX_RECHECK_2_2026-10-30.md` | 12544 | `a5c4ff212f51d2872b38490cd073d8978bcfe0125d7b12ad5de6a82fc21651b6` | nein |
| `codex-review-20261030/CODEX_REVIEW_2026-10-30.md` | `F:\ATAS_Data\_Altbestand\2026-11-25\Codex_Review_Context\CODEX_REVIEW_2026-10-30.md` | 21275 | `f9f73d0717eb785452c0dbc15ce9079139f88a803e9cdd5c08dd44dc744ffb44` | nein |
| `codex-review-20261030/HANDOFF.md` | `F:\ATAS_Data\_Altbestand\2026-11-25\Codex_Review_Context\HANDOFF.md` | 7309 | `411db9b5cf75ba672f5143c7f479200cd3d6cee26ae2262171791934f35d9f01` | ja |
| `codex-review-20261030/HANDOFF_CURRENT.md` | `F:\ATAS_Data\_Altbestand\2026-11-25\Codex_Review_Context\HANDOFF_CURRENT.md` | 15123 | `98e8b9aadd0d5619dd8997cdf118db347fecb42c06ac7dd9f2150ba5015cb3ae` | nein |
| `codex-review-20261030/review-harness/WriterHarness.cs` | `F:\ATAS_Data\_Altbestand\2026-11-25\Codex_Review_Context\Review_cb71abd5503e4eb3b6d1e7fa8f9d5135\WriterHarness.cs` | 13210 | `2ff6a9330f296d4cda854a9955c549fe0991eb49c11a647ae2b2579e6278816b` | nein |
| `codex-review-20261030/review-harness/ProbeChecks.cs` | `F:\ATAS_Data\_Altbestand\2026-11-25\Codex_Review_Context\Review_cb71abd5503e4eb3b6d1e7fa8f9d5135\ProbeChecks.cs` | 1364 | `9955ce7d0e8bb5f9d7172e8d059337d19ac0b5b01db369c5e6d1cb36665815ae` | nein |
| `codex-review-20261030/review-harness/bridge_checks.py` | `F:\ATAS_Data\_Altbestand\2026-11-25\Codex_Review_Context\Review_cb71abd5503e4eb3b6d1e7fa8f9d5135\bridge_checks.py` | 3466 | `c98ef0026424d36ab03f17a2a33730b73ddf41516839e98558e418fbb6d2c374` | nein |
| `codex-review-20261030/review-harness/report_draft.md` | `F:\ATAS_Data\_Altbestand\2026-11-25\Codex_Review_Context\Review_cb71abd5503e4eb3b6d1e7fa8f9d5135\report_draft.md` | 21274 | `1e549ae977d641c292e7918ccd83ac85906d65d8bfaaa3ea030ee33b8be859ff` | nein |
