#!/usr/bin/env python3
"""CLI minimo del driver AICopilot (HTTP REST localhost :17890)."""
from __future__ import annotations

import argparse
import http.client
import json
import sys

HOST = "127.0.0.1"
PORT = 17890
TIMEOUT = 5


def request(method: str, path: str, body: dict | None = None) -> tuple[int, str]:
    payload = None
    headers = {"Accept": "application/json", "Connection": "close"}
    if body is not None:
        payload = json.dumps(body)
        headers["Content-Type"] = "application/json"
    try:
        conn = http.client.HTTPConnection(HOST, PORT, timeout=TIMEOUT)
        conn.request(method, path, body=payload, headers=headers)
        resp = conn.getresponse()
        text = resp.read().decode("utf-8", errors="replace")
        conn.close()
        return resp.status, text
    except Exception as e:
        print(f"error: {e}", file=sys.stderr)
        sys.exit(1)


def main() -> None:
    p = argparse.ArgumentParser(description="AICopilot driver CLI")
    sub = p.add_subparsers(dest="cmd", required=True)
    sub.add_parser("health")
    sub.add_parser("status")
    sub.add_parser("intercept-abort")
    sub.add_parser("abort")
    ic = sub.add_parser("intercept")
    ic.add_argument("--index", type=int, required=True)
    ic.add_argument("--station", default="TailHigh")
    ic.add_argument("--label", default="")
    args = p.parse_args()

    if args.cmd == "health":
        code, text = request("GET", "/health")
    elif args.cmd == "status":
        code, text = request("GET", "/status")
    elif args.cmd == "intercept":
        body: dict = {"index": args.index, "station": args.station}
        if args.label:
            body["label"] = args.label
        code, text = request("POST", "/intercept", body)
    elif args.cmd == "intercept-abort":
        code, text = request("POST", "/intercept/abort", {})
    elif args.cmd == "abort":
        code, text = request("POST", "/abort", {})
    else:
        p.error(f"comando desconocido: {args.cmd}")
        return

    print(text)
    if code >= 400:
        sys.exit(1)


if __name__ == "__main__":
    main()