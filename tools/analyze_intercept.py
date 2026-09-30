#!/usr/bin/env python
"""Resumen de un vuelo de intercept a partir de la caja negra (DataLog.csv).

Solo lectura, sin dependencias (stdlib). Uso:

    python tools/analyze_intercept.py                    # DataLog.csv por defecto
    python tools/analyze_intercept.py --previous         # DataLog.previous.csv
    python tools/analyze_intercept.py ruta\\DataLog.csv --all   # todos los intercepts
    python tools/analyze_intercept.py --window 0.0 --tail 8      # ventana libre (s) desde el Inicio

Por cada intercept (marca 'Inicio: interceptar' .. 'Fin:'/siguiente 'Inicio:'/EOF)
y por cada fase (Pursuit/Closing/Station, deducida de la columna Accion y de las
marcas 'Fase:'), imprime:
  - duracion, dt medio/max de muestreo (jitter del log, ~110 ms normal)
  - G normal: media, sigma, min/max, sigma de la parte de alta frecuencia
    (G - media movil 1 s), % de tiempo con G<0.3 y G>GSOFT
  - tasas de cabeceo/alabeo: sigma y sigma de alta frecuencia
  - chatter de mando: cambios de signo/s del incremento del yugo y variacion
    total por s (pitch, roll), % de tiempo saturado (|mando|>0.95)
  - error de seguimiento RMS pitch/bank (obj vs real), saltos maximos del objetivo
  - frecuencia dominante de G, pitch rate y roll rate (DFT sobre serie a 10 Hz)
  - inversion de giro (rumbo se mueve contra el signo del alabeo: G negativa)
  - marcas: rango/sep/cierre/err long-lat-vert de cada 'Fase:'/'Fin:', distancia
    minima al blanco vista en marcas, y si hubo adelantamiento (err long<0 con
    rango<StationExit o cierre negativo tras estar cerca)
"""
import argparse
import csv
import math
import os
import re
import sys

DEFAULT_DIR = r"E:\X-Plane 12\Resources\plugins\AICopilot\win_x64"
GSOFT = 6.0
CH = ["Hora", "Fase", "Accion", "Nota", "IAS_kt", "ALT_ft", "AGL_ft", "VS_fpm",
      "PitchObj_deg", "PitchReal_deg", "BankObj_deg", "BankReal_deg",
      "RumboObj_deg", "RumboReal_deg", "G", "ThrottleObj_pct", "ThrottleReal_pct",
      "FlapsObj_pct", "FlapsReal_pct", "MandoPitch", "MandoRoll", "MandoYaw",
      "G_obj", "G_pred", "AoA_deg", "Aerofrenos_pct", "Peso_lb", "Mach",
      "PitchRate_dps", "RollRate_dps", "Adaptacion", "Proteccion"]


def fnum(s):
    try:
        return float(s)
    except (ValueError, TypeError):
        return float("nan")


def tsec(hora):
    h, m, rest = hora.split(":")
    return int(h) * 3600 + int(m) * 60 + float(rest)


def load(path):
    rows = []
    with open(path, newline="", encoding="utf-8-sig", errors="replace") as f:
        rd = csv.reader(f, delimiter=";")
        header = next(rd, None)
        if not header:
            return rows
        idx = {n: i for i, n in enumerate(header)}
        t0 = None
        prev = None
        day = 0.0
        for r in rd:
            if len(r) < 4 or ":" not in r[0]:
                continue
            try:
                t = tsec(r[0])
            except ValueError:
                continue
            if prev is not None and t + day < prev - 1:
                day += 86400.0
            t += day
            prev = t
            d = {"t": t, "nota": r[idx["Nota"]] if "Nota" in idx else ""}
            for n in CH:
                if n in idx and idx[n] < len(r):
                    d[n] = r[idx[n]]
            rows.append(d)
    return rows


def split_runs(rows, take_all):
    """Devuelve lista de (i0, i1) sobre rows por intercept."""
    starts = [i for i, r in enumerate(rows) if r["nota"].startswith("Inicio: interceptar")]
    runs = []
    for k, s in enumerate(starts):
        end = len(rows)
        for j in range(s + 1, len(rows)):
            n = rows[j]["nota"]
            if n.startswith("Fin: interceptacion") or n.startswith("Inicio:"):
                end = j + 1
                break
        runs.append((s, end))
    return runs if take_all else runs[-1:]


