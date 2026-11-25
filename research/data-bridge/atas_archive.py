#!/usr/bin/env python3
"""
Verlustfreie Archivierung von ATAS-Rohaufnahmen (Zstandard) + transparenter Leseweg.

Leseweg (fuer Bridge, Pruefskripte, Collector):
  resolve/exists/open_binary/open_text/sha256_file/original_size lesen `ticks.ndjson` ODER `ticks.ndjson.zst`
  gleich. Hashes beziehen sich IMMER auf die urspruenglichen, entpackten Bytes -> vorhandene Manifest-Hashes
  (inputHashes) bleiben gueltig, "Fortsetzen" erkennt archivierte Tage weiterhin als committed.
  meta.json, summary.json und archive.json werden NIE komprimiert (bleiben direkt lesbar).

Archivformat: <name>.zst = aneinandergehaengte Zstandard-Frames (je Block von BLOCK_BYTES Originaldaten). Das ist
Standard-zstd (z. B. `zstd -d` entpackt es bytegenau) und wird mit konstantem Speicher gestreamt.
Je Aufnahmeordner fuehrt archive.json Groesse + SHA-256 jedes Originals (Pruefwert fuer Wiederherstellung).

Ablauf je Datei (nacheinander, im bestehenden Ordner, keine Kopie des Bestands):
  1. Quelle: nicht gesperrt, Groesse/Zeitstempel merken; genug freier Platz (Originalgroesse + Reserve)?
  2. Blockweise lesen, dabei SHA-256 bilden, komprimiert nach <name>.zst.tmp (gleiches Laufwerk), fsync.
  3. Optional: SHA-256 gegen erwarteten Wert (Manifest-inputHashes) -> Abweichung: Original bleibt.
  4. Temporaeres Archiv vollstaendig entpacken: Bytezahl + SHA-256 == Original? Sonst Original bleibt.
  5. Nicht kleiner als das Original? -> Archiv verwerfen, Original bleibt.
  6. Quelle unveraendert? (Groesse, Zeitstempel UND erneuter SHA-256) Sonst Original bleibt.
  7. archive.json: Eintrag 'verified' (atomar) -> tmp in <name>.zst umbenennen -> Original loeschen -> 'archived'.
Jeder Abbruch dazwischen ist fortsetzbar (siehe _reconcile_file): ein .tmp ist immer nur ein verwerfbarer Rest;
liegen Original und Archiv nebeneinander, wird nur nach erneuter VOLLER Pruefung beider gegen archive.json
geloescht, sonst bleibt beides und der Zustand wird als 'unklar' gemeldet (nie blind loeschen).

Automatisch archiviert werden nur Aufnahmen, die eindeutig abgeschlossen und importiert sind (siehe select).
Dieses Modul schreibt NIE in eine Collection (trades.csv / collection_manifest.json) und nimmt nichts neu auf.
"""
from __future__ import annotations

import argparse, hashlib, io, json, os, shutil, subprocess, sys, time, uuid
from datetime import datetime, timezone
from pathlib import Path

ARCHIVE_SUFFIX = ".zst"
TMP_SUFFIX = ".zst.tmp"
RESTORE_TMP_SUFFIX = ".restore.tmp"
ARCHIVE_MANIFEST = "archive.json"
ARCHIVE_SCHEMA = "atas-recording-archive-1"
BLOCK_BYTES = 64 * 1024 * 1024
DEFAULT_LEVEL = 3
FREE_RESERVE_BYTES = 2 * 1024 ** 3          # mindestens so viel muss nach dem groessten Schreibvorgang frei bleiben
QUIET_SECONDS = 10 * 60                      # Ordner, die juenger sind, gelten als moeglicherweise aktiv
NEVER_COMPRESS = ("meta.json", "summary.json", ARCHIVE_MANIFEST)
COMPRESSIBLE_EXT = (".ndjson", ".csv")
STREAM_KIND = {"ticks.ndjson": "Trades", "ticks.csv": "Trades", "dom.ndjson": "DOM", "bbo.ndjson": "BBO",
               "cumtrades.ndjson": "Kumulierte Trades", "bars.ndjson": "Bars", "gaps.ndjson": "Luecken"}


class ArchiveError(RuntimeError):
    pass


class OutOfSpace(ArchiveError):
    pass


class Stopped(ArchiveError):
    pass


