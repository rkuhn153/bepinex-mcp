#!/usr/bin/env python3
"""CLI full BepInEx + bridge setup for Unity Modding Studio / MCP.

Usage:
  python game_setup.py "D:\\SteamLibrary\\steamapps\\common\\SomeGame"
"""

from __future__ import annotations

import argparse
import os
import re
import shutil
import subprocess
import sys
import time
import zipfile
from typing import Optional

try:
    import httpx
except ImportError:
    print("httpx is required: pip install httpx", file=sys.stderr)
    sys.exit(2)


def find_steam_appid(g_dir: str) -> Optional[str]:
    current = g_dir
    for _ in range(6):
        parent, name = os.path.split(current)
        if name.lower() == "steamapps":
            for f in os.listdir(current):
                if f.startswith("appmanifest_") and f.endswith(".acf"):
                    acf_path = os.path.join(current, f)
                    try:
                        with open(acf_path, "r", encoding="utf-8", errors="ignore") as file:
                            content = file.read()
                        install_dir_match = re.search(
                            r'"installdir"\s+"([^"]+)"', content, re.IGNORECASE
                        )
                        if install_dir_match:
                            install_dir = install_dir_match.group(1).lower()
                            if install_dir in g_dir.lower():
                                appid_match = re.search(
                                    r'"appid"\s+"(\d+)"', content, re.IGNORECASE
                                )
                                if appid_match:
                                    return appid_match.group(1)
                    except OSError:
                        pass
        if not parent or parent == current:
            break
        current = parent
    return None


def launch_and_wait(
    game_exe: str,
    game_dir: str,
    steam_appid: Optional[str],
    is_il2cpp: bool,
) -> bool:
    env = os.environ.copy()
    if steam_appid:
        env["SteamAppId"] = steam_appid
        env["SteamGameId"] = steam_appid

    proc = subprocess.Popen([game_exe], cwd=game_dir, env=env)
    wait_limit = 90 if is_il2cpp else 25
    success = False
    try:
        for idx in range(wait_limit):
            time.sleep(1)
            if is_il2cpp:
                hash_file = os.path.join(
                    game_dir, "BepInEx", "interop", "assembly-hash.txt"
                )
                if os.path.isfile(hash_file):
                    success = True
                    break
            else:
                plugins_dir = os.path.join(game_dir, "BepInEx", "plugins")
                if os.path.isdir(plugins_dir):
                    success = True
                    break
            if idx > 8 and proc.poll() is not None and not success:
                break
    finally:
        try:
            proc.terminate()
            proc.wait(timeout=3)
        except Exception:
            try:
                proc.kill()
            except Exception:
                pass
    return success


def setup_game(game_directory: str) -> str:
    if not game_directory.strip():
        return "Error: game_directory path is required."

    game_dir = os.path.abspath(game_directory)
    if not os.path.isdir(game_dir):
        return f"Error: Game directory does not exist: {game_dir}"

    executables = [
        f
        for f in os.listdir(game_dir)
        if f.lower().endswith(".exe") and "unitycrashhandler" not in f.lower()
    ]
    if not executables:
        return f"Error: no game .exe in {game_dir}"

    game_exe = os.path.join(game_dir, executables[0])
    is_il2cpp = os.path.exists(os.path.join(game_dir, "GameAssembly.dll"))
    data_folders = [
        f
        for f in os.listdir(game_dir)
        if os.path.isdir(os.path.join(game_dir, f)) and f.endswith("_Data")
    ]
    if not data_folders:
        return f"Error: not a Unity game root (missing *_Data): {game_dir}"

    runtime_type = "IL2CPP" if is_il2cpp else "Mono"
    status = [f"Detected Unity {runtime_type}: {executables[0]}"]

    if is_il2cpp:
        bepinex_url = (
            "https://builds.bepinex.dev/projects/bepinex_be/785/"
            "BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.785+6abdba4.zip"
        )
    else:
        bepinex_url = (
            "https://github.com/BepInEx/BepInEx/releases/download/"
            "v5.4.22/BepInEx_x64_5.4.22.0.zip"
        )

    zip_path = os.path.join(game_dir, "bepinex_temp.zip")
    status.append(f"Downloading BepInEx for {runtime_type}...")
    try:
        with httpx.Client(timeout=120.0, follow_redirects=True) as client:
            with client.stream("GET", bepinex_url) as r:
                r.raise_for_status()
                with open(zip_path, "wb") as f:
                    for chunk in r.iter_bytes():
                        f.write(chunk)
        status.append("Download complete.")
    except Exception as e:
        return f"Failed to download BepInEx: {e}"

    status.append("Extracting BepInEx...")
    try:
        with zipfile.ZipFile(zip_path, "r") as zip_ref:
            zip_ref.extractall(game_dir)
        os.remove(zip_path)
        status.append("Extraction complete.")
    except Exception as e:
        if os.path.exists(zip_path):
            os.remove(zip_path)
        return f"Failed to extract BepInEx: {e}"

    status.append("Bootstrapping (launch game once)...")
    steam_appid = find_steam_appid(game_dir)
    if steam_appid:
        status.append(f"Steam AppID {steam_appid} → steam_appid.txt")
        try:
            with open(os.path.join(game_dir, "steam_appid.txt"), "w", encoding="utf-8") as f:
                f.write(steam_appid)
        except OSError as e:
            status.append(f"Warning: could not write steam_appid.txt: {e}")

    try:
        bootstrap_success = launch_and_wait(game_exe, game_dir, steam_appid, is_il2cpp)
        if not bootstrap_success:
            winhttp_path = os.path.join(game_dir, "winhttp.dll")
            version_path = os.path.join(game_dir, "version.dll")
            if os.path.isfile(winhttp_path):
                status.append("winhttp timed out — trying version.dll proxy...")
                try:
                    if os.path.isfile(version_path):
                        os.remove(version_path)
                    os.rename(winhttp_path, version_path)
                    bootstrap_success = launch_and_wait(
                        game_exe, game_dir, steam_appid, is_il2cpp
                    )
                except OSError as e:
                    status.append(f"version.dll fallback failed: {e}")
        if bootstrap_success:
            status.append("Bootstrap OK (BepInEx folders ready).")
        else:
            status.append(
                "Bootstrap timed out — run the game once manually if plugins/interop missing."
            )
    except Exception as e:
        return f"Bootstrap failed: {e}"

    # Deploy bridge
    modding_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    bepinex_mcp_dir = os.path.join(modding_root, "BepInExMCP")
    if not os.path.isdir(bepinex_mcp_dir):
        return f"BepInExMCP not found at {bepinex_mcp_dir}"

    status.append("Building and deploying BepInExMCP...")
    try:
        if is_il2cpp:
            ps_script = os.path.join(bepinex_mcp_dir, "Build-Il2Cpp.ps1")
            if not os.path.isfile(ps_script):
                return f"Build-Il2Cpp.ps1 missing: {ps_script}"
            cmd = [
                "powershell",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                ps_script,
                "-GameDir",
                game_dir,
                "-Configuration",
                "Release",
                "-Deploy",
            ]
            result = subprocess.run(
                cmd, capture_output=True, text=True, cwd=bepinex_mcp_dir
            )
            if result.returncode != 0:
                return f"IL2CPP build/deploy failed:\n{result.stderr or result.stdout}"
        else:
            project_path = os.path.join(bepinex_mcp_dir, "BepInExMCP.csproj")
            result = subprocess.run(
                ["dotnet", "build", project_path, "--configuration", "Release"],
                capture_output=True,
                text=True,
                cwd=bepinex_mcp_dir,
            )
            if result.returncode != 0:
                return f"Mono build failed:\n{result.stderr or result.stdout}"
            plugins_dir = os.path.join(game_dir, "BepInEx", "plugins")
            os.makedirs(plugins_dir, exist_ok=True)
            mono_dll_src = os.path.join(
                bepinex_mcp_dir, "bin", "Release", "netstandard2.0", "BepInExMCP.dll"
            )
            if not os.path.isfile(mono_dll_src):
                mono_dll_src = os.path.join(
                    bepinex_mcp_dir, "bin", "Debug", "netstandard2.0", "BepInExMCP.dll"
                )
            if not os.path.isfile(mono_dll_src):
                return "Built BepInExMCP.dll not found."
            shutil.copy(mono_dll_src, os.path.join(plugins_dir, "BepInExMCP.dll"))
        status.append("Plugin built and deployed.")
    except Exception as e:
        return f"Deploy failed: {e}"

    status.append(f"Done: {game_dir}")
    status.append("Launch the game — bridge should listen on http://localhost:8080/mcp/")
    return "\n".join(status)


def main() -> int:
    parser = argparse.ArgumentParser(description="Full BepInEx + BepInExMCP setup")
    parser.add_argument("game_directory", help="Unity game install root")
    args = parser.parse_args()
    result = setup_game(args.game_directory)
    print(result)
    fail_markers = (
        "error:",
        "failed to download",
        "failed to extract",
        "bootstrap failed",
        "build failed",
        "deploy failed",
        "not found",
    )
    low = result.lower()
    if any(m in low for m in fail_markers) and "done:" not in low:
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