def mean(a):
    return sum(a) / len(a) if a else float("nan")


def std(a):
    if len(a) < 2:
        return float("nan")
    m = mean(a)
    return math.sqrt(sum((x - m) ** 2 for x in a) / (len(a) - 1))


def rms(a):
    return math.sqrt(sum(x * x for x in a) / len(a)) if a else float("nan")


def highpass(ts, xs, win=1.0):
    """x - media movil de +-win/2 segundos."""
    out = []
    j0 = 0
    for i, t in enumerate(ts):
        while ts[j0] < t - win / 2:
            j0 += 1
        j1 = i
        while j1 + 1 < len(ts) and ts[j1 + 1] <= t + win / 2:
            j1 += 1
        out.append(xs[i] - mean(xs[j0:j1 + 1]))
    return out


def dom_freq(ts, xs, fmin=0.15, fmax=4.5):
    """Frecuencia dominante (Hz) por DFT sobre serie remuestreada a 10 Hz."""
    if len(ts) < 20:
        return float("nan"), float("nan")
    fs = 10.0
    n = int((ts[-1] - ts[0]) * fs)
    if n < 20:
        return float("nan"), float("nan")
    ser = []
    j = 0
    for k in range(n):
        t = ts[0] + k / fs
        while j + 1 < len(ts) - 1 and ts[j + 1] < t:
            j += 1
        t0, t1 = ts[j], ts[j + 1]
        a = 0 if t1 == t0 else (t - t0) / (t1 - t0)
        ser.append(xs[j] + a * (xs[j + 1] - xs[j]))
    m = mean(ser)
    ser = [(x - m) * (0.5 - 0.5 * math.cos(2 * math.pi * i / (n - 1))) for i, x in enumerate(ser)]
    best, bf = 0.0, float("nan")
    k = 1
    while k <= n // 2:
        f = k * fs / n
        if f >= fmin and f <= fmax:
            re_ = sum(x * math.cos(2 * math.pi * f * i / fs) for i, x in enumerate(ser))
            im = sum(x * math.sin(2 * math.pi * f * i / fs) for i, x in enumerate(ser))
            p = math.hypot(re_, im) * 2 / n
            if p > best:
                best, bf = p, f
        k += 1
    return bf, best


def chatter(ts, xs):
    """(cambios de signo/s del incremento con |d|>0.02, variacion total/s)."""
    if len(xs) < 3:
        return float("nan"), float("nan")
    flips, tv, last = 0, 0.0, 0.0
    for i in range(1, len(xs)):
        d = xs[i] - xs[i - 1]
        tv += abs(d)
        if abs(d) > 0.02:
            if last and (d > 0) != (last > 0):
                flips += 1
            last = d
    dur = ts[-1] - ts[0]
    return flips / dur, tv / dur


def phase_of(accion):
    a = accion or ""
    if "Persecucion" in a:
        return "Pursuit"
    if "Acercamiento" in a:
        return "Closing"
    if "formacion" in a:
        return "Station"
    if "Esperando" in a:
        return "WaitingTakeoff"
    if "perdido" in a:
        return "Lost"
    if a.startswith("Interceptar"):
        return "Otra"
    return "post-intercept/-"


def series(rows, col):
    return [fnum(r.get(col)) for r in rows]


def clean(ts, xs):
    p = [(t, x) for t, x in zip(ts, xs) if not math.isnan(x)]
    return [a for a, _ in p], [b for _, b in p]