# ---------------------------------------------------------------------------
# Leseweg (transparent roh / .zst)
# ---------------------------------------------------------------------------

def resolve(path) -> Path | None:
    """Tatsaechliche Datei fuer einen logischen Namen: das Original, sonst <name>.zst, sonst None.
    Liegen beide vor (z. B. unterbrochene Archivierung), gilt das Original."""
    p = Path(path)
    if p.is_file():
        return p
    z = Path(str(p) + ARCHIVE_SUFFIX)
    return z if z.is_file() else None


def exists(path) -> bool:
    return resolve(path) is not None


def is_archived(path) -> bool:
    r = resolve(path)
    return r is not None and r.name.endswith(ARCHIVE_SUFFIX) and not Path(path).name.endswith(ARCHIVE_SUFFIX)


class _ZstRaw(io.RawIOBase):
    """RawIOBase-Adapter ueber pyarrows streamenden zstd-Decoder (konstanter Speicher, mehrere Frames)."""

    def __init__(self, path: Path):
        import pyarrow as pa   # nur bei Bedarf (roh lesen braucht kein pyarrow)
        self._s = pa.input_stream(str(path), compression="zstd")

    def readable(self):
        return True

    def readinto(self, b):
        data = self._s.read(len(b))
        n = len(data)
        b[:n] = data
        return n

    def close(self):
        try:
            self._s.close()
        finally:
            super().close()


def open_binary(path):
    """Datei-Objekt mit den ORIGINAL-Bytes (roh oder aus .zst entpackt)."""
    r = resolve(path)
    if r is None:
        raise FileNotFoundError(f"{path} (weder roh noch {ARCHIVE_SUFFIX})")
    if r.name.endswith(ARCHIVE_SUFFIX) and not Path(path).name.endswith(ARCHIVE_SUFFIX):
        return io.BufferedReader(_ZstRaw(r), buffer_size=1 << 20)
    return open(r, "rb")


def open_text(path, encoding="utf-8"):
    """Wie open(path, 'r', encoding=...) (universelle Zeilenenden), aber auch fuer <name>.zst."""
    return io.TextIOWrapper(open_binary(path), encoding=encoding)


def sha256_file(path) -> str:
    """SHA-256 der Original-Bytes (bei .zst: der entpackten Bytes)."""
    h = hashlib.sha256()
    with open_binary(path) as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def original_size(path) -> int:
    """Groesse der Original-Bytes (bei .zst aus archive.json, sonst durch Entpacken gezaehlt)."""
    p = Path(path)
    r = resolve(p)
    if r is None:
        raise FileNotFoundError(str(p))
    if r == p:
        return p.stat().st_size
    e = load_archive_manifest(p.parent).get("files", {}).get(p.name)
    if e and e.get("size") is not None:
        return int(e["size"])
    n = 0
    with open_binary(p) as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            n += len(chunk)
    return n


# ---------------------------------------------------------------------------
# archive.json je Aufnahmeordner
# ---------------------------------------------------------------------------

def load_archive_manifest(rec_dir) -> dict:
    p = Path(rec_dir) / ARCHIVE_MANIFEST
    if p.is_file():
        return json.loads(p.read_text(encoding="utf-8"))
    return {"schemaVersion": ARCHIVE_SCHEMA, "files": {}}


def _write_archive_manifest(rec_dir: Path, m: dict):
    m["schemaVersion"] = ARCHIVE_SCHEMA
    m["updatedAtUtc"] = _now()
    tmp = rec_dir / (".archive_" + uuid.uuid4().hex + ".tmp")
    data = json.dumps(m, indent=2, ensure_ascii=False).encode("utf-8")
    with open(tmp, "wb") as f:
        f.write(data)
        f.flush()
        os.fsync(f.fileno())
    os.replace(tmp, rec_dir / ARCHIVE_MANIFEST)


def _now():
    return datetime.now(timezone.utc).isoformat()


# ---------------------------------------------------------------------------
# Hilfen: Sperre, Platz, Stopp
# ---------------------------------------------------------------------------

