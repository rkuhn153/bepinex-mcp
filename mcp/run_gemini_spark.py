#!/usr/bin/env python3
"""
Stable Gemini Spark launcher: MCP (streamable-http) + cloudflared.

Keeps both processes alive, auto-restarts on crash, writes the public URL to
gemini-spark-url.txt. Uses normal child processes (no PowerShell pipes).

Usage:
  python run_gemini_spark.py
  double-click start-gemini-spark.bat
"""

from __future__ import annotations

import argparse
import os
import re
import signal
import socket
import subprocess
import sys
import time
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent
DEFAULT_PORT = 8765
URL_RE = re.compile(r"https://[a-zA-Z0-9-]+\.trycloudflare\.com")


def _python() -> str:
    preferred = Path(r"C:\Python313\python.exe")
    if preferred.is_file():
        return str(preferred)
    return sys.executable


def _cloudflared() -> Path:
    path = ROOT / "tools" / "cloudflared.exe"
    if path.is_file():
        return path
    path.parent.mkdir(parents=True, exist_ok=True)
    url = (
        "https://github.com/cloudflare/cloudflared/releases/latest/"
        "download/cloudflared-windows-amd64.exe"
    )
    print(f"Downloading cloudflared -> {path}")
    urllib.request.urlretrieve(url, path)
    return path


def _kill_port(port: int) -> None:
    if os.name != "nt":
        return
    try:
        creation = subprocess.CREATE_NO_WINDOW  # type: ignore[attr-defined]
    except AttributeError:
        creation = 0
    try:
        out = subprocess.check_output(
            ["netstat", "-ano"], text=True, errors="ignore", creationflags=creation
        )
    except Exception:
        return
    pids: set[int] = set()
    needle = f":{port}"
    for line in out.splitlines():
        if "LISTENING" not in line.upper() or needle not in line:
            continue
        parts = line.split()
        try:
            pids.add(int(parts[-1]))
        except (ValueError, IndexError):
            pass
    for pid in pids:
        if pid > 0:
            subprocess.run(
                ["taskkill", "/PID", str(pid), "/F", "/T"],
                capture_output=True,
                creationflags=creation,
            )


def _kill_cloudflared() -> None:
    if os.name != "nt":
        return
    try:
        creation = subprocess.CREATE_NO_WINDOW  # type: ignore[attr-defined]
    except AttributeError:
        creation = 0
    subprocess.run(
        ["taskkill", "/IM", "cloudflared.exe", "/F"],
        capture_output=True,
        creationflags=creation,
    )


def _port_open(port: int, host: str = "127.0.0.1") -> bool:
    try:
        with socket.create_connection((host, port), timeout=0.4):
            return True
    except OSError:
        return False


def _popen(args: list[str], log_path: Path) -> subprocess.Popen:
    log_path.parent.mkdir(parents=True, exist_ok=True)
    # Line-buffered-ish append log
    log_f = open(log_path, "a", encoding="utf-8", errors="replace", buffering=1)
    kwargs: dict = {
        "cwd": str(ROOT),
        "stdout": log_f,
        "stderr": subprocess.STDOUT,
        "stdin": subprocess.DEVNULL,
    }
    if os.name == "nt":
        # Keep children attached to this supervisor (so we can restart them),
        # but don't flash a console window.
        kwargs["creationflags"] = subprocess.CREATE_NO_WINDOW  # type: ignore[attr-defined]
    return subprocess.Popen(args, **kwargs)


def _parse_tunnel_url(log_path: Path) -> str | None:
    if not log_path.is_file():
        return None
    try:
        text = log_path.read_text(encoding="utf-8", errors="ignore")
    except OSError:
        return None
    matches = URL_RE.findall(text)
    return matches[-1] if matches else None