def stats_block(name, rows):
    ts = [r["t"] for r in rows]
    n = len(rows)
    if n < 3:
        print(f"  [{name}] {n} muestras: insuficiente")
        return
    dts = [ts[i] - ts[i - 1] for i in range(1, n)]
    print(f"  [{name}] {n} muestras, {ts[-1]-ts[0]:.1f} s | dt medio {mean(dts)*1000:.0f} ms, "
          f"max {max(dts)*1000:.0f} ms, sigma {std(dts)*1000:.0f} ms")

    g_t, g = clean(ts, series(rows, "G"))
    if len(g) >= 3:
        hp = highpass(g_t, g)
        low = 100.0 * sum(1 for x in g if x < 0.3) / len(g)
        high = 100.0 * sum(1 for x in g if x > GSOFT) / len(g)
        f, a = dom_freq(g_t, g)
        print(f"    G: media {mean(g):.2f} sigma {std(g):.2f} min {min(g):.2f} max {max(g):.2f} | "
              f"sigma HF(1s) {std(hp):.3f} | G<0.3: {low:.0f}% G>{GSOFT:.0f}: {high:.0f}% | "
              f"dom {f:.2f} Hz amp {a:.2f}")
    for lab, col in (("cabeceo Q", "PitchRate_dps"), ("alabeo P", "RollRate_dps")):
        tt, x = clean(ts, series(rows, col))
        if len(x) >= 3:
            f, a = dom_freq(tt, x)
            print(f"    {lab}: sigma {std(x):.1f} dps, sigma HF {std(highpass(tt, x)):.2f}, "
                  f"max|.| {max(abs(v) for v in x):.1f}, dom {f:.2f} Hz amp {a:.1f}")
    for lab, col in (("yugo pitch", "MandoPitch"), ("yugo roll", "MandoRoll")):
        tt, x = clean(ts, series(rows, col))
        if len(x) >= 3:
            fl, tv = chatter(tt, x)
            sat = 100.0 * sum(1 for v in x if abs(v) > 0.95) / len(x)
            print(f"    {lab}: chatter {fl:.2f} cambios-signo/s, variacion {tv:.2f}/s, "
                  f"saturado {sat:.0f}%, rango [{min(x):.2f},{max(x):.2f}]")
    for lab, o, r_ in (("pitch", "PitchObj_deg", "PitchReal_deg"), ("bank", "BankObj_deg", "BankReal_deg")):
        ob, re_ = series(rows, o), series(rows, r_)
        err = [a - b for a, b in zip(ob, re_) if not (math.isnan(a) or math.isnan(b))]
        to, xo = clean(ts, ob)
        if err and len(xo) > 2:
            jumps = [abs(xo[i] - xo[i - 1]) / max(to[i] - to[i - 1], 1e-3) for i in range(1, len(xo))]
            print(f"    {lab}: error obj-real RMS {rms(err):.1f} deg, max {max(abs(e) for e in err):.1f}; "
                  f"vel. max del objetivo {max(jumps):.0f} deg/s")
    # Rumbo obj (si algun dia se registra)
    ho = [v for v in series(rows, "RumboObj_deg") if not math.isnan(v)]
    if not ho:
        print("    (RumboObj_deg vacio durante intercept: falta el setpoint de rumbo/track)")
    # Inversion de giro: rumbo se mueve contra el signo del alabeo
    inv = 0
    tot = 0
    for i in range(1, n):
        b = fnum(rows[i].get("BankReal_deg"))
        h0, h1 = fnum(rows[i - 1].get("RumboReal_deg")), fnum(rows[i].get("RumboReal_deg"))
        dt = ts[i] - ts[i - 1]
        if math.isnan(b) or math.isnan(h0) or math.isnan(h1) or dt <= 0 or abs(b) < 30:
            continue
        dh = (h1 - h0 + 180) % 360 - 180
        rate = dh / dt
        tot += 1
        # convencion X-Plane: bank positivo = ala derecha abajo = rumbo aumenta
        if abs(rate) > 3 and (rate > 0) != (b > 0):
            inv += 1
    if tot:
        print(f"    giro invertido (rumbo contra alabeo, |bank|>30): {inv}/{tot} muestras")


def parse_marks(rows):
    out = []
    for r in rows:
        n = r["nota"]
        if not (n.startswith("Fase:") or n.startswith("Fin:") or n.startswith("Inicio:") or n.startswith("AVISO")):
            continue
        d = {"t": r["t"], "txt": n}
        for k, pat in (("rango", r"rango=(-?[\d.]+)m"), ("sep", r"sep=(-?[\d.]+)m"),
                       ("cierre", r"cierre=([+-]?[\d.]+)kt"), ("along", r"err long=([+-]?[\d.]+)"),
                       ("cross", r"lat=([+-]?[\d.]+) vert"), ("vert", r"vert=([+-]?[\d.]+)m")):
            m = re.search(pat, n)
            d[k] = float(m.group(1)) if m else None
        out.append(d)
    return out