def is_locked(path) -> bool:
    """True, wenn ein anderer Prozess die Datei offen hat (Windows: exklusives Oeffnen scheitert)."""
    p = str(path)
    if os.name != "nt":
        return False
    import ctypes
    from ctypes import wintypes
    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    k32.CreateFileW.restype = wintypes.HANDLE
    k32.CreateFileW.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD, wintypes.LPVOID,
                                wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
    GENERIC_READ, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL = 0x80000000, 3, 0x80
    h = k32.CreateFileW(p, GENERIC_READ, 0, None, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, None)
    if h == wintypes.HANDLE(-1).value or h is None:
        return ctypes.get_last_error() in (32, 33)   # SHARING_VIOLATION / LOCK_VIOLATION
    k32.CloseHandle(h)
    return False


def free_bytes(path) -> int:
    return shutil.disk_usage(str(path)).free


def _check_stop(stop_file):
    if stop_file and Path(stop_file).exists():
        raise Stopped("Abbruch angefordert")


def _emit(progress, **ev):
    if progress:
        progress(ev)


def _stat_sig(p: Path):
    st = p.stat()
    return (st.st_size, st.st_mtime_ns)


def _hash_and_size(path_or_stream_opener, stop_file=None):
    h = hashlib.sha256()
    n = 0
    with path_or_stream_opener() as f:
        while True:
            _check_stop(stop_file)
            chunk = f.read(BLOCK_BYTES)
            if not chunk:
                break
            h.update(chunk)
            n += len(chunk)
    return h.hexdigest(), n


def _safe_remove(p: Path):
    try:
        p.unlink()
    except FileNotFoundError:
        pass


# ---------------------------------------------------------------------------
# Eine Datei archivieren / Zustand abgleichen / wiederherstellen
# ---------------------------------------------------------------------------

def _reconcile_file(rec_dir: Path, name: str, m: dict, stop_file=None) -> dict:
    """Bringt einen evtl. unterbrochenen Zustand in Ordnung, ohne je ungeprueft zu loeschen.
    Rueckgabe {'state': 'raw'|'archived'|'unclear', ...}."""
    raw = rec_dir / name
    fin = rec_dir / (name + ARCHIVE_SUFFIX)
    tmp = rec_dir / (name + TMP_SUFFIX)
    if tmp.exists():
        _safe_remove(tmp)                       # verwerfbarer Rest eines Abbruchs; Original bleibt unberuehrt
    e = m.get("files", {}).get(name)
    if raw.exists() and fin.exists():
        if not e or e.get("sha256") is None:
            return {"state": "unclear", "reason": "Original und Archiv vorhanden, aber kein Pruefwert in archive.json"}
        zsha, zn = _hash_and_size(lambda: open_binary_zst(fin), stop_file)
        rsha, rn = _hash_and_size(lambda: open(raw, "rb"), stop_file)
        if zsha == rsha == e["sha256"] and zn == rn == int(e["size"]):
            raw.unlink()                         # beides voll geprueft und identisch -> Original entbehrlich
            e["state"] = "archived"; e["rawRemovedAtUtc"] = _now()
            _write_archive_manifest(rec_dir, m)
            return {"state": "archived", "resumed": True}
        return {"state": "unclear", "reason": "Original und Archiv vorhanden und NICHT identisch mit dem Pruefwert"}
    if fin.exists():
        if e and e.get("state") == "verified":
            e["state"] = "archived"; e.setdefault("rawRemovedAtUtc", _now())
            _write_archive_manifest(rec_dir, m)
        if not e:
            return {"state": "unclear", "reason": "Archiv ohne Eintrag in archive.json"}
        return {"state": "archived"}
    if raw.exists():
        return {"state": "raw"}
    return {"state": "missing"}


def open_binary_zst(path: Path):
    return io.BufferedReader(_ZstRaw(path), buffer_size=1 << 20)


