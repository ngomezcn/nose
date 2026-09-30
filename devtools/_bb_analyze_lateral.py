# -*- coding: utf-8 -*-
"""Parse continuous al/cr from Adaptacion; pinpoint lateral overshoots."""
import csv
import math
import re
from pathlib import Path

CSV = Path(r"E:\X-Plane 12\Resources\plugins\AICopilot\win_x64\DataLog.csv")
OUT = Path(r"c:\Users\naimg\Desktop\AICopilot\devtools\_bb_lateral.txt")


def fnum(s, default=float("nan")):
    try:
        return float(s) if s not in (None, "", "-") else default
    except ValueError:
        return default


def ang_diff(a, b):
    return (a - b + 180.0) % 360.0 - 180.0


def unwrap(series):
    out = [series[0]]
    for h in series[1:]:
        out.append(out[-1] + ang_diff(h, out[-1] % 360.0))
    return out


def parse_adapt(a):
    d = {}
    for k in ("rng", "sep", "cl", "al", "cr", "vt", "trkObj", "trkErr", "iasObj",
              "vsObj", "tgtGS", "tgtTrk", "tgtTurn", "eta", "ovt", "minSep", "pG", "brk", "wv"):
        m = re.search(rf"\b{k}=(-?\d+(?:\.\d+)?)", a or "")
        if m:
            d[k] = float(m.group(1))
    m = re.search(r"\breg=(\w+)", a or "")
    if m:
        d["reg"] = m.group(1)
    return d


def main():
    rows = list(csv.DictReader(CSV.open(encoding="utf-8"), delimiter=";"))
    start = next(i for i, r in enumerate(rows) if (r.get("Nota") or "").startswith("Inicio: interceptar"))
    telem = []
    for r in rows[start:]:
        if math.isnan(fnum(r.get("IAS_kt"))):
            continue
        ad = parse_adapt(r.get("Adaptacion") or "")
        telem.append({
            "hora": r["Hora"],
            "hdg": fnum(r.get("RumboReal_deg")),
            "hdg_obj": fnum(r.get("RumboObj_deg")),
            "bank": fnum(r.get("BankReal_deg")),
            "bank_obj": fnum(r.get("BankObj_deg")),
            "g": fnum(r.get("G")),
            "ias": fnum(r.get("IAS_kt")),
            "accion": r.get("Accion") or "",
            "prot": (r.get("Proteccion") or "").strip(),
            **ad,
        })
    hu = unwrap([s["hdg"] for s in telem])
    for s, u in zip(telem, hu):
        s["hdg_u"] = u

    lines = []
    lines.append(f"n={len(telem)} with_cr={sum(1 for s in telem if 'cr' in s)}")

    # Zero crossings of cr (lateral): + = left of station/track, - = right
    # Convention: CrossM>0 => station to our right => we are LEFT of track line
    lines.append("\n=== LATERAL (cr) ZERO CROSSINGS (sign flip) ===")
    prev = None
    for s in telem:
        if "cr" not in s:
            continue
        cr = s["cr"]
        if abs(cr) < 50:
            # near zero - note closest
            pass
        sign = 1 if cr >= 0 else -1
        if prev is not None and sign != prev[1] and abs(prev[0]["cr"]) > 100 and abs(cr) > 100:
            # crossed through zero between samples - find nearer
            lines.append(
                f"  CROSS {prev[0]['hora']} cr={prev[0]['cr']:+.0f} -> {s['hora']} cr={cr:+.0f} "
                f"({'LEFT(+)' if sign>0 else 'RIGHT(-)'}) "
                f"rng={s.get('rng')} cl={s.get('cl')} "
                f"bank={s['bank']:+.0f}/{s['bank_obj']:+.0f} "
                f"hdg={s['hdg']:.0f}/{s['hdg_obj']:.0f} "
                f"al={s.get('al')} Acc={'P' if 'Persecucion' in s['accion'] else 'A'}"
            )
        prev = (s, sign)

    # Also find times when |cr| minimal while rng small
    lines.append("\n=== NEAR TRACK ( |cr|<200 ) with rng<5000 ===")
    for s in telem:
        if "cr" not in s:
            continue
        if abs(s["cr"]) < 200 and s.get("rng", 99999) < 5000:
            # downsample ~1s
            if int(float(s["hora"].split(":")[2]) * 10) % 10 == 0:
                lines.append(
                    f"  {s['hora']} cr={s['cr']:+.0f} al={s.get('al'):+.0f} rng={s['rng']:.0f} "
                    f"cl={s.get('cl'):+.0f} bank={s['bank']:+.0f}/{s['bank_obj']:+.0f} "
                    f"hdg={s['hdg']:.0f}/{s['hdg_obj']:.0f}"
                )

    # First overshoot cycle detail: from cr+ to cr- around 15:44
    lines.append("\n=== CYCLE1 approach+overshoot 15:44:00-15:45:20 (1Hz) ===")
    for s in telem:
        t = s["hora"]
        if t < "15:44:00" or t > "15:45:20":
            continue
        sec = float(t.split(":")[2])
        if int(sec) != sec and abs(sec - int(sec)) > 0.15:
            continue
        # every ~1s: when fractional part near .0-.2
        if (sec % 1) > 0.25:
            continue
        lines.append(
            f"  {s['hora']} cr={s.get('cr', float('nan')):+7.0f} al={s.get('al', float('nan')):+7.0f} "
            f"rng={s.get('rng', float('nan')):6.0f} cl={s.get('cl', float('nan')):+5.0f} "
            f"bank={s['bank']:+5.0f}/{s['bank_obj']:+5.0f} "
            f"hdg={s['hdg']:5.0f}/{s['hdg_obj']:5.0f} trkErr={s.get('trkErr', float('nan')):+5.0f} "
            f"ias={s['ias']:.0f}/{s.get('iasObj', float('nan')):.0f} "
            f"wv={s.get('wv', 0)} G={s['g']:.2f}"
        )

    lines.append("\n=== CYCLE3 closest pass RIGHT->LEFT 15:49:00-15:50:10 (1Hz) ===")
    for s in telem:
        t = s["hora"]
        if t < "15:49:00" or t > "15:50:10":
            continue
        sec = float(t.split(":")[2])
        if (sec % 1) > 0.25:
            continue
        lines.append(
            f"  {s['hora']} cr={s.get('cr', float('nan')):+7.0f} al={s.get('al', float('nan')):+7.0f} "
            f"rng={s.get('rng', float('nan')):6.0f} cl={s.get('cl', float('nan')):+5.0f} "
            f"bank={s['bank']:+5.0f}/{s['bank_obj']:+5.0f} "
            f"hdg={s['hdg']:5.0f}/{s['hdg_obj']:5.0f} trkErr={s.get('trkErr', float('nan')):+5.0f} "
            f"ias={s['ias']:.0f}/{s.get('iasObj', float('nan')):.0f} "
            f"wv={s.get('wv', 0)} G={s['g']:.2f} reg={s.get('reg')}"
        )

    # Find exact cr=0 crossings with interpolation note
    lines.append("\n=== ALL cr SIGN FLIPS (any magnitude) ===")
    prev_s = None
    for s in telem:
        if "cr" not in s:
            continue
        if prev_s and (prev_s["cr"] >= 0) != (s["cr"] >= 0):
            # interpolate fraction
            frac = abs(prev_s["cr"]) / (abs(prev_s["cr"]) + abs(s["cr"]) + 1e-9)
            lines.append(
                f"  {prev_s['hora']}->{s['hora']} cr {prev_s['cr']:+.0f}->{s['cr']:+.0f} "
                f"rng~{prev_s.get('rng'):.0f}->{s.get('rng'):.0f} "
                f"al~{prev_s.get('al'):.0f} bank={s['bank']:+.0f}/{s['bank_obj']:+.0f} "
                f"hdg={s['hdg']:.0f} cl={s.get('cl')}"
            )
        prev_s = s

    # Peak |cr| after each approach min
    lines.append("\n=== cr extrema (local max |cr| when |cr|>2000) ===")
    for i in range(5, len(telem) - 5):
        s = telem[i]
        if "cr" not in s:
            continue
        cr = s["cr"]
        if abs(cr) < 2500:
            continue
        window = [telem[j].get("cr", 0) for j in range(i - 5, i + 6) if "cr" in telem[j]]
        if abs(cr) == max(abs(x) for x in window):
            lines.append(
                f"  {s['hora']} cr={cr:+.0f} al={s.get('al'):+.0f} rng={s.get('rng'):.0f} "
                f"bank={s['bank']:+.0f}/{s['bank_obj']:+.0f} hdg={s['hdg']:.0f}"
            )

    # Weave and protection
    lines.append("\n=== Weave (wv) non-zero samples count ===")
    wv_nz = [s for s in telem if s.get("wv", 0) != 0]
    lines.append(f"  wv!=0 count={len(wv_nz)}")
    if wv_nz:
        lines.append(f"  first {wv_nz[0]['hora']} wv={wv_nz[0]['wv']} last {wv_nz[-1]['hora']} wv={wv_nz[-1]['wv']}")

    prots = {}
    for s in telem:
        p = s.get("prot") or ""
        if p:
            prots[p] = prots.get(p, 0) + 1
    lines.append("\n=== Proteccion frequencies ===")
    for p, c in sorted(prots.items(), key=lambda x: -x[1])[:20]:
        lines.append(f"  {c:5d} {p[:100]}")

    # Phase never Station?
    lines.append("\n=== Accion containing Station/Formacion ===")
    for s in telem:
        if "Formacion" in s["accion"] or "Station" in s["accion"] or "estacion" in s["accion"].lower():
            lines.append(f"  {s['hora']} {s['accion']}")
            break
    else:
        lines.append("  NEVER reached Station/Formacion in this recording")

    OUT.write_text("\n".join(lines), encoding="utf-8")
    print("wrote", OUT, "lines", len(lines))


if __name__ == "__main__":
    main()
