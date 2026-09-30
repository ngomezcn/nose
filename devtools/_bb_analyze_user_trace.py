# -*- coding: utf-8 -*-
"""InterceptTrace: desGs vs range near target."""
import csv
import math
from pathlib import Path

p = Path(r"E:\X-Plane 12\Resources\plugins\AICopilot\win_x64\InterceptTrace.csv")
out = Path(r"c:\Users\naimg\Desktop\AICopilot\devtools\_bb_user_trace_speed.txt")

with open(p, encoding="utf-8", errors="replace") as f:
    rows = list(csv.DictReader(f, delimiter=";"))


def f(x):
    try:
        return float(x)
    except Exception:
        return float("nan")


mps2kt = 1.943844

lines = []
lines.append(f"trace rows={len(rows)}")

# regimes / phases
from collections import Counter

lines.append("regime: " + str(Counter(r["regime"] for r in rows)))
lines.append("phase: " + str(Counter(r["phase"] for r in rows)))
lines.append("stage: " + str(Counter(r["stage"] for r in rows)))
lines.append("sideLocked: " + str(Counter(r["sideLocked"] for r in rows)))
lines.append("side: " + str(Counter(r["side"] for r in rows)))

# near target: range < 3000
near = [r for r in rows if f(r["range"]) < 3000]
lines.append(f"near<3km n={len(near)}")

# sample every ~1s by t integer
last = None
lines.append("NEAR timeline (range<5km, ~1s):")
for r in rows:
    rng = f(r["range"])
    if rng >= 5000:
        continue
    t = int(f(r["t"]))
    if t == last:
        continue
    last = t
    tgt_gs = math.sqrt(f(r["tvx"]) ** 2 + f(r["tvz"]) ** 2) * mps2kt
    own_gs = math.sqrt(f(r["ovx"]) ** 2 + f(r["ovz"]) ** 2) * mps2kt
    des_gs_kt = f(r["desGs"]) * mps2kt
    des_ias_kt = f(r["desIas"]) * mps2kt
    lines.append(
        f"t={t}s rng={rng:.0f} rpR={f(r['rpRange']):.0f} al={f(r['along']):.0f} "
        f"cr={f(r['cross']):.0f} ownIAS={f(r['ownIas']):.0f} tgtGS={tgt_gs:.0f} "
        f"desGs={des_gs_kt:.0f} desIas={des_ias_kt:.0f} iasCmd={f(r['iasCmd']):.0f} "
        f"reg={r['regime']} ph={r['phase']} st={r['stage']} "
        f"lk={r['sideLocked']} side={r['side']} cor={r['corridor']} eta={f(r['eta']):.0f}"
    )

# buckets: avg desGs vs tgt when range bands
lines.append("BUCKETS desGs excess:")
for lo, hi in [(0, 1000), (1000, 2000), (2000, 3500), (3500, 5000), (5000, 8000)]:
    sub = [r for r in rows if lo <= f(r["range"]) < hi]
    if not sub:
        continue
    def tgt(r):
        return math.sqrt(f(r["tvx"]) ** 2 + f(r["tvz"]) ** 2)
    avg_des = sum(f(r["desGs"]) for r in sub) / len(sub)
    avg_tgt = sum(tgt(r) for r in sub) / len(sub)
    avg_cmd = sum(f(r["iasCmd"]) for r in sub) / len(sub)
    avg_rp = sum(f(r["rpRange"]) for r in sub) / len(sub)
    lines.append(
        f"  rng[{lo},{hi}) n={len(sub)} avgDesGs={avg_des*mps2kt:.0f}kt "
        f"avgTgtGS={avg_tgt*mps2kt:.0f}kt excess={((avg_des-avg_tgt)*mps2kt):.0f}kt "
        f"avgIasCmd={avg_cmd:.0f} avgRpRange={avg_rp:.0f}"
    )

# min range row
m = min(rows, key=lambda r: f(r["range"]))
tgt_gs = math.sqrt(f(m["tvx"]) ** 2 + f(m["tvz"]) ** 2) * mps2kt
lines.append(
    f"MIN rng t={m['t']} rng={m['range']} rpR={m['rpRange']} al={m['along']} cr={m['cross']} "
    f"desGs={f(m['desGs'])*mps2kt:.0f} iasCmd={m['iasCmd']} tgtGS={tgt_gs:.0f} "
    f"reg={m['regime']} ph={m['phase']}"
)

out.write_text("\n".join(lines), encoding="utf-8")
print("wrote", out, "lines", len(lines))