def archive_file(rec_dir, name, *, level=DEFAULT_LEVEL, expected_sha=None, stop_file=None,
                 progress=None, keep=False, reserve=FREE_RESERVE_BYTES) -> dict:
    """Archiviert EINE Datei gemaess Modul-Doku. keep=True: nur messen (Archiv wird verworfen, Original bleibt)."""
    import pyarrow as pa
    rec_dir = Path(rec_dir)
    raw = rec_dir / name
    fin = rec_dir / (name + ARCHIVE_SUFFIX)
    tmp = rec_dir / (name + TMP_SUFFIX)
    m = load_archive_manifest(rec_dir)
    st = _reconcile_file(rec_dir, name, m, stop_file)
    res = {"file": name, "kind": STREAM_KIND.get(name, "Sonstige")}
    if st["state"] != "raw":
        res.update(result="already_archived" if st["state"] == "archived" else st["state"], **st)
        if st["state"] == "archived":
            e = m["files"].get(name, {})
            res.update(origSize=e.get("size"), zstSize=fin.stat().st_size if fin.exists() else None)
        return res
    if is_locked(raw):
        return {**res, "result": "locked", "reason": "Datei ist von einem anderen Prozess geoeffnet"}
    sig0 = _stat_sig(raw)
    size = sig0[0]
    if free_bytes(rec_dir) < size + reserve:
        raise OutOfSpace(f"Zu wenig freier Platz fuer {raw} ({size} B + Reserve {reserve} B) - Original bleibt")
    codec = pa.Codec("zstd", compression_level=level)
    h = hashlib.sha256()
    n = 0
    t0 = time.time()
    try:
        with open(raw, "rb") as src, open(tmp, "wb") as dst:
            while True:
                _check_stop(stop_file)
                block = src.read(BLOCK_BYTES)
                if not block:
                    break
                h.update(block)
                n += len(block)
                dst.write(codec.compress(block, asbytes=True))
                _emit(progress, event="block", file=name, done=n, total=size)
            dst.flush()
            os.fsync(dst.fileno())
    except Stopped:
        _safe_remove(tmp)
        raise
    except OSError as ex:
        _safe_remove(tmp)
        if getattr(ex, "winerror", None) in (39, 112) or ex.errno == 28:
            raise OutOfSpace(f"Platte voll beim Schreiben von {tmp.name} - Original bleibt") from ex
        return {**res, "result": "read_or_write_error", "reason": str(ex)}
    sha = h.hexdigest()
    zsize = tmp.stat().st_size
    res.update(origSize=n, zstSize=zsize, sha256=sha, compressSec=round(time.time() - t0, 2))
    if n != size:
        _safe_remove(tmp)
        return {**res, "result": "source_changed", "reason": "Groesse hat sich waehrend des Lesens geaendert"}
    if expected_sha and expected_sha != sha:
        _safe_remove(tmp)
        return {**res, "result": "hash_mismatch_manifest", "reason": "SHA-256 weicht vom Collection-Manifest ab"}
    # 4) Archiv vollstaendig entpacken und vergleichen
    try:
        zsha, zn = _hash_and_size(lambda: open_binary_zst(tmp), stop_file)
    except Stopped:
        _safe_remove(tmp)
        raise
    except Exception as ex:   # beschaedigtes/unlesbares Archiv
        _safe_remove(tmp)
        return {**res, "result": "verify_failed", "reason": f"Archiv nicht lesbar: {ex}"}
    if (zsha, zn) != (sha, n):
        _safe_remove(tmp)
        return {**res, "result": "verify_failed", "reason": "Entpacktes Archiv weicht vom Original ab"}
    if zsize >= n:
        _safe_remove(tmp)
        return {**res, "result": "kept_not_smaller"}
    if keep:
        _safe_remove(tmp)
        return {**res, "result": "measured"}
    # 6) Quelle unveraendert? (Groesse, Zeitstempel, erneuter Hash)
    try:
        rsha, rn = _hash_and_size(lambda: open(raw, "rb"), stop_file)
    except Stopped:
        _safe_remove(tmp)
        raise
    if _stat_sig(raw) != sig0 or (rsha, rn) != (sha, n):
        _safe_remove(tmp)
        return {**res, "result": "source_changed", "reason": "Original hat sich seit dem Lesen geaendert"}
    # 7) erst Pruefwert festhalten, dann umbenennen, dann Original loeschen
    m.setdefault("files", {})[name] = {"state": "verified", "size": n, "sha256": sha, "archive": fin.name,
                                       "archiveSize": zsize, "codec": "zstd", "level": level,
                                       "blockBytes": BLOCK_BYTES, "archivedAtUtc": _now()}
    _write_archive_manifest(rec_dir, m)
    os.rename(tmp, fin)                       # scheitert, falls fin existiert (wird oben ausgeschlossen)
    raw.unlink()
    m["files"][name]["state"] = "archived"
    m["files"][name]["rawRemovedAtUtc"] = _now()
    _write_archive_manifest(rec_dir, m)
    return {**res, "result": "archived", "seconds": round(time.time() - t0, 2)}


