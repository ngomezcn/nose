# -*- coding: utf-8 -*-
"""Deeper intercept overshoot / orbit analysis from Adaptacion field."""
import csv
import math
import re
from pathlib import Path

CSV_PATH = Path(r"E:\X-Plane 12\Resources\plugins\AICopilot\win_x64\DataLog.csv")
OUT = Path(r"c:\Users\naimg\Desktop\AICopilot\devtools\_bb_intercept_deep.txt")


def fnum(s, default=float("nan")):
    if s is None or s == "" or s == "-":
        return default
    try:
        return float(s)
    except ValueError:
        return default


def ang_diff(a, b):
    return (a - b + 180.0) % 360.0 - 180.0


def unwrap(series):
    if not series:
        return []
    out = [series[0]]
    for h in series[1:]:
        out.append(out[-1] + ang_diff(h, out[-1] % 360.0))
    return out


def parse_adapt(adapt):
    """Parse 'int rng=34165 sep=34310 cl=185 al=25598' and optional lat/long if present."""
    d = {}
    if not adapt:
        return d
    for key in ("rng", "sep", "cl", "al", "lat", "long", "vert", "el"):
        m = re.search(rf"\b{key}=(-?\d+(?:\.\d+)?)", adapt)
        if m:
            d[key] = float(m.group(1))
    return d


def parse_phase_nota(nota):
    d = {}
    for key, pat in (
        ("rango", r"rango=(-?\d+(?:\.\d+)?)m"),
        ("cierre_kt", r"cierre=([+-]?\d+(?:\.\d+)?)kt"),
        ("err_long", r"err long=([+-]?\d+(?:\.\d+)?)"),
        ("err_lat", r"lat=([+-]?\d+(?:\.\d+)?)"),
        ("err_vert", r"vert=([+-]?\d+(?:\.\d+)?)m"),
        ("bankObj", r"bankObj=([+-]?\d+(?:\.\d+)?)"),
        ("IASobj", r"IASobj=(\d+(?:\.\d+)?)"),
    ):
        m = re.search(pat, nota)
        if m:
            d[key] = float(m.group(1))
    return d


