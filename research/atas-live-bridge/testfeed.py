"""TESTDATEN-Feed für die Marktdaten-Ansicht (KEINE Marktdaten, KEIN ATAS).

Spricht dasselbe Protokoll wie der ATAS-Indikator „OF Live Bridge (read-only)“ (Schema atas-live-1), meldet sich aber mit
source="test" und Instrument „MNQ-TEST“ – das Dashboard kennzeichnet alles als Testdaten. Damit lassen sich Anzeige,
Zähler, Verbindungsabbruch, Quellenwechsel und der Kerzenvergleich prüfen, solange kein echter ATAS-Feed läuft.

  python testfeed.py [--port 5036] [--seconds 150] [--rate 40] [--disconnect-after 0] [--seed 1] [--gap-every 0]

--disconnect-after N: nach N Sekunden Verbindung schließen (Prüfung „getrennt/veraltet“).
--gap-every N: jede N-te Sequenznummer auslassen (Prüfung „verlorene Ereignisse werden gezählt“).
"""
import argparse, json, random, socket, sys, time, uuid
from datetime import datetime, timezone


def iso(t: float) -> str:
    return datetime.fromtimestamp(t, tz=timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f") + "0Z"


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=5036)
    ap.add_argument("--seconds", type=float, default=150)
    ap.add_argument("--rate", type=float, default=40, help="Trades je Sekunde")
    ap.add_argument("--disconnect-after", type=float, default=0)
    ap.add_argument("--seed", type=int, default=1)
    ap.add_argument("--gap-every", type=int, default=0)
    a = ap.parse_args(argv)
    rnd = random.Random(a.seed)
    s = socket.create_connection(("127.0.0.1", a.port))
    seq = 0
    sent = 0

    def send(obj):
        nonlocal seq, sent
        if obj["type"] != "hello":
            seq += 1
            if a.gap_every and seq % a.gap_every == 0:
                seq += 1  # Lücke erzeugen
            obj["seq"] = seq
        s.sendall((json.dumps(obj, separators=(",", ":")) + "\n").encode())
        sent += 1

    send({"type": "hello", "schema": "atas-live-1", "source": "test", "instrument": "MNQ-TEST", "exchange": "TEST", "tickSize": 0.25,
          "chartType": "TimeFrame", "timeFrame": "1", "atasVersion": "TESTDATEN", "bridge": "testfeed.py", "sessionId": uuid.uuid4().hex})
    price = 21000.0
    bid, ask = price - 0.25, price
    book = {"Bid": {bid - i * 0.25: rnd.randint(1, 30) for i in range(10)}, "Ask": {ask + i * 0.25: rnd.randint(1, 30) for i in range(10)}}
    send({"type": "domSnapshot", "t": iso(time.time()), "levels": [{"side": sd, "p": p, "v": v} for sd in book for p, v in book[sd].items()]})
    t0 = time.time()
    bar_idx, bar = 0, None
    cum_id, cum = 0, None
    last_hb = t0
    while time.time() - t0 < a.seconds:
        if a.disconnect_after and time.time() - t0 >= a.disconnect_after:
            break
        now = time.time()
        minute = int(now // 60) * 60
        if bar is None or bar["start"] != minute:
            if bar is not None:
                send(bar_msg(bar, bar_idx, True))
                bar_idx += 1
            bar = {"start": minute, "o": None, "h": -1e18, "l": 1e18, "c": None, "v": 0, "bid": 0, "ask": 0, "n": 0, "pv": 0.0, "fp": {}}
        side = "Buy" if rnd.random() < 0.5 else "Sell"
        price = max(1.0, price + rnd.choice([-0.25, 0, 0, 0.25]))
        vol = rnd.choice([1, 1, 1, 2, 3])
        send({"type": "trade", "t": iso(now), "r": iso(now), "p": price, "v": vol, "d": side})
        b = bar
        b["o"] = price if b["o"] is None else b["o"]; b["h"] = max(b["h"], price); b["l"] = min(b["l"], price); b["c"] = price
        b["v"] += vol; b["n"] += 1; b["pv"] += price * vol
        f = b["fp"].setdefault(price, [0, 0])
        if side == "Buy": b["ask"] += vol; f[1] += vol
        else: b["bid"] += vol; f[0] += vol
        # Bid/Ask + DOM
        bid, ask = price - 0.25, price
        send({"type": "bbo", "t": iso(now), "side": "Bid", "p": bid, "v": rnd.randint(1, 20)})
        send({"type": "bbo", "t": iso(now), "side": "Ask", "p": ask, "v": rnd.randint(1, 20)})
        send({"type": "dom", "t": iso(now), "side": rnd.choice(["Bid", "Ask"]), "p": price + rnd.choice([-1, -0.5, 0.5, 1]), "v": rnd.randint(0, 25)})
        # kumulierte Trades: gleiche Richtung -> Update (Volumen kumuliert), sonst neu
        if cum and cum["d"] == side and rnd.random() < 0.6:
            cum["v"] += vol; cum["n"] += 1; cum["p1"] = price
            send({"type": "cum", "id": cum["id"], "ev": "update", "t": cum["t"], "p0": cum["p0"], "p1": price, "v": cum["v"], "n": cum["n"], "d": side})
        else:
            cum_id += 1
            cum = {"id": cum_id, "t": iso(now), "p0": price, "p1": price, "v": vol, "n": 1, "d": side}
            send({"type": "cum", "id": cum_id, "ev": "new", "t": cum["t"], "p0": price, "p1": price, "v": vol, "n": 1, "d": side})
        if now - last_hb >= 1:
            send({"type": "hb", "r": iso(now), "queue": 0, "drops": {}})
            send(bar_msg(bar, bar_idx, False))
            last_hb = now
        time.sleep(1.0 / a.rate)
    if bar is not None and not a.disconnect_after:
        send(bar_msg(bar, bar_idx, True))
    s.close()
    print(json.dumps({"sent": sent, "lastSeq": seq, "disconnected": bool(a.disconnect_after)}))


def bar_msg(b, idx, closed):
    return {"type": "bar", "bar": idx, "closed": closed, "t": iso(b["start"]), "tLast": iso(b["start"] + 59.999),
            "o": b["o"], "h": b["h"], "l": b["l"], "c": b["c"], "v": b["v"], "bid": b["bid"], "ask": b["ask"], "delta": b["ask"] - b["bid"],
            "ticks": b["n"], "vwap": round(b["pv"] / b["v"], 6) if b["v"] else 0,
            "fp": [{"p": p, "b": v[0], "a": v[1]} for p, v in sorted(b["fp"].items())]}


if __name__ == "__main__":
    sys.exit(main())
