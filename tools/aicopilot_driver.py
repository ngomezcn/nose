#!/usr/bin/env python3
"""CLI de la API REST de AICopilotCore (http://127.0.0.1:17890).

    python tools/aicopilot_driver.py status
    python tools/aicopilot_driver.py takeoff [Relaxed|Combat|Emergency]
    python tools/aicopilot_driver.py intercept [INDEX] [TailHigh|ParallelRight|ParallelLeft|Above|Below]
    python tools/aicopilot_driver.py abort
"""
import sys
import urllib.error
import urllib.parse
import urllib.request

URL = "http://127.0.0.1:17890"
# Sin proxies: en Windows urllib coge el del sistema y podria desviar 127.0.0.1.
OPENER = urllib.request.build_opener(urllib.request.ProxyHandler({}))


def call(method, path, **params):
    query = urllib.parse.urlencode({k: v for k, v in params.items() if v is not None})
    req = urllib.request.Request(f"{URL}{path}?{query}", method=method)
    try:
        with OPENER.open(req, timeout=10) as r:
            print(r.read().decode())
    except urllib.error.HTTPError as e:
        print(e.read().decode() or f"HTTP {e.code}")
        sys.exit(1)
    except urllib.error.URLError as e:
        print(f"no responde {URL} ({e.reason}): esta abierto AICopilotCore?")
        sys.exit(1)


def main():
    args = sys.argv[1:] + [None, None, None]
    cmd, a, b = args[0], args[1], args[2]
    if cmd == "status":
        call("GET", "/status")
    elif cmd == "takeoff":
        call("POST", "/takeoff", style=a)
    elif cmd == "intercept":
        call("POST", "/intercept", index=a, station=b)
    elif cmd == "abort":
        call("POST", "/abort")
    else:
        print(__doc__)
        sys.exit(2)


if __name__ == "__main__":
    main()
