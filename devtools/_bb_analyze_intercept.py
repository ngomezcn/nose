# -*- coding: utf-8 -*-
"""One-shot black-box intercept analysis. Do not import from product code."""
import csv
import math
import re
from pathlib import Path

CSV_PATH = Path(r"E:\X-Plane 12\Resources\plugins\AICopilot\win_x64\DataLog.csv")
OUT = Path(r"c:\Users\naimg\Desktop\AICopilot\devtools\_bb_intercept_report.txt")


def fnum(s, default=float("nan")):
    if s is None or s == "" or s == "-":
        return default
    try:
        return float(s)
    except ValueError:
        return default


def ang_diff(a, b):
    """Smallest signed difference a-b in degrees (-180..180)."""
    d = (a - b + 180.0) % 360.0 - 180.0
    return d


def unwrap_heading(series):
    """Return unwrapped continuous heading path from 0-360 samples."""
    if not series:
        return []
    out = [series[0]]
    for h in series[1:]:
        prev = out[-1]
        d = ang_diff(h, prev % 360.0)
        # ang_diff uses wrapped prev; rebuild absolute
        out.append(prev + d)
    return out


def main():
    rows = []
    with CSV_PATH.open(encoding="utf-8", errors="replace") as fh:
        reader = csv.DictReader(fh, delimiter=";")
        for row in reader:
            rows.append(row)

    lines = []
    lines.append(f"FILE={CSV_PATH}")
    lines.append(f"ROWS={len(rows)}")

    # --- all markers ---
    markers = []
    for i, row in enumerate(rows):
        nota = (row.get("Nota") or "").strip()
        if not nota:
            continue
        if nota.startswith(("Inicio:", "Fin:", "Fase:", "AVISO:")):
            markers.append((i, row.get("Hora", ""), nota))
            lines.append(f"MARKER[{i}] {row.get('Hora','')} | {nota}")

    # find intercept window
    start_i = None
    end_i = None
    for i, h, nota in markers:
        if nota.startswith("Inicio: interceptar"):
            start_i = i
        if start_i is not None and nota.startswith("Fin:") and "intercept" in nota.lower():
            end_i = i
            break
    if start_i is None:
        for i, h, nota in markers:
            if "intercept" in nota.lower() and nota.startswith("Inicio:"):
                start_i = i
                break
    if end_i is None and start_i is not None:
        # next Fin: or end of file
        for i, h, nota in markers:
            if i > start_i and nota.startswith("Fin:"):
                end_i = i
                break
    if end_i is None:
        end_i = len(rows) - 1

    lines.append(f"\nWINDOW start_i={start_i} end_i={end_i}")
    if start_i is None:
        OUT.write_text("\n".join(lines), encoding="utf-8")
        print("No intercept start found")
        return

    win = rows[start_i : end_i + 1]
    lines.append(f"WINDOW rows={len(win)} from {win[0].get('Hora')} to {win[-1].get('Hora')}")
    lines.append(f"START NOTA: {win[0].get('Nota')}")

    # Parse cfg from start note
    start_nota = win[0].get("Nota") or ""
    cfg = {}
    for key in ("dist", "lat", "vert"):
        m = re.search(rf"{key}=(-?\d+(?:\.\d+)?)m", start_nota)
        if m:
            cfg[key] = float(m.group(1))
    lines.append(f"CFG parsed: {cfg}")

    # Collect samples with telemetry
    samples = []
    for idx, row in enumerate(win):
        global_i = start_i + idx
        ias = fnum(row.get("IAS_kt"))
        if math.isnan(ias):
            # marker-only row still keep for phase notes
            nota = (row.get("Nota") or "").strip()
            if nota:
                samples.append(
                    {
                        "gi": global_i,
                        "hora": row.get("Hora", ""),
                        "fase": row.get("Fase", ""),
                        "accion": row.get("Accion", ""),
                        "nota": nota,
                        "marker": True,
                    }
                )
            continue
        s = {
            "gi": global_i,
            "hora": row.get("Hora", ""),
            "fase": row.get("Fase", ""),
            "accion": row.get("Accion", ""),
            "nota": (row.get("Nota") or "").strip(),
            "marker": False,
            "ias": ias,
            "alt": fnum(row.get("ALT_ft")),
            "agl": fnum(row.get("AGL_ft")),
            "vs": fnum(row.get("VS_fpm")),
            "pitch_obj": fnum(row.get("PitchObj_deg")),
            "pitch": fnum(row.get("PitchReal_deg")),
            "bank_obj": fnum(row.get("BankObj_deg")),
            "bank": fnum(row.get("BankReal_deg")),
            "hdg_obj": fnum(row.get("RumboObj_deg")),
            "hdg": fnum(row.get("RumboReal_deg")),
            "g": fnum(row.get("G")),
            "g_obj": fnum(row.get("G_obj")),
            "g_pred": fnum(row.get("G_pred")),
            "thr_obj": fnum(row.get("ThrottleObj_pct")),
            "thr": fnum(row.get("ThrottleReal_pct")),
            "mp": fnum(row.get("MandoPitch")),
            "mr": fnum(row.get("MandoRoll")),
            "my": fnum(row.get("MandoYaw")),
            "pr": fnum(row.get("PitchRate_dps")),
            "rr": fnum(row.get("RollRate_dps")),
            "adapt": (row.get("Adaptacion") or "").strip(),
            "prot": (row.get("Proteccion") or "").strip(),
            "aoa": fnum(row.get("AoA_deg")),
        }
        # Try to parse range/cierre/lat from Nota or Accion if present in sample notes
        samples.append(s)

    # Phase markers inside window
    lines.append("\n=== PHASE TIMELINE ===")
    for s in samples:
        if s.get("marker") or (s.get("nota") and s["nota"].startswith(("Fase:", "Inicio:", "Fin:", "AVISO:"))):
            lines.append(f"  {s['hora']} | {s.get('nota','')[:220]}")

    # Unique Accion / Fase values over time
    lines.append("\n=== ACCION/FASE transitions (telemetry rows) ===")
    prev_key = None
    for s in samples:
        if s.get("marker"):
            continue
        key = (s.get("fase"), s.get("accion"))
        if key != prev_key:
            lines.append(f"  {s['hora']} Fase={s.get('fase')} Accion={s.get('accion')}")
            prev_key = key

    telem = [s for s in samples if not s.get("marker")]
    if not telem:
        OUT.write_text("\n".join(lines), encoding="utf-8")
        print("No telemetry")
        return

    # Heading unwrap for loop detection
    hdg = [s["hdg"] for s in telem]
    hdg_u = unwrap_heading(hdg)
    for s, hu in zip(telem, hdg_u):
        s["hdg_u"] = hu

    # Detect 360 loops: periods where |Δheading| accumulates ~360°
    lines.append("\n=== HEADING LOOP DETECTION (unwrap) ===")
    lines.append(f"  hdg start={telem[0]['hdg']:.1f} end={telem[-1]['hdg']:.1f}")
    lines.append(f"  unwrap start={hdg_u[0]:.1f} end={hdg_u[-1]:.1f} netΔ={hdg_u[-1]-hdg_u[0]:.1f}°")

    # Sliding window: find stretches of ~360° turn
    loops = []
    i = 0
    n = len(telem)
    while i < n - 10:
        # look ahead for +300 or -300 accumulation
        j = i + 1
        found = None
        while j < n:
            d = hdg_u[j] - hdg_u[i]
            if abs(d) >= 300:
                found = (i, j, d)
                break
            # stop if heading nearly settles
            if j - i > 5 and abs(d) < 20 and abs(telem[j]["rr"]) < 5:
                # not a loop from here
                break
            j += 1
        if found:
            i0, j0, d = found
            # refine end when |d| first exceeds 330
            for k in range(i0 + 1, j0 + 1):
                if abs(hdg_u[k] - hdg_u[i0]) >= 330:
                    j0 = k
                    d = hdg_u[k] - hdg_u[i0]
                    break
            loops.append((i0, j0, d))
            lines.append(
                f"  LOOP {len(loops)}: {telem[i0]['hora']} -> {telem[j0]['hora']} "
                f"Δhdg={d:+.0f}° bank@start={telem[i0]['bank']:.0f} bank_obj={telem[i0]['bank_obj']:.0f} "
                f"bank@end={telem[j0]['bank']:.0f} hdg={telem[i0]['hdg']:.0f}->{telem[j0]['hdg']:.0f} "
                f"Accion={telem[i0].get('accion')}"
            )
            i = j0 + 1
        else:
            i += 1

    # Bank sign analysis: positive bank = right wing down typically in aviation?
    # In X-Plane, positive roll is typically right wing down.
    lines.append("\n=== BANK SIGN STATS (telemetry) ===")
    banks = [s["bank"] for s in telem]
    bank_objs = [s["bank_obj"] for s in telem]
    lines.append(
        f"  bank real: min={min(banks):.1f} max={max(banks):.1f} mean={sum(banks)/len(banks):.1f}"
    )
    lines.append(
        f"  bank obj:  min={min(bank_objs):.1f} max={max(bank_objs):.1f} mean={sum(bank_objs)/len(bank_objs):.1f}"
    )
    right_frac = sum(1 for b in banks if b > 5) / len(banks)
    left_frac = sum(1 for b in banks if b < -5) / len(banks)
    lines.append(f"  fraction bank>5° (right?): {right_frac:.2%}  bank<-5° (left?): {left_frac:.2%}")

    # Parse rango/cierre/offset from Nota fields if embedded in markers; also scan Accion
    # Many intercept logs put rango in Fase notes. Also check Adaptacion.
    lines.append("\n=== NOTES mentioning rango/cierre/lat/offset ===")
    for s in samples:
        nota = s.get("nota") or ""
        if any(k in nota.lower() for k in ("rango", "cierre", "lat", "offset", "derecha", "izquierda", "nm")):
            lines.append(f"  {s['hora']} | {nota[:280]}")

    # Sample dense key series at key events: start, each phase, min range if available
    # Extract numbers from phase notes
    range_events = []
    for s in samples:
        nota = s.get("nota") or ""
        m = re.search(r"rango\s*=\s*(-?\d+(?:\.\d+)?)\s*m", nota, re.I)
        cierre = re.search(r"cierre\s*=\s*(-?\d+(?:\.\d+)?)", nota, re.I)
        latm = re.search(r"lat(?:eral)?\s*=\s*(-?\d+(?:\.\d+)?)\s*m", nota, re.I)
        if m or "Fase:" in nota:
            range_events.append(
                {
                    "hora": s["hora"],
                    "rango": float(m.group(1)) if m else None,
                    "cierre": float(cierre.group(1)) if cierre else None,
                    "lat": float(latm.group(1)) if latm else None,
                    "nota": nota[:200],
                }
            )

    lines.append("\n=== PARSED RANGE EVENTS ===")
    for e in range_events:
        lines.append(f"  {e}")

    # Downsample every ~1s for overview (assuming ~10Hz -> every 10th)
    lines.append("\n=== SERIES (~1 Hz): hora hdg/hdgObj bank/bankObj G Gobj IAS Accion adapt|prot ===")
    step = max(1, len(telem) // 120)  # ~120 lines max
    # better: time-based if we can parse; use index step ~10
    step = 10
    for s in telem[::step]:
        adapt = s["adapt"][:40] if s["adapt"] else ""
        prot = s["prot"][:40] if s["prot"] else ""
        lines.append(
            f"  {s['hora']} hdg={s['hdg']:6.1f}/{s['hdg_obj']:6.1f} "
            f"bank={s['bank']:+6.1f}/{s['bank_obj']:+6.1f} "
            f"G={s['g']:.2f}/{s['g_obj']:.2f} IAS={s['ias']:.0f} "
            f"mr={s['mr']:+.2f} rr={s['rr']:+.0f} "
            f"Acc={s.get('accion','')[:30]} {adapt}|{prot}"
        )

    # Find moments of large bank_obj sign flips and heading error spikes
    lines.append("\n=== HEADING ERROR SPIKES (|hdg-hdgObj|>60) transitions ===")
    prev_big = False
    for s in telem:
        err = abs(ang_diff(s["hdg_obj"], s["hdg"]))
        big = err > 60
        if big and not prev_big:
            lines.append(
                f"  ENTER big err {s['hora']} err={ang_diff(s['hdg_obj'], s['hdg']):+.0f} "
                f"hdg={s['hdg']:.0f} obj={s['hdg_obj']:.0f} bank={s['bank']:+.0f}/{s['bank_obj']:+.0f} "
                f"Acc={s.get('accion')}"
            )
        if (not big) and prev_big:
            lines.append(
                f"  EXIT  big err {s['hora']} err={ang_diff(s['hdg_obj'], s['hdg']):+.0f} "
                f"hdg={s['hdg']:.0f} obj={s['hdg_obj']:.0f} bank={s['bank']:+.0f}/{s['bank_obj']:+.0f}"
            )
        prev_big = big

    # Extreme bank commands
    lines.append("\n=== EXTREME |bank_obj|>45 peaks ===")
    for s in telem:
        if abs(s["bank_obj"]) > 45:
            # only record local peaks roughly
            pass
    # find local max |bank_obj|
    for i in range(1, len(telem) - 1):
        a0 = abs(telem[i - 1]["bank_obj"])
        a1 = abs(telem[i]["bank_obj"])
        a2 = abs(telem[i + 1]["bank_obj"])
        if a1 >= 50 and a1 >= a0 and a1 >= a2:
            s = telem[i]
            lines.append(
                f"  {s['hora']} bank_obj={s['bank_obj']:+.0f} bank={s['bank']:+.0f} "
                f"hdg={s['hdg']:.0f}/{s['hdg_obj']:.0f} G={s['g']:.2f} mr={s['mr']:+.2f} Acc={s.get('accion')}"
            )

    # Adaptacion / Proteccion unique values
    lines.append("\n=== UNIQUE Adaptacion ===")
    adapts = sorted({s["adapt"] for s in telem if s["adapt"]})
    for a in adapts[:50]:
        lines.append(f"  {a}")
    lines.append("\n=== UNIQUE Proteccion ===")
    prots = sorted({s["prot"] for s in telem if s["prot"]})
    for a in prots[:50]:
        lines.append(f"  {a}")

    # G extremes
    gmax = max(telem, key=lambda s: s["g"])
    gmin = min(telem, key=lambda s: s["g"])
    lines.append(f"\n=== G extremes: max={gmax['g']:.2f} @{gmax['hora']} min={gmin['g']:.2f} @{gmin['hora']}")

    # Early window detail (first 30s of intercept) at 2Hz
    lines.append("\n=== FIRST ~40s DETAIL (every 5 samples) ===")
    # parse times roughly by counting samples *0.1
    for s in telem[:400:5]:
        lines.append(
            f"  {s['hora']} hdg={s['hdg']:6.1f}/{s['hdg_obj']:6.1f} "
            f"bank={s['bank']:+6.1f}/{s['bank_obj']:+6.1f} "
            f"G={s['g']:.2f} IAS={s['ias']:.0f} pitch={s['pitch']:+.1f}/{s['pitch_obj']:+.1f} "
            f"mr={s['mr']:+.2f} Acc={s.get('accion','')[:40]}"
        )

    # Around each loop: detail
    lines.append("\n=== LOOP CONTEXT (±2s) ===")
    for li, (i0, j0, d) in enumerate(loops, 1):
        lines.append(f"--- Loop {li} Δ={d:+.0f} ---")
        for s in telem[max(0, i0 - 20) : min(n, j0 + 20) : 5]:
            lines.append(
                f"  {s['hora']} hdg={s['hdg']:6.1f}/{s['hdg_obj']:6.1f} "
                f"bank={s['bank']:+6.1f}/{s['bank_obj']:+6.1f} "
                f"G={s['g']:.2f} mr={s['mr']:+.2f} Acc={s.get('accion','')[:40]} "
                f"ad={s['adapt'][:30]}"
            )

    OUT.write_text("\n".join(lines), encoding="utf-8")
    print(f"Wrote {OUT} lines={len(lines)} telem={len(telem)} loops={len(loops)}")


if __name__ == "__main__":
    main()