def compressible_names(rec_dir) -> list[str]:
    """Logische Namen der komprimierbaren Stroeme (roh oder bereits archiviert), stabil sortiert."""
    out = set()
    for f in os.listdir(rec_dir):
        base = f[:-len(ARCHIVE_SUFFIX)] if f.endswith(ARCHIVE_SUFFIX) else f
        if f.endswith(TMP_SUFFIX):
            base = f[:-len(TMP_SUFFIX)]
        if base in NEVER_COMPRESS or not base.endswith(COMPRESSIBLE_EXT):
            continue
        out.add(base)
    return sorted(out)


def restore_recording(rec_dir, *, stop_file=None, progress=None, reserve=FREE_RESERVE_BYTES) -> dict:
    """Bytegenaue Rueckwandlung aller Archive eines Ordners (fuer aeltere Tools). Prueft gegen archive.json."""
    rec_dir = Path(rec_dir)
    m = load_archive_manifest(rec_dir)
    files = []
    for name in compressible_names(rec_dir):
        raw = rec_dir / name
        fin = rec_dir / (name + ARCHIVE_SUFFIX)
        tmp = rec_dir / (name + RESTORE_TMP_SUFFIX)
        _safe_remove(rec_dir / (name + TMP_SUFFIX))
        _safe_remove(tmp)
        if not fin.exists():
            files.append({"file": name, "result": "raw_present"})
            continue
        e = m.get("files", {}).get(name)
        if not e or not e.get("sha256"):
            files.append({"file": name, "result": "unclear", "reason": "kein Pruefwert in archive.json"})
            continue
        if raw.exists():
            rsha, rn = _hash_and_size(lambda: open(raw, "rb"), stop_file)
            if (rsha, rn) == (e["sha256"], int(e["size"])):
                fin.unlink()                    # Original liegt geprueft vor -> Archiv entbehrlich
                e["state"] = "restored"; e["restoredAtUtc"] = _now()
                _write_archive_manifest(rec_dir, m)
                files.append({"file": name, "result": "restored"})
            else:
                files.append({"file": name, "result": "unclear", "reason": "Original weicht vom Pruefwert ab"})
            continue
        if free_bytes(rec_dir) < int(e["size"]) + reserve:
            raise OutOfSpace(f"Zu wenig freier Platz zum Wiederherstellen von {name}")
        h = hashlib.sha256(); n = 0
        try:
            with open_binary_zst(fin) as src, open(tmp, "wb") as dst:
                while True:
                    _check_stop(stop_file)
                    b = src.read(BLOCK_BYTES)
                    if not b:
                        break
                    h.update(b); n += len(b); dst.write(b)
                    _emit(progress, event="block", file=name, done=n, total=int(e["size"]))
                dst.flush(); os.fsync(dst.fileno())
        except Exception as ex:
            _safe_remove(tmp)
            if isinstance(ex, (Stopped,)):
                raise
            files.append({"file": name, "result": "verify_failed", "reason": f"Archiv nicht lesbar: {ex}"})
            continue
        if (h.hexdigest(), n) != (e["sha256"], int(e["size"])):
            _safe_remove(tmp)
            files.append({"file": name, "result": "verify_failed", "reason": "Entpackt != Pruefwert"})
            continue
        os.rename(tmp, raw)
        fin.unlink()
        e["state"] = "restored"; e["restoredAtUtc"] = _now()
        _write_archive_manifest(rec_dir, m)
        files.append({"file": name, "result": "restored", "size": n})
    ok = all(f["result"] in ("restored", "raw_present") for f in files)
    if ok:
        m["restoredAtUtc"] = _now()
        _write_archive_manifest(rec_dir, m)
    return {"recording": rec_dir.name, "ok": ok, "files": files}


def verify_recording(rec_dir, stop_file=None) -> dict:
    """Entpackt jedes Archiv eines Ordners vollstaendig und vergleicht mit archive.json (aendert nichts)."""
    rec_dir = Path(rec_dir)
    m = load_archive_manifest(rec_dir)
    out = []
    for name in compressible_names(rec_dir):
        fin = rec_dir / (name + ARCHIVE_SUFFIX)
        if not fin.exists():
            continue
        e = m.get("files", {}).get(name) or {}
        try:
            zsha, zn = _hash_and_size(lambda: open_binary_zst(fin), stop_file)
            ok = (zsha, zn) == (e.get("sha256"), e.get("size"))
            out.append({"file": name, "ok": ok, "size": zn})
        except Stopped:
            raise
        except Exception as ex:
            out.append({"file": name, "ok": False, "reason": str(ex)})
    return {"recording": rec_dir.name, "ok": all(x["ok"] for x in out), "files": out}


