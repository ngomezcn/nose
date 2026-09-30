# -*- coding: utf-8 -*-
import csv
import re
from pathlib import Path

path = Path(r"E:\X-Plane 12\Resources\plugins\AICopilot\win_x64\DataLog.csv")
outp = Path(r"c:\Users\naimg\Desktop\AICopilot\devtools\_bb_user_intercept_extra.txt")

with open(path, encoding="utf-8", errors="replace") as f:
    rows = list(csv.DictReader(f, delimiter=";"))

pat = re.compile(r"(iasObj|tgtGS|cl|rng|al|cr|reg|side|lk|cor)=([^\s|;,]+)")


def parse(s):
    d = {}
    for m in pat.finditer(s or ""):
        try:
            d[m.group(1)] = float(m.group(2))
        except Exception:
            d[m.group(1)] = m.group(2)
    return d


series = []
for r in rows:
    if not (r.get("IAS_kt") or "").strip():
        continue
    a = parse(r.get("Adaptacion", ""))
    if "rng" not in a:
        continue
    series.append(
        (
            r["Hora"],
            float(r["IAS_kt"]),
            float(r.get("ThrottleObj_pct") or 0),
            a,
            (r.get("Accion") or "")[:45],
            float(r.get("BankReal_deg") or 0),
            float(r.get("RumboReal_deg") or 0),
            r.get("Adaptacion") or "",
        )
    )

out = []
prev = None
for h, ias, thr, a, acc, bank, hdg, adap in series:
    al = a.get("al")
    if al is None:
        continue
    sign = 1 if al >= 0 else -1
    if prev is None:
        prev = sign
        continue
    if sign != prev:
        out.append(
            f"AL_CROSS {h} al={al} rng={a.get('rng')} IAS={ias:.0f} "
            f"iasObj={a.get('iasObj')} cl={a.get('cl')} Acc={acc}"
        )
        prev = sign

out.append("CLOSEST:")
for h, ias, thr, a, acc, bank, hdg, adap in sorted(
    series, key=lambda x: x[3].get("rng", 1e9)
)[:8]:
    out.append(
        f"  {h} rng={a.get('rng')} al={a.get('al')} cr={a.get('cr')} "
        f"IAS={ias:.0f} iasObj={a.get('iasObj')} tgtGS={a.get('tgtGS')} "
        f"cl={a.get('cl')} Thr={thr:.0f} Acc={acc}"
    )

out.append("LATE_FROM_16:17:20:")
last = None
for h, ias, thr, a, acc, bank, hdg, adap in series:
    if h < "16:17:20":
        continue
    sec = int(h[6:8])
    if sec % 2:
        continue
    if h[:8] == last:
        continue
    last = h[:8]
    out.append(
        f"{h} rng={a.get('rng')} al={a.get('al')} cr={a.get('cr')} "
        f"IAS={ias:.0f} iasObj={a.get('iasObj')} tgtGS={a.get('tgtGS')} "
        f"cl={a.get('cl')} Thr={thr:.0f} bank={bank:.0f} hdg={hdg:.0f} Acc={acc}"
    )

out.append("THR_HIGH_NEAR_rng<2000_thr>=70:")
last = None
for h, ias, thr, a, acc, bank, hdg, adap in series:
    if a.get("rng", 9999) >= 2000 or thr < 70:
        continue
    if h[:8] == last:
        continue
    last = h[:8]
    out.append(
        f"{h} rng={a.get('rng')} IAS={ias:.0f} iasObj={a.get('iasObj')} "
        f"tgtGS={a.get('tgtGS')} cl={a.get('cl')} al={a.get('al')} "
        f"cr={a.get('cr')} Thr={thr:.0f} Acc={acc}"
    )

out.append("ADAP_FULL:")
for t in [
    "16:13:10",
    "16:15:36",
    "16:15:48",
    "16:17:30",
    "16:17:36",
    "16:17:45",
    "16:18:20",
]:
    for h, ias, thr, a, acc, bank, hdg, adap in series:
        if h.startswith(t):
            out.append(f"{h} Acc={acc} ADAP={adap}")
            break

# excess speed summary: max iasObj / IAS for rng buckets
out.append("BUCKETS rng:")
for lo, hi in [(0, 1000), (1000, 2000), (2000, 3500), (3500, 5000)]:
    sub = [s for s in series if lo <= s[3].get("rng", -1) < hi]
    if not sub:
        continue
    max_ias = max(sub, key=lambda x: x[1])
    max_obj = max(sub, key=lambda x: x[3].get("iasObj") or 0)
    avg_ias = sum(x[1] for x in sub) / len(sub)
    avg_obj = sum(x[3].get("iasObj") or 0 for x in sub) / len(sub)
    avg_tgt = sum(x[3].get("tgtGS") or 0 for x in sub) / len(sub)
    out.append(
        f"  rng[{lo},{hi}) n={len(sub)} avgIAS={avg_ias:.0f} avgIasObj={avg_obj:.0f} "
        f"avgTgtGS={avg_tgt:.0f} maxIAS@{max_ias[0]}={max_ias[1]:.0f} "
        f"maxObj@{max_obj[0]}={max_obj[3].get('iasObj')}"
    )

# phase time shares
from collections import Counter

c = Counter()
for h, ias, thr, a, acc, bank, hdg, adap in series:
    c[acc] += 1
out.append("PHASE_TIME_SAMPLES:")
for k, v in c.most_common():
    out.append(f"  {v} {k}")

outp.write_text("\n".join(out), encoding="utf-8")
print("wrote", outp, "lines", len(out))
