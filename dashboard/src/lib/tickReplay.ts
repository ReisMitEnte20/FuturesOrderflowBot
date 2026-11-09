// Historisches TICK-Replay mit sich aufbauenden Zeitkerzen (Standard 5 Minuten).
//
// Reine Logik ohne Abhaengigkeiten (direkt mit `node --test` pruefbar). KEINE Animation zwischen fertigen
// OHLC-Werten: jede Kerze entsteht ausschliesslich aus den bis zur Replay-Position angewandten ECHTEN Trades.
//
//  - Zeiteinheit: 100 ns (wie .NET-Ticks). Jeder Trade traegt seine Zeit als exakte Ganzzahl relativ zu einem
//    Anker, der auf einer 5-Minuten-Grenze liegt. Ganzzahlen < 2^53 sind in Float64 exakt -> die Zuordnung zu
//    Kerzenintervallen [Start, Ende) ist exakt (ein Trade genau auf der Grenze beginnt die NEUE Kerze).
//  - Reihenfolge = Originalreihenfolge der Quelle. Gleiche Zeitstempel bleiben in ihrer Reihenfolge, identische
//    Trades werden NICHT dedupliziert.
//  - Position p = Anzahl bereits angewandter Trades. Der Zustand bei p haengt nur von den Trades [0, p) ab ->
//    keine Zukunftsdaten, und 1x/100x/Seek liefern bei gleichem p identische Werte.
//  - Ruecksprung stellt den Zustand ueber DENSELBEN Codepfad her wie erneutes Abspielen (abgeschlossene Kerzen
//    stammen aus dem Vorwaertslauf, die laufende Kerze wird ab ihrem ersten Trade neu aufgebaut).

/** 100-ns-Einheiten je Millisekunde. */
export const UNITS_PER_MS = 10_000;
/** 5 Minuten in 100-ns-Einheiten (3e9, exakt in Float64). */
export const BUCKET_5M = 5 * 60 * 1000 * UNITS_PER_MS;
/** Bytes je Trade im Binaerformat des Servers (Float64 t, Float64 Preis, Int32 Volumen, Int8 Seite). */
export const ROW_BYTES = 21;

/** Spaltenspeicher EINER Session (nur die ausgewaehlte Session liegt im Browser, nicht die ganze Woche). */
export interface TickStore {
  /** Zeit je Trade in 100-ns-Einheiten relativ zum Anker. */
  t: Float64Array;
  price: Float64Array;
  vol: Int32Array;
  /** Aggressor: 1 = Buy, -1 = Sell, 0 = unbekannt. */
  side: Int8Array;
  /** Zusammenhaengend geladene Trades ab Index 0 (Nachladen fuellt luecklos von vorne). */
  loaded: number;
  /** Gesamtzahl der Trades der Session laut Server/Manifest. */
  total: number;
}

export function createStore(total: number): TickStore {
  if (!Number.isInteger(total) || total < 0) throw new Error("Ungueltige Trade-Anzahl: " + total);
  return { t: new Float64Array(total), price: new Float64Array(total), vol: new Int32Array(total), side: new Int8Array(total), loaded: 0, total };
}

export interface DecodedChunk { n: number; t: Float64Array; price: Float64Array; vol: Int32Array; side: Int8Array; }

/** Dekodiert einen Server-Abschnitt: Spaltenblock [t f64 x n][Preis f64 x n][Vol i32 x n][Seite i8 x n] (little endian). */
export function decodeChunk(buf: ArrayBuffer): DecodedChunk {
  if (buf.byteLength % ROW_BYTES !== 0) throw new Error(`Abschnittsgroesse ${buf.byteLength} ist kein Vielfaches von ${ROW_BYTES}`);
  const n = buf.byteLength / ROW_BYTES;
  return {
    n,
    t: new Float64Array(buf, 0, n),
    price: new Float64Array(buf, 8 * n, n),
    vol: new Int32Array(buf, 16 * n, n),
    side: new Int8Array(buf, 20 * n, n),
  };
}

/**
 * Haengt einen Abschnitt LUECKENLOS an (offset muss genau dem bisher Geladenen entsprechen). Fail-closed:
 * Ueberlappung, Luecke, Ueberlauf, fallende Zeit, Preis/Volumen <= 0 oder unbekannte Seite -> Fehler.
 */