# ---------------------------------------------------------------------------
# Auswahl: welche Aufnahmen duerfen automatisch archiviert werden?
# ---------------------------------------------------------------------------

def _norm(p) -> str:
    return os.path.normcase(os.path.normpath(str(p)))


def load_job_refs(data_dir) -> dict:
    """recordingId -> Liste (collection, job_id, job) aus ALLEN Collection-Manifesten (nur lesend)."""
    cols = Path(data_dir) / "Collections"
    refs = {}
    if not cols.is_dir():
        return refs
    for c in sorted(os.listdir(cols)):
        mp = cols / c / "collection_manifest.json"
        if not mp.is_file():
            continue
        try:
            m = json.loads(mp.read_text(encoding="utf-8"))
        except Exception:
            continue
        for jid, j in (m.get("jobs") or {}).items():
            refs.setdefault(j.get("recordingId"), []).append((c, jid, j))
    return refs


def classify_recording(rec_dir: Path, refs: dict, now=None) -> tuple[bool, str, dict]:
    """(geeignet, Grund, Info). Streng: nur eindeutig abgeschlossene, importierte, fehlerfreie Aufnahmen."""
    now = now or time.time()
    info = {}
    summ_p, meta_p = rec_dir / "summary.json", rec_dir / "meta.json"
    if not summ_p.is_file():
        return False, "keine summary.json (aktiv oder abgebrochen)", info
    try:
        summ = json.loads(summ_p.read_text(encoding="utf-8"))
        meta = json.loads(meta_p.read_text(encoding="utf-8")) if meta_p.is_file() else None
    except Exception as ex:
        return False, f"summary/meta unlesbar ({ex})", info
    if meta is None:
        return False, "keine meta.json", info
    am = load_archive_manifest(rec_dir)
    if am.get("restoredAtUtc"):
        return False, "wiederhergestellt (bewusst roh behalten)", info
    # Nur Recorder-Dateien zaehlen (eigene Archiv-/Temp-Dateien wuerden einen Wiederanlauf sonst blockieren).
    newest = max((f.stat().st_mtime for f in rec_dir.iterdir()
                  if f.name != ARCHIVE_MANIFEST and not f.name.endswith((ARCHIVE_SUFFIX, TMP_SUFFIX, RESTORE_TMP_SUFFIX))
                  and not f.name.startswith(".archive_")), default=0)
    if now - newest < QUIET_SECONDS:
        return False, "kuerzlich geaendert (moeglicherweise aktiv)", info
    r = refs.get(rec_dir.name)
    if not r:
        return False, "keinem Collection-Auftrag zugeordnet (nicht importiert)", info
    jobs = [x for x in r if x[2].get("status") == "committed"]
    if not jobs:
        return False, "Auftrag nicht committed (offen/abgelehnt)", info
    for c, jid, j in jobs:
        if j.get("coverageStatus") != "complete":
            return False, f"Auftrag {jid} in {c}: coverageStatus={j.get('coverageStatus')}", info
        if (j.get("runId") or "") != (meta.get("runId") or "") or not meta.get("runId"):
            return False, f"runId passt nicht zum Auftrag {jid}", info
        if j.get("recordingDir") and _norm(j["recordingDir"]) != _norm(rec_dir):
            return False, f"Auftrag {jid} verweist auf anderen Ordner", info
    if summ.get("clean") is not True:
        return False, "summary.clean ist nicht true", info
    td = summ.get("totalDrops")
    if not (isinstance(td, int) and not isinstance(td, bool) and td == 0):
        return False, f"Recorder-Drops gemeldet (totalDrops={td})", info
    # meta/summary unveraendert gegenueber dem Import?
    expected = {}
    for c, jid, j in jobs:
        for k, v in (j.get("inputHashes") or {}).items():
            if k in expected and expected[k] != v:
                return False, f"widerspruechliche inputHashes fuer {k}", info
            expected[k] = v
    for k in ("meta.json", "summary.json"):
        if k in expected and sha256_file(rec_dir / k) != expected[k]:
            return False, f"{k} weicht vom Collection-Manifest ab", info
    info["expected"] = expected
    info["jobs"] = [(c, jid) for c, jid, _ in jobs]
    info["date"] = (jobs[0][2].get("window") or {}).get("start", "")[:10]
    return True, "", info


