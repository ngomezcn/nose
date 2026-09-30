# -*- coding: utf-8 -*-
"""One-shot blackbox analysis for last intercept flight. Do not modify flight code."""
import csv
import re
from pathlib import Path

path = Path(r"E:\X-Plane 12\Resources\plugins\AICopilot\win_x64\DataLog.csv")
trace_path = Path(r"E:\X-Plane 12\Resources\plugins\AICopilot\win_x64\InterceptTrace.csv")
out = Path(r"c:\Users\naimg\Desktop\AICopilot\devtools\_bb_user_intercept_report.txt")

with open(path, "r", encoding="utf-8", errors="replace") as f:
    rows = list(csv.DictReader(f, delimiter=";"))


def fnum(s):
    try:
        return float((s or "").strip() or "nan")
    except Exception:
        return float("nan")


marks = []
for i, r in enumerate(rows):
    n = (r.get("Nota") or "").strip()
    if n.startswith(("Inicio:", "Fin:", "Fase:", "AVISO:")):
        marks.append((i, r.get("Hora", ""), n))

start_i = None
end_i = len(rows) - 1
for i, h, n in marks:
    if n.startswith("Inicio: interceptar"):
        start_i = i
    if start_i is not None and i > start_i and n.startswith("Fin:"):
        # first Fin after intercept start that looks related, or any Fin later
        if "intercept" in n.lower() or "Intercept" in n or True:
            # keep last Fin in window if multiple; take first meaningful after start
            if end_i == len(rows) - 1 or i < end_i:
                end_i = i
                # don't break — allow later Fin if intercept continues; actually take last Fin
                pass

# Prefer last Fin after intercept start
fins = [i for i, h, n in marks if start_i is not None and i > start_i and n.startswith("Fin:")]
if fins:
    end_i = fins[-1]
if start_i is None:
    start_i = 0

samples = []
for i in range(start_i, min(end_i + 1, len(rows))):
    r = rows[i]
    if not (r.get("IAS_kt") or "").strip():
        continue
    samples.append((i, r))

pat = re.compile(
    r"(?:^|\s|,|\|)(iasObj|tgtGS|cl|rng|al|cr|reg|side|lk|cor)=([^\s|;,]+)"
)


def parse_adap(s):
    d = {}
    for m in pat.finditer(s or ""):
        k, v = m.group(1), m.group(2)
        try:
            d[k] = float(v)
        except Exception:
            d[k] = v
    return d


series = []
for i, r in samples:
    adap = parse_adap(r.get("Adaptacion", ""))
    series.append(
        {
            "Hora": r["Hora"],
            "Accion": (r.get("Accion") or "")[:50],
            "IAS": fnum(r["IAS_kt"]),
            "ThrObj": fnum(r.get("ThrottleObj_pct")),
            "ThrReal": fnum(r.get("ThrottleReal_pct")),
            "bank": fnum(r.get("BankReal_deg")),
            "bankObj": fnum(r.get("BankObj_deg")),
            "hdg": fnum(r.get("RumboReal_deg")),
            "hdgObj": fnum(r.get("RumboObj_deg")),
            "ALT": fnum(r.get("ALT_ft")),
            **adap,
            "Adaptacion": (r.get("Adaptacion") or "")[:240],
            "Proteccion": (r.get("Proteccion") or "")[:120],
        }
    )

phases = [(h, n) for _, h, n in marks if n.startswith("Fase:")]
acciones = sorted(
    {
        (r.get("Accion") or "").strip()
        for _, r in samples
        if (r.get("Accion") or "").strip() and r.get("Accion") != "-"
    }
)

lk_vals = [s.get("lk") for s in series if "lk" in s]
side_vals = [s.get("side") for s in series if "side" in s]
hot = [
    s
    for s in series
    if s.get("rng") is not None
    and s.get("IAS") == s.get("IAS")
    and s["rng"] < 5000
    and s["IAS"] > 350
]

# Formation / Station ever?
form_station = [
    (h, n)
    for _, h, n in marks
    if "Formation" in n or "Station" in n or "formacion" in n.lower()
]
acc_fs = [a for a in acciones if "Formation" in a or "Station" in a or "Formacion" in a]

lines = []
lines.append(f"=== ROWS={len(rows)} samples_in_window={len(samples)} series={len(series)}")
lines.append(f"WINDOW start_i={start_i} end_i={end_i}")
lines.append("")
lines.append("=== MARCAS ===")
for i, h, n in marks:
    lines.append(f"{h} | {n}")
lines.append("")
lines.append("=== ACCIONES UNICAS ===")
for a in acciones:
    lines.append(f"  {a}")
lines.append("")
lines.append("=== FASES ===")
for h, n in phases:
    lines.append(f"{h} | {n}")
lines.append("")
lines.append("=== Formation/Station? ===")
lines.append(f"marks: {form_station}")
lines.append(f"acciones: {acc_fs}")

lines.append("")
lines.append("=== SIDE LOCK ===")
if lk_vals:
    lines.append(
        f"lk count={len(lk_vals)} min={min(lk_vals)} max={max(lk_vals)} unique={sorted(set(lk_vals))}"
    )
    lk1 = sum(1 for v in lk_vals if v == 1)
    lines.append(f"lk==1: {lk1}/{len(lk_vals)} ({100 * lk1 / len(lk_vals):.1f}%)")
else:
    lines.append("NO lk field")
if side_vals:
    lines.append(f"side unique={sorted(set(side_vals))} count={len(side_vals)}")
    prev = None
    trans = []
    for s in series:
        if "side" not in s:
            continue
        if prev is None:
            prev = s["side"]
            continue
        if s["side"] != prev:
            trans.append(
                (
                    s["Hora"],
                    prev,
                    s["side"],
                    s.get("rng"),
                    s.get("IAS"),
                    s.get("lk"),
                    s["Accion"],
                )
            )
            prev = s["side"]
    lines.append(f"side transitions: {len(trans)}")
    for t in trans:
        lines.append(
            f"  {t[0]} side {t[1]}->{t[2]} rng={t[3]} IAS={t[4]} lk={t[5]} Acc={t[6]}"
        )