def analyze(rows, i0, i1, tail):
    seg = rows[i0:i1]
    t0 = seg[0]["t"]
    print("=" * 78)
    print(f"INTERCEPT  {seg[0]['nota'][:110]}")
    print(f"           duracion {seg[-1]['t']-t0:.1f} s (filas {i0}-{i1})")
    fin = [r for r in seg if r["nota"].startswith("Fin:")]
    if fin:
        print(f"           {fin[-1]['nota'][:200]}")
    samples = [r for r in seg if r.get("IAS_kt", "") != ""]
    if not samples:
        print("  sin muestras de telemetria")
        return
    # el intercept sigue hasta el Fin, pero las muestras posteriores (accion '-')
    # hasta 'tail' s se incluyen como fase 'post' para ver la recuperacion
    end_t = seg[-1]["t"]
    post = [r for r in rows[i1:i1 + 400] if r.get("IAS_kt", "") != "" and r["t"] <= end_t + tail]
    groups = {}
    for r in samples:
        groups.setdefault(phase_of(r.get("Accion")), []).append(r)
    stats_block("TOTAL intercept", samples)
    for name, g in groups.items():
        if name != "post-intercept/-" and len(groups) > 1:
            stats_block(f"fase {name}", g)
    if post and tail > 0:
        stats_block(f"recuperacion +{tail:.0f}s", post)

    marks = parse_marks(seg)
    print("  Marcas:")
    for m in marks:
        rel = m["t"] - t0
        extra = " ".join(f"{k}={m[k]:g}" for k in ("rango", "sep", "cierre", "along", "cross", "vert") if m[k] is not None)
        print(f"    +{rel:6.1f}s {m['txt'][:70]}  {extra}")
    seps = [m["sep"] for m in marks if m["sep"] is not None]
    rngs = [m["rango"] for m in marks if m["rango"] is not None]
    if seps or rngs:
        print(f"  Distancia minima vista en marcas: sep {min(seps) if seps else float('nan'):.0f} m, "
              f"rango {min(rngs) if rngs else float('nan'):.0f} m (solo en marcas; no hay columna de rango)")
    over = [m for m in marks if m["along"] is not None and m["along"] < -50 and (m["rango"] or 1e9) < 500]
    print(f"  Adelantamiento (err long < -50 m con rango < 500 m): {'SI' if over else 'no detectado'}")
    phases = [m for m in marks if m["txt"].startswith("Fase:")]
    if len(phases) >= 2:
        gaps = [phases[i]["t"] - phases[i - 1]["t"] for i in range(1, len(phases))]
        print(f"  Cambios de fase: {len(phases)} (min entre cambios {min(gaps):.1f} s) "
              f"{'-> POSIBLE HISTERESIS INSUFICIENTE (resiembra autothrottle)' if min(gaps) < 3 else ''}")
    thr = series(samples, "ThrottleObj_pct")
    tt, thr = clean([r["t"] for r in samples], thr)
    if len(thr) > 3:
        fl, tv = chatter(tt, [x / 100 for x in thr])
        print(f"  Gas obj: chatter {fl:.2f} cambios-signo/s, variacion {tv:.2f}/s, "
              f"min {min(thr):.0f}% max {max(thr):.0f}%")
    if len(samples) < 60:
        print(f"  AVISO: solo {len(samples)} muestras (~{len(samples)/9:.0f} s); las estadisticas de "
              "frecuencia no son fiables por debajo de ~20 s.")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("csv", nargs="?", help="DataLog.csv (por defecto el de X-Plane)")
    ap.add_argument("--previous", action="store_true", help="usar DataLog.previous.csv")
    ap.add_argument("--all", action="store_true", help="todos los intercepts (por defecto solo el ultimo)")
    ap.add_argument("--tail", type=float, default=3.0, help="s de recuperacion tras el Fin a incluir")
    a = ap.parse_args()
    path = a.csv or os.path.join(DEFAULT_DIR, "DataLog.previous.csv" if a.previous else "DataLog.csv")
    if not os.path.exists(path):
        print(f"No existe {path}")
        return 1
    rows = load(path)
    print(f"{path}: {len(rows)} filas")
    runs = split_runs(rows, a.all)
    if not runs:
        print("No hay marca 'Inicio: interceptar' en este fichero.")
        return 0
    for i0, i1 in runs:
        analyze(rows, i0, i1, a.tail)
    return 0


if __name__ == "__main__":
    sys.exit(main())