def replay_run_active() -> bool:
    """True, wenn ein Collector-Sammellauf (Probe) laeuft -> keine Archivierung parallel zum Replay."""
    if os.name != "nt":
        return False
    try:
        r = subprocess.run(["tasklist", "/FI", "IMAGENAME eq AtasReplayProbe.exe", "/FO", "CSV", "/NH"],
                           capture_output=True, timeout=20)
        if r.returncode != 0:
            return True   # unklar -> vorsichtig
        # Bytes statt Text: tasklist schreibt in der OEM-Codepage (deutsche Umlaute) -> nur ASCII-Namen vergleichen.
        return b'"atasreplayprobe.exe"' in r.stdout.lower()
    except Exception:
        return True   # unklar -> vorsichtig: als aktiv behandeln


def analyze(data_dir) -> dict:
    data_dir = Path(data_dir)
    rec_root = data_dir / "Recordings"
    refs = load_job_refs(data_dir)
    by_kind = {}
    eligible, skipped, archived, unclear = [], [], [], []
    total = 0
    now = time.time()
    for d in sorted(os.listdir(rec_root)) if rec_root.is_dir() else []:
        p = rec_root / d
        if not p.is_dir():
            continue
        size = 0
        for f in p.iterdir():
            s = f.stat().st_size
            size += s
            base = f.name[:-len(ARCHIVE_SUFFIX)] if f.name.endswith(ARCHIVE_SUFFIX) else f.name
            k = by_kind.setdefault(STREAM_KIND.get(base, "Sonstige"), {"rawBytes": 0, "archivedBytes": 0})
            k["archivedBytes" if f.name.endswith(ARCHIVE_SUFFIX) else "rawBytes"] += s
        total += size
        names = compressible_names(p)
        am = load_archive_manifest(p)
        raw_left = [n for n in names if (p / n).exists()]
        both = [n for n in names if (p / n).exists() and (p / (n + ARCHIVE_SUFFIX)).exists()]
        n_arch = sum(1 for n in names if (p / (n + ARCHIVE_SUFFIX)).exists())
        if both:
            unclear.append({"recording": d, "files": both, "bytes": size})
        ok, why, info = classify_recording(p, refs, now)
        entry = {"recording": d, "bytes": size, "date": info.get("date", "")}
        if n_arch and not raw_left:
            archived.append(entry)
        elif ok:
            entry["rawBytes"] = sum((p / n).stat().st_size for n in raw_left)
            eligible.append(entry)
        else:
            skipped.append({**entry, "reason": why})
    du = shutil.disk_usage(str(data_dir))
    return {"dataDir": str(data_dir), "recordingsBytes": total, "byKind": by_kind,
            "eligible": eligible, "skipped": skipped, "archived": archived, "unclear": unclear,
            "disk": {"used": du.used, "free": du.free, "total": du.total}}