def _smoke_local(port: int) -> None:
    try:
        import httpx

        base = f"http://127.0.0.1:{port}"
        # Probes that previously failed for Gemini Spark
        for method, path in [
            ("HEAD", "/mcp"),
            ("GET", "/.well-known/oauth-protected-resource"),
            ("GET", "/.well-known/oauth-protected-resource/mcp"),
            ("GET", "/health"),
        ]:
            r = httpx.request(method, base + path, timeout=10.0)
            print(f"[smoke] {method:4} {path} -> {r.status_code}")

        r = httpx.post(
            base + "/mcp",
            headers={
                "Content-Type": "application/json",
                "Accept": "application/json, text/event-stream",
            },
            json={
                "jsonrpc": "2.0",
                "id": 1,
                "method": "initialize",
                "params": {
                    "protocolVersion": "2025-03-26",
                    "capabilities": {},
                    "clientInfo": {"name": "spark-launcher", "version": "1"},
                },
            },
            timeout=15.0,
        )
        print(f"[smoke] POST /mcp initialize -> {r.status_code}")
    except Exception as exc:
        print(f"[smoke] failed: {exc}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", type=int, default=DEFAULT_PORT)
    parser.add_argument("--game-ip", default="localhost")
    args = parser.parse_args()
    port = args.port

    mcp_log = ROOT / "gemini-mcp.err.log"
    cf_log = ROOT / "gemini-cf.err.log"
    url_file = ROOT / "gemini-spark-url.txt"
    pid_file = ROOT / "gemini-spark.pids"

    for p in (mcp_log, cf_log, url_file, pid_file):
        try:
            p.unlink()
        except FileNotFoundError:
            pass

    print("=== Unity Mod Helper -> Gemini Spark (stable) ===")
    print(f"Local MCP:  http://127.0.0.1:{port}/mcp")
    print(f"Game bridge http://{args.game_ip}:8080/mcp")
    print("Leave this window open.\n")

    _kill_cloudflared()
    _kill_port(port)
    time.sleep(0.5)

    py = _python()
    script = str(ROOT / "ModdersHelperApp.py")
    cf_bin = str(_cloudflared())

    mcp_cmd = [
        py,
        script,
        "--headless",
        "--transport",
        "streamable-http",
        "--host",
        "127.0.0.1",
        "--port",
        str(port),
        "--game-ip",
        args.game_ip,
    ]
    cf_cmd = [cf_bin, "tunnel", "--url", f"http://127.0.0.1:{port}"]

    mcp_proc: subprocess.Popen | None = None
    cf_proc: subprocess.Popen | None = None
    stop = False

    def _shutdown(*_a):
        nonlocal stop
        stop = True

    signal.signal(signal.SIGINT, _shutdown)
    if hasattr(signal, "SIGTERM"):
        signal.signal(signal.SIGTERM, _shutdown)

    def start_mcp() -> subprocess.Popen:
        print("[mcp] starting…")
        return _popen(mcp_cmd, mcp_log)

    def start_cf() -> subprocess.Popen:
        print("[cf]  starting cloudflared…")
        # Truncate so we only parse the new URL
        cf_log.write_text("", encoding="utf-8")
        return _popen(cf_cmd, cf_log)

    try:
        mcp_proc = start_mcp()
        for _ in range(100):
            if mcp_proc.poll() is not None:
                print("[mcp] exited early — gemini-mcp.err.log:")
                if mcp_log.is_file():
                    print(mcp_log.read_text(encoding="utf-8", errors="ignore")[-2500:])
                return 1
            if _port_open(port):
                break
            time.sleep(0.2)
        else:
            print("[mcp] port never opened")
            return 1
        print(f"[mcp] up on :{port} (pid {mcp_proc.pid})")
        _smoke_local(port)

        cf_proc = start_cf()
        public_url = None
        for _ in range(90):
            if cf_proc.poll() is not None:
                print("[cf] exited early — gemini-cf.err.log:")
                if cf_log.is_file():
                    print(cf_log.read_text(encoding="utf-8", errors="ignore")[-2500:])
                return 1
            public_url = _parse_tunnel_url(cf_log)
            if public_url:
                break
            time.sleep(1)
        if not public_url:
            print("[cf] no public URL parsed")
            return 1

        mcp_url = f"{public_url}/mcp"
        url_file.write_text(mcp_url + "\n", encoding="utf-8")
        pid_file.write_text(f"{mcp_proc.pid}\n{cf_proc.pid}\n", encoding="utf-8")

        # Public smoke (may take a second for DNS)
        time.sleep(2)
        try:
            import httpx

            r = httpx.get(
                f"{public_url}/.well-known/oauth-protected-resource", timeout=20.0
            )
            print(f"[smoke] public oauth-protected-resource -> {r.status_code}")
            r = httpx.request("HEAD", mcp_url, timeout=20.0)
            print(f"[smoke] public HEAD /mcp -> {r.status_code}")
            r = httpx.post(
                mcp_url,
                headers={
                    "Content-Type": "application/json",
                    "Accept": "application/json, text/event-stream",
                },
                json={
                    "jsonrpc": "2.0",
                    "id": 1,
                    "method": "initialize",
                    "params": {
                        "protocolVersion": "2025-03-26",
                        "capabilities": {},
                        "clientInfo": {"name": "spark-public", "version": "1"},
                    },
                },
                timeout=30.0,
            )
            print(f"[smoke] public POST initialize -> {r.status_code}")
        except Exception as exc:
            print(f"[smoke] public probe failed (retry in Spark anyway): {exc}")

        print()
        print("=" * 64)
        print(" PASTE THIS IN GEMINI SPARK:")
        print(f" {mcp_url}")
        print("=" * 64)
        print()
        print("gemini.google.com → Settings → Connected Apps")
        print("→ Custom apps for Spark → Add a custom app")
        print("→ paste URL → Next")
        print("If Advanced credentials appear, leave blank / no auth.")
        print()
        print("KEEP THIS WINDOW OPEN (auto-restarts if children die).")
        print("Ctrl+C to stop.\n")

        while not stop:
            if mcp_proc.poll() is not None:
                print(f"[mcp] died code={mcp_proc.returncode}; restarting…")
                time.sleep(1.5)
                if stop:
                    break
                _kill_port(port)
                mcp_proc = start_mcp()
                for _ in range(80):
                    if _port_open(port) or mcp_proc.poll() is not None:
                        break
                    time.sleep(0.2)
            if cf_proc.poll() is not None:
                print(f"[cf]  died code={cf_proc.returncode}; restarting…")
                time.sleep(1.5)
                if stop:
                    break
                cf_proc = start_cf()
                public_url = None
                for _ in range(90):
                    public_url = _parse_tunnel_url(cf_log)
                    if public_url or cf_proc.poll() is not None:
                        break
                    time.sleep(1)
                if public_url:
                    mcp_url = f"{public_url}/mcp"
                    url_file.write_text(mcp_url + "\n", encoding="utf-8")
                    print()
                    print(" NEW URL (re-add in Spark):")
                    print(f" {mcp_url}")
                    print()
            if mcp_proc and cf_proc:
                pid_file.write_text(
                    f"{mcp_proc.pid}\n{cf_proc.pid}\n", encoding="utf-8"
                )
            time.sleep(2)
    finally:
        print("Stopping children…")
        for proc in (cf_proc, mcp_proc):
            if proc and proc.poll() is None:
                try:
                    proc.terminate()
                except Exception:
                    pass
        time.sleep(0.6)
        for proc in (cf_proc, mcp_proc):
            if proc and proc.poll() is None:
                try:
                    proc.kill()
                except Exception:
                    pass
        _kill_cloudflared()
        _kill_port(port)
        print("Stopped.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