export function appendChunk(store: TickStore, offset: number, chunk: DecodedChunk): void {
  if (offset !== store.loaded) throw new Error(`Abschnitt bei ${offset}, erwartet ${store.loaded} (keine Luecken/Ueberlappungen)`);
  if (offset + chunk.n > store.total) throw new Error("Abschnitt ueberschreitet die Session-Laenge");
  let prev = offset > 0 ? store.t[offset - 1] : -Infinity;
  for (let k = 0; k < chunk.n; k++) {
    const tk = chunk.t[k];
    if (!(tk >= prev)) throw new Error(`Nicht chronologisch bei Trade ${offset + k}`);
    if (!(chunk.price[k] > 0) || !(chunk.vol[k] > 0)) throw new Error(`Ungueltiger Preis/Volumen bei Trade ${offset + k}`);
    const s = chunk.side[k];
    if (s !== 1 && s !== -1 && s !== 0) throw new Error(`Ungueltige Seite bei Trade ${offset + k}`);
    prev = tk;
  }
  store.t.set(chunk.t, offset);
  store.price.set(chunk.price, offset);
  store.vol.set(chunk.vol, offset);
  store.side.set(chunk.side, offset);
  store.loaded = offset + chunk.n;
}

/** Kerze aus angewandten Trades. Bei der laufenden Kerze: Stand bis zur Replay-Position. */
export interface ReplayCandle {
  /** Intervallnummer relativ zum Anker: [bucket*size, (bucket+1)*size). */
  bucket: number;
  firstIdx: number;
  lastIdx: number;
  open: number;
  high: number;
  low: number;
  close: number;
  volume: number;
  buyVolume: number;
  sellVolume: number;
  trades: number;
}

export function bucketOf(t: number, bucketSize: number = BUCKET_5M): number {
  return Math.floor(t / bucketSize);
}

/** Anzahl geladener Trades mit Zeit <= tLimit (obere Grenze; Trades genau auf tLimit gelten als erreicht). */
export function countAtOrBefore(store: TickStore, tLimit: number): number {
  let lo = 0, hi = store.loaded;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if (store.t[mid] <= tLimit) lo = mid + 1; else hi = mid;
  }
  return lo;
}

/** Anzahl geladener Trades mit Zeit < tLimit (alles VOR einem Zeitpunkt). */
export function countBefore(store: TickStore, tLimit: number): number {
  let lo = 0, hi = store.loaded;
  while (lo < hi) {
    const mid = (lo + hi) >>> 1;
    if (store.t[mid] < tLimit) lo = mid + 1; else hi = mid;
  }
  return lo;
}

export class TickReplayEngine {
  readonly store: TickStore;
  readonly bucketSize: number;
  /** Anzahl angewandter Trades (Replay-Position). */
  p = 0;
  /** Abgeschlossene Kerzen (Endwerte, nur aus Trades vor p). */
  closed: ReplayCandle[] = [];
  /** Laufende Kerze (Teilstand bis p) oder null vor dem ersten Trade. */
  cur: ReplayCandle | null = null;
  /** Endwerte je Kerzen-Ordinal aus dem Vorwaertslauf (fuer exakte Ruecksprunge). */
  private finals: ReplayCandle[] = [];
  /** Index des ersten Trades je Kerzen-Ordinal. */
  private firstIdxByOrdinal: number[] = [];

  constructor(store: TickStore, bucketSize: number = BUCKET_5M) {
    if (!(bucketSize > 0) || !Number.isInteger(bucketSize)) throw new Error("Ungueltige Intervallgroesse");
    this.store = store;
    this.bucketSize = bucketSize;
  }

  reset(): void {
    this.p = 0;
    this.closed = [];
    this.cur = null;
  }

  /** Wendet genau den naechsten (geladenen) Trade an. false, wenn keiner mehr geladen ist. */
  step(): boolean {
    if (this.p >= this.store.loaded) return false;
    this.applyOne();
    return true;
  }

  /** Spielt vorwaerts bis Position target (begrenzt auf das Geladene). Jeder Trade wird einzeln angewandt. */
  advanceTo(target: number): void {
    const end = Math.min(target, this.store.loaded);
    while (this.p < end) this.applyOne();
  }

  /** Setzt die Position exakt auf target (vor- oder rueckwaerts) – Ergebnis identisch zum Abspielen bis target. */
  seek(target: number): void {
    const tgt = Math.max(0, Math.min(Math.floor(target), this.store.loaded));
    if (tgt >= this.p) { this.advanceTo(tgt); return; }
    if (tgt === 0) { this.reset(); return; }
    const b = bucketOf(this.store.t[tgt - 1], this.bucketSize);
    // Kerzen vor der Ziel-Kerze sind im Vorwaertslauf bereits abgeschlossen worden (sonst waere p nicht > tgt).
    let k = 0;
    while (k < this.firstIdxByOrdinal.length && this.firstIdxByOrdinal[k] < tgt
      && bucketOf(this.store.t[this.firstIdxByOrdinal[k]], this.bucketSize) < b) k++;
    this.closed = this.finals.slice(0, k);
    this.cur = null;
    this.p = this.firstIdxByOrdinal[k];
    this.advanceTo(tgt);
  }