def main():
    rows = []
    with CSV_PATH.open(encoding="utf-8", errors="replace") as fh:
        for row in csv.DictReader(fh, delimiter=";"):
            rows.append(row)

    # Find intercept window
    start = next(i for i, r in enumerate(rows) if (r.get("Nota") or "").startswith("Inicio: interceptar"))
    end = len(rows) - 1
    for i in range(start + 1, len(rows)):
        n = r.get("Nota") if False else (rows[i].get("Nota") or "")
        if n.startswith("Fin: grabacion"):
            end = i
            break

    lines = []
    telem = []
    for i in range(start, end + 1):
        row = rows[i]
        ias = fnum(row.get("IAS_kt"))
        nota = (row.get("Nota") or "").strip()
        if math.isnan(ias):
            if nota.startswith("Fase:") or nota.startswith("Inicio:") or nota.startswith("Fin:"):
                pe = parse_phase_nota(nota)
                lines.append(f"EVENT {row.get('Hora')} {nota[:180]}")
                lines.append(f"       parsed={pe}")
            continue
        adapt = (row.get("Adaptacion") or "").strip()
        ad = parse_adapt(adapt)
        s = {
            "hora": row.get("Hora", ""),
            "ias": ias,
            "hdg": fnum(row.get("RumboReal_deg")),
            "hdg_obj": fnum(row.get("RumboObj_deg")),
            "bank": fnum(row.get("BankReal_deg")),
            "bank_obj": fnum(row.get("BankObj_deg")),
            "g": fnum(row.get("G")),
            "mr": fnum(row.get("MandoRoll")),
            "rr": fnum(row.get("RollRate_dps")),
            "thr_obj": fnum(row.get("ThrottleObj_pct")),
            "thr": fnum(row.get("ThrottleReal_pct")),
            "accion": row.get("Accion") or "",
            "adapt": adapt,
            "prot": (row.get("Proteccion") or "").strip(),
            **ad,
        }
        telem.append(s)

    hdgu = unwrap([s["hdg"] for s in telem])
    for s, u in zip(telem, hdgu):
        s["hdg_u"] = u

    # Continuous series from Adaptacion rng
    with_rng = [s for s in telem if "rng" in s]
    lines.append(f"telem={len(telem)} with_rng={len(with_rng)}")
    if with_rng:
        rngs = [s["rng"] for s in with_rng]
        imin = min(range(len(with_rng)), key=lambda i: with_rng[i]["rng"])
        lines.append(
            f"rng min={with_rng[imin]['rng']:.0f}m @{with_rng[imin]['hora']} "
            f"hdg={with_rng[imin]['hdg']:.0f}/{with_rng[imin]['hdg_obj']:.0f} "
            f"bank={with_rng[imin]['bank']:+.0f}/{with_rng[imin]['bank_obj']:+.0f} "
            f"cl={with_rng[imin].get('cl')} sep={with_rng[imin].get('sep')}"
        )
        lines.append(f"rng start={with_rng[0]['rng']:.0f} end={with_rng[-1]['rng']:.0f}")

    # Detect heading revolutions: each time unwrap crosses +360 multiples from start
    lines.append("\n=== HEADING REVOLUTIONS ===")
    base = hdgu[0]
    crossed = set()
    for s in telem:
        revs = int((s["hdg_u"] - base) // 360)
        if revs >= 1 and revs not in crossed:
            crossed.add(revs)
            lines.append(
                f"  completed ~{revs} full turn(s) by {s['hora']} "
                f"hdg_u={s['hdg_u']:.0f} hdg={s['hdg']:.0f} bank={s['bank']:+.0f}/{s['bank_obj']:+.0f} "
                f"rng={s.get('rng')} Acc={s['accion'][:50]}"
            )
    lines.append(f"  net unwrap Δ={hdgu[-1]-hdgu[0]:.1f}° (~{(hdgu[-1]-hdgu[0])/360:.2f} turns)")

    # Detect sustained high |bank| turn segments (orbit candidates)
    lines.append("\n=== SUSTAINED |bank_obj|>=40 SEGMENTS ===")
    in_seg = False
    seg0 = None
    for i, s in enumerate(telem):
        hot = abs(s["bank_obj"]) >= 40
        if hot and not in_seg:
            in_seg = True
            seg0 = i
        elif (not hot) and in_seg:
            in_seg = False
            a, b = telem[seg0], telem[i - 1]
            dhdg = b["hdg_u"] - a["hdg_u"]
            lines.append(
                f"  {a['hora']}->{b['hora']} Δt~{(i-1-seg0)*0.1:.1f}s Δhdg={dhdg:+.0f}° "
                f"bank_obj {a['bank_obj']:+.0f}.. peak "
                f"sign={'L' if a['bank_obj']<0 else 'R'} "
                f"rng {a.get('rng')}->{b.get('rng')} "
                f"hdg {a['hdg']:.0f}->{b['hdg']:.0f}"
            )
    if in_seg:
        a, b = telem[seg0], telem[-1]
        dhdg = b["hdg_u"] - a["hdg_u"]
        lines.append(
            f"  {a['hora']}->{b['hora']} (to end) Δhdg={dhdg:+.0f}° "
            f"bank_obj~{a['bank_obj']:+.0f} rng {a.get('rng')}->{b.get('rng')}"
        )

    # Lateral overshoot: track bank sign vs closing
    # Sample every 2s with rng/cl
    lines.append("\n=== ~2s SERIES (rng, cl, bank, hdg) ===")
    step = 20
    for s in telem[::step]:
        lines.append(
            f"  {s['hora']} rng={s.get('rng', float('nan')):7.0f} "
            f"cl={s.get('cl', float('nan')):+6.0f} sep={s.get('sep', float('nan')):7.0f} "
            f"hdg={s['hdg']:6.1f}/{s['hdg_obj']:6.1f} "
            f"bank={s['bank']:+6.1f}/{s['bank_obj']:+6.1f} "
            f"G={s['g']:.2f} IAS={s['ias']:.0f} "
            f"{'P' if 'Persecucion' in s['accion'] else 'A' if 'Acercamiento' in s['accion'] else '?'}"
        )

    # Find zero-crossings of lateral from phase err_lat, and continuous if we can infer
    # Closest approaches (local minima of rng)
    lines.append("\n=== LOCAL MINIMA OF rng (closest approaches) ===")
    for i in range(2, len(with_rng) - 2):
        r0 = with_rng[i - 2]["rng"]
        r1 = with_rng[i - 1]["rng"]
        r2 = with_rng[i]["rng"]
        r3 = with_rng[i + 1]["rng"]
        r4 = with_rng[i + 2]["rng"]
        if r2 <= r1 and r2 <= r3 and r2 < r0 and r2 < r4 and r2 < 8000:
            s = with_rng[i]
            # look at bank before/after
            lines.append(
                f"  MIN rng={s['rng']:.0f} @{s['hora']} cl={s.get('cl')} "
                f"hdg={s['hdg']:.0f}/{s['hdg_obj']:.0f} bank={s['bank']:+.0f}/{s['bank_obj']:+.0f} "
                f"G={s['g']:.2f} Acc={s['accion'][:55]}"
            )

    # Detail around critical phase times
    critical_times = [
        "15:43:50",
        "15:44:40",
        "15:46:11",
        "15:46:53",
        "15:48:25",
        "15:49:07",
        "15:49:25",
        "15:49:45",
    ]
    lines.append("\n=== DETAIL ±8s AROUND PHASE CHANGES ===")
    for ct in critical_times:
        lines.append(f"--- around {ct} ---")
        for s in telem:
            if s["hora"].startswith(ct[:5]):  # HH:MM rough filter first
                # finer: within 8s of ct
                pass
        # parse HH:MM:SS
        hh, mm, ss = map(float, ct.split(":"))
        t0 = hh * 3600 + mm * 60 + ss
        for s in telem:
            parts = s["hora"].split(":")
            t = float(parts[0]) * 3600 + float(parts[1]) * 60 + float(parts[2])
            if abs(t - t0) <= 8:
                if int(round((t - t0) * 10)) % 5 == 0:  # ~0.5s
                    lines.append(
                        f"  {s['hora']} rng={s.get('rng', float('nan')):7.0f} cl={s.get('cl', float('nan')):+5.0f} "
                        f"hdg={s['hdg']:6.1f}/{s['hdg_obj']:6.1f} "
                        f"bank={s['bank']:+6.1f}/{s['bank_obj']:+6.1f} "
                        f"mr={s['mr']:+.2f} G={s['g']:.2f}"
                    )

    # Sign flip of bank_obj: when does commanded bank reverse?
    lines.append("\n=== BANK_OBJ SIGN FLIPS ===")
    prev_sign = 0
    for s in telem:
        if abs(s["bank_obj"]) < 8:
            continue
        sign = 1 if s["bank_obj"] > 0 else -1
        if prev_sign != 0 and sign != prev_sign:
            lines.append(
                f"  FLIP {s['hora']} bank_obj->{s['bank_obj']:+.1f} bank={s['bank']:+.1f} "
                f"hdg={s['hdg']:.0f}/{s['hdg_obj']:.0f} rng={s.get('rng')} cl={s.get('cl')} "
                f"Acc={s['accion'][:50]}"
            )
        prev_sign = sign

    # Throttle / speed around overshoots
    lines.append("\n=== SPEED/THROTTLE at phase markers windows ===")
    for ct in ["15:44:40", "15:46:53", "15:49:07", "15:49:25", "15:49:45"]:
        hh, mm, ss = map(float, ct.split(":"))
        t0 = hh * 3600 + mm * 60 + ss
        near = []
        for s in telem:
            parts = s["hora"].split(":")
            t = float(parts[0]) * 3600 + float(parts[1]) * 60 + float(parts[2])
            if abs(t - t0) < 1.0:
                near.append(s)
        if near:
            s = near[len(near) // 2]
            lines.append(
                f"  {s['hora']} IAS={s['ias']:.0f} thr={s['thr']:.0f}/{s['thr_obj']:.0f} "
                f"rng={s.get('rng')} cl={s.get('cl')} bank={s['bank']:+.0f}/{s['bank_obj']:+.0f}"
            )

    # Heading error vs bank command correlation near closest pass
    lines.append("\n=== CLOSEST PASS DETAIL (rng<3000) every ~0.5s ===")
    for s in with_rng:
        if s["rng"] < 3000:
            parts = s["hora"].split(":")
            # downsample
            frac = float(parts[2])
            if int(frac * 10) % 5 == 0:
                lines.append(
                    f"  {s['hora']} rng={s['rng']:.0f} cl={s.get('cl')} "
                    f"hdg={s['hdg']:.1f}/{s['hdg_obj']:.1f} errH={ang_diff(s['hdg_obj'], s['hdg']):+.0f} "
                    f"bank={s['bank']:+.1f}/{s['bank_obj']:+.1f} G={s['g']:.2f} "
                    f"Acc={'P' if 'Persecucion' in s['accion'] else 'A'}"
                )

    OUT.write_text("\n".join(lines), encoding="utf-8")
    print(f"wrote {OUT} lines={len(lines)}")


if __name__ == "__main__":
    main()