def compress_all(data_dir, *, level=DEFAULT_LEVEL, stop_file=None, only=None, progress=None,
                 limit=None) -> dict:
    """Archiviert alle geeigneten Aufnahmen nacheinander. Bricht bei Platzmangel/Stopp kontrolliert ab."""
    data_dir = Path(data_dir)
    if replay_run_active():
        raise ArchiveError("Ein Sammellauf (AtasReplayProbe) laeuft - Archivierung erst nach dem Lauf.")
    refs = load_job_refs(data_dir)
    rec_root = data_dir / "Recordings"
    done, results, status = 0, [], "completed"
    before = shutil.disk_usage(str(data_dir)).free
    for d in sorted(os.listdir(rec_root)):
        p = rec_root / d
        if not p.is_dir() or (only and d not in only):
            continue
        ok, why, info = classify_recording(p, refs)
        if not ok:
            continue
        names = compressible_names(p)
        if not any((p / n).exists() for n in names):
            continue
        if limit is not None and done >= limit:
            break
        _emit(progress, event="recording", recording=d, date=info.get("date"))
        rec_res = {"recording": d, "date": info.get("date"), "files": []}
        try:
            for n in names:
                if any(is_locked(p / x) for x in names if (p / x).exists()):
                    rec_res["files"].append({"file": n, "result": "locked"})
                    break
                r = archive_file(p, n, level=level, expected_sha=info["expected"].get(n),
                                 stop_file=stop_file, progress=progress)
                rec_res["files"].append(r)
                _emit(progress, event="file", recording=d, **{k: v for k, v in r.items() if k != "event"})
                if r["result"] not in ("archived", "already_archived", "kept_not_smaller"):
                    break   # Fehler in dieser Aufnahme -> restliche Dateien dieser Aufnahme nicht anfassen
        except Stopped:
            status = "stopped"
            results.append(rec_res)
            break
        except OutOfSpace as ex:
            status = "out_of_space"
            rec_res["error"] = str(ex)
            results.append(rec_res)
            break
        except Exception as ex:   # z. B. Loeschen vom Virenscanner blockiert: Zustand ist fortsetzbar, naechste Aufnahme
            rec_res["error"] = f"{type(ex).__name__}: {ex}"
            _emit(progress, event="file", recording=d, file="-", result="error", reason=rec_res["error"])
        results.append(rec_res)
        done += 1
    after = shutil.disk_usage(str(data_dir)).free
    return {"status": status, "recordings": results, "freeBefore": before, "freeAfter": after,
            "freedBytes": after - before}


# ---------------------------------------------------------------------------
# CLI (JSON-Zeilen fuer die Oberflaeche)
# ---------------------------------------------------------------------------

def _print(obj):
    print(json.dumps(obj, ensure_ascii=False), flush=True)


def main(argv=None):
    for _st in (sys.stdout, sys.stderr):
        try:
            _st.reconfigure(encoding="utf-8", errors="backslashreplace")
        except Exception:
            pass
    ap = argparse.ArgumentParser(description="Rohaufnahmen verlustfrei archivieren (zstd) / wiederherstellen")
    sub = ap.add_subparsers(dest="cmd", required=True)
    a1 = sub.add_parser("analyze"); a1.add_argument("--data-dir", required=True)
    a2 = sub.add_parser("compress"); a2.add_argument("--data-dir", required=True)
    a2.add_argument("--level", type=int, default=DEFAULT_LEVEL); a2.add_argument("--stop-file")
    a2.add_argument("--only", nargs="*"); a2.add_argument("--limit", type=int)
    a3 = sub.add_parser("restore"); a3.add_argument("--recording", required=True); a3.add_argument("--stop-file")
    a4 = sub.add_parser("verify"); a4.add_argument("--recording", required=True)
    a5 = sub.add_parser("measure", help="nur messen: komprimieren, pruefen, Archiv verwerfen")
    a5.add_argument("--recording", required=True); a5.add_argument("--level", type=int, default=DEFAULT_LEVEL)
    a = ap.parse_args(argv)

    def prog(ev):
        if ev.get("event") != "block":
            _print(ev)
        elif ev["done"] == ev["total"] or (ev["done"] // BLOCK_BYTES) % 4 == 0:
            _print(ev)

    try:
        if a.cmd == "analyze":
            _print({"event": "result", **analyze(a.data_dir)})
        elif a.cmd == "compress":
            _print({"event": "result", **compress_all(a.data_dir, level=a.level, stop_file=a.stop_file,
                                                      only=a.only, progress=prog, limit=a.limit)})
        elif a.cmd == "restore":
            _print({"event": "result", **restore_recording(a.recording, stop_file=a.stop_file, progress=prog)})
        elif a.cmd == "verify":
            _print({"event": "result", **verify_recording(a.recording)})
        elif a.cmd == "measure":
            p = Path(a.recording)
            res = [archive_file(p, n, level=a.level, keep=True, progress=prog) for n in compressible_names(p)]
            _print({"event": "result", "recording": p.name, "files": res})
        return 0
    except Stopped:
        _print({"event": "result", "status": "stopped"})
        return 3
    except OutOfSpace as ex:
        _print({"event": "result", "status": "out_of_space", "error": str(ex)})
        return 4
    except ArchiveError as ex:
        _print({"event": "result", "status": "refused", "error": str(ex)})
        return 5


if __name__ == "__main__":
    sys.exit(main())