  private applyOne(): void {
    const s = this.store;
    const i = this.p;
    const px = s.price[i];
    const v = s.vol[i];
    const b = bucketOf(s.t[i], this.bucketSize);
    let c = this.cur;
    if (c === null || b !== c.bucket) {
      if (c !== null) {
        this.closed.push(c);
        this.finals[this.closed.length - 1] = c; // ab hier unveraenderlich (neue Kerze = neues Objekt)
      }
      c = { bucket: b, firstIdx: i, lastIdx: i, open: px, high: px, low: px, close: px, volume: 0, buyVolume: 0, sellVolume: 0, trades: 0 };
      this.firstIdxByOrdinal[this.closed.length] = i;
      this.cur = c;
    }
    if (px > c.high) c.high = px;
    if (px < c.low) c.low = px;
    c.close = px;
    c.volume += v;
    if (s.side[i] > 0) c.buyVolume += v; else if (s.side[i] < 0) c.sellVolume += v;
    c.trades++;
    c.lastIdx = i;
    this.p = i + 1;
  }

  /** Zeit des zuletzt angewandten Trades (100 ns relativ zum Anker) oder null. */
  lastTime(): number | null {
    return this.p > 0 ? this.store.t[this.p - 1] : null;
  }

  /** Alle bis p sichtbaren Kerzen (abgeschlossene + laufende), chronologisch. */
  candles(): ReplayCandle[] {
    return this.cur ? [...this.closed, this.cur] : [...this.closed];
  }
}

/**
 * Unabhaengige Referenz-Aggregation (naiv, ohne Engine-Zustand): OHLCV je Intervall aus den Trades [0, p).
 * Dient Tests und Abgleichen; die Engine muss bei jeder Position exakt dasselbe liefern.
 */
export function aggregateReference(store: TickStore, p: number, bucketSize: number = BUCKET_5M): ReplayCandle[] {
  const out: ReplayCandle[] = [];
  const end = Math.min(p, store.loaded);
  for (let i = 0; i < end; i++) {
    const b = bucketOf(store.t[i], bucketSize);
    let c = out.length ? out[out.length - 1] : null;
    if (!c || c.bucket !== b) {
      c = { bucket: b, firstIdx: i, lastIdx: i, open: store.price[i], high: -Infinity, low: Infinity, close: 0, volume: 0, buyVolume: 0, sellVolume: 0, trades: 0 };
      out.push(c);
    }
    c.high = Math.max(c.high, store.price[i]);
    c.low = Math.min(c.low, store.price[i]);
    c.close = store.price[i];
    c.volume += store.vol[i];
    if (store.side[i] > 0) c.buyVolume += store.vol[i]; else if (store.side[i] < 0) c.sellVolume += store.vol[i];
    c.trades++;
    c.lastIdx = i;
  }
  return out;
}

/** Status einer angezeigten Kerze – ehrlich gekennzeichnet. */
export type CandleStatus = "abgeschlossen" | "laufend" | "rand-unvollstaendig";

/**
 * Status einer Kerze relativ zur Replay-Uhr (100 ns, Anker-relativ) und zum Session-Fenster [winStart, winEnd).
 *  - rand-unvollstaendig: das Intervall liegt nur teilweise im Aufnahmefenster (Randkerze).
 *  - laufend: Replay-Uhr hat das Intervallende noch nicht erreicht und es koennen weitere Trades folgen.
 *  - abgeschlossen: Intervallende erreicht oder keine weiteren Trades der Session vorhanden.
 */
export function candleStatus(c: ReplayCandle, bucketSize: number, clock: number, winStart: number, winEnd: number, sessionExhausted: boolean): CandleStatus {
  const s = c.bucket * bucketSize, e = s + bucketSize;
  if (s < winStart || e > winEnd) return "rand-unvollstaendig";
  if (clock >= e || sessionExhausted) return "abgeschlossen";
  return "laufend";
}

/** Abspieluhr: Marktzeit in 100 ns, die mit Faktor speed relativ zur Wandzeit laeuft. */
export function advanceClock(clock: number, wallMs: number, speed: number): number {
  return clock + Math.max(0, wallMs) * speed * UNITS_PER_MS;
}