else:
    lines.append("NO side field")

lines.append("")
lines.append("=== HOT: rng<5000 & IAS>350 ===")
lines.append(f"count={len(hot)}")
last_t = None
for s in hot:
    hh = s["Hora"][:8]
    if hh != last_t:
        last_t = hh
        lines.append(
            f"{s['Hora']} Acc={s['Accion']} IAS={s['IAS']:.0f} iasObj={s.get('iasObj')} "
            f"tgtGS={s.get('tgtGS')} cl={s.get('cl')} rng={s.get('rng')} al={s.get('al')} "
            f"cr={s.get('cr')} reg={s.get('reg')} side={s.get('side')} lk={s.get('lk')} "
            f"cor={s.get('cor')} bank={s['bank']:.1f} hdg={s['hdg']:.0f} "
            f"ThrObj={s['ThrObj']:.0f}"
        )

lines.append("")
lines.append("=== TIMELINE SERIES (~1s, with rng or intercept Accion) ===")
last_t = None
for s in series:
    hh = s["Hora"][:8]
    if hh == last_t:
        continue
    last_t = hh
    has_rng = "rng" in s
    acc = s["Accion"] or ""
    if not has_rng and acc in ("", "-"):
        continue
    lines.append(
        f"{s['Hora']} Acc={s['Accion']} IAS={s['IAS']:.0f} iasObj={s.get('iasObj')} "
        f"tgtGS={s.get('tgtGS')} cl={s.get('cl')} rng={s.get('rng')} al={s.get('al')} "
        f"cr={s.get('cr')} reg={s.get('reg')} side={s.get('side')} lk={s.get('lk')} "
        f"cor={s.get('cor')} bank={s['bank']:.1f} hdg={s['hdg']:.0f} "
        f"Thr={s['ThrObj']:.0f}/{s['ThrReal']:.0f}"
    )

near = [s for s in series if s.get("rng") is not None and s["rng"] < 5000]
if near:
    maxias = max(near, key=lambda x: x["IAS"])
    minrng = min(near, key=lambda x: x["rng"])
    lines.append("")
    lines.append(
        f"MAX IAS within 5km: {maxias['Hora']} IAS={maxias['IAS']} iasObj={maxias.get('iasObj')} "
        f"rng={maxias.get('rng')} ThrObj={maxias['ThrObj']} Acc={maxias['Accion']}"
    )
    lines.append(
        f"MIN rng: {minrng['Hora']} rng={minrng['rng']} IAS={minrng['IAS']} "
        f"iasObj={minrng.get('iasObj')} Acc={minrng['Accion']} ThrObj={minrng['ThrObj']}"
    )

# iasObj vs IAS when rng small
lines.append("")
lines.append("=== iasObj when rng<3000 (1s) ===")
last_t = None
for s in series:
    if s.get("rng") is None or s["rng"] >= 3000:
        continue
    hh = s["Hora"][:8]
    if hh == last_t:
        continue
    last_t = hh
    lines.append(
        f"{s['Hora']} rng={s.get('rng')} IAS={s['IAS']:.0f} iasObj={s.get('iasObj')} "
        f"tgtGS={s.get('tgtGS')} cl={s.get('cl')} ThrObj={s['ThrObj']:.0f} "
        f"reg={s.get('reg')} side={s.get('side')} lk={s.get('lk')} Acc={s['Accion']}"
    )

lines.append("")
lines.append("=== SAMPLE Adaptacion ===")
for s in series:
    if "rng" in s:
        lines.append(f"early: {s['Adaptacion']}")
        break
mid = len(series) // 2
for s in series[mid:]:
    if s.get("Adaptacion") and "rng" in s:
        lines.append(f"mid: {s['Adaptacion']}")
        break
if hot:
    lines.append(f"hot_last: {hot[-1].get('Adaptacion', '')}")
# find when rng first < 2000
for s in series:
    if s.get("rng") is not None and s["rng"] < 2000:
        lines.append(f"first_rng<2km: {s['Hora']} {s['Adaptacion']}")
        break

# InterceptTrace summary if present
if trace_path.exists():
    lines.append("")
    lines.append("=== InterceptTrace.csv ===")
    with open(trace_path, "r", encoding="utf-8", errors="replace") as f:
        tr = list(csv.DictReader(f, delimiter=";"))
    lines.append(f"trace rows={len(tr)} cols={list(tr[0].keys()) if tr else []}")
    # last N unique phase transitions / sample
    if tr:
        # print header keys that look useful
        keys = list(tr[0].keys())
        interesting = [
            k
            for k in keys
            if any(
                x in k.lower()
                for x in (
                    "fase",
                    "phase",
                    "rng",
                    "range",
                    "ias",
                    "side",
                    "lock",
                    "lk",
                    "thr",
                    "obj",
                    "cl",
                    "hora",
                    "t",
                    "reg",
                    "bank",
                    "hdg",
                )
            )
        ]
        lines.append(f"interesting cols: {interesting[:40]}")
        # dump first/last few rows abbreviated
        for label, idx in [("first", 0), ("mid", len(tr) // 2), ("last", -1)]:
            row = tr[idx]
            parts = [f"{k}={row.get(k)}" for k in keys[:25]]
            lines.append(f"trace_{label}: " + " | ".join(parts)[:500])

out.write_text("\n".join(lines), encoding="utf-8")
print(f"Wrote {out} ({len(lines)} lines)")
