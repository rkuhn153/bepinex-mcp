#!/usr/bin/env python3
"""
Modder's Helper App (v2 - GUI Builder)
This standalone app runs a GUI and an MCP server simultaneously.
"""

import os
import sys
import logging
import httpx
import threading
import tkinter as tk
import queue  # <-- NEW: For thread-safe job queue
import json
import argparse
import time
import asyncio
from typing import Any
# --- NEW: Import Flask for the webhook server ---
from flask import Flask, request, jsonify
from mcp.server.fastmcp import FastMCP
from bridge_client import BridgeClient, BridgeError
from profiles import (
    BATCH_COMMANDS,
    ProfileStore,
    ProfileValidationError,
    default_profiles_dir,
    validate_profile,
)


# --- Configuration ---
logging.basicConfig(
    level=logging.INFO,
    format='%(asctime)s - %(name)s - %(levelname)s - %(message)s',
    stream=sys.stderr
)
logger = logging.getLogger("unityhelper-app")

parser = argparse.ArgumentParser()
parser.add_argument("--game-ip", default="localhost", help="The IP address of the game to mod.")
parser.add_argument(
    "--gui",
    action="store_true",
    help="Open the Tk GUI window on startup. Default is no window; use the gui_start MCP tool later.",
)
parser.add_argument(
    "--headless",
    action="store_true",
    help="Run without the Tk GUI (default). Kept for backward compatibility with launchers.",
)
parser.add_argument(
    "--profiles-dir",
    default=None,
    help="Directory for persistent mod profiles (overrides UNITY_MCP_PROFILES_DIR).",
)
parser.add_argument(
    "--transport",
    choices=("stdio", "sse", "streamable-http"),
    default=None,
    help=(
        "MCP transport. stdio is required when Cursor/Grok launches this as a local MCP server. "
        "Use streamable-http (or sse) for remote clients like Gemini Spark."
    ),
)
parser.add_argument(
    "--host",
    default=None,
    help="HTTP bind host for sse/streamable-http (default: 127.0.0.1). Use 0.0.0.0 for LAN.",
)
parser.add_argument(
    "--port",
    type=int,
    default=None,
    help="HTTP port for sse/streamable-http (default: 8000).",
)
args, unknown_args = parser.parse_known_args()

# Clean sys.argv so FastMCP doesn't crash on custom args
sys.argv = [sys.argv[0]] + unknown_args
GAME_IP = args.game_ip
TRANSPORT = args.transport or "stdio"
MCP_HOST = args.host or "127.0.0.1"
MCP_PORT = int(args.port or 8000)
# Default: no GUI window on startup (MCP hosts spawn this process often).
# Opt in with --gui, or call the gui_start tool at runtime. --headless remains
# accepted and wins if both flags are passed.
HEADLESS = (not bool(args.gui)) or bool(args.headless)

HELPER_BASE_URL = f"http://{GAME_IP}:8080/mcp"
bridge_client = BridgeClient(HELPER_BASE_URL)
profile_store = ProfileStore(args.profiles_dir or default_profiles_dir())
# --- NEW: Thread-Safe Job Queue ---
# This is the "mailbox" between the AI (background thread)
# and the GUI (main thread).
gui_job_queue = queue.Queue()
event_queue = queue.Queue()    # <-- NEW: For the game to send events to the AI
profile_event_queue = queue.Queue()
gui_app = None

# --- MCP Server Setup ---
# host/port only matter for sse and streamable-http (remote MCP / Gemini Spark).
mcp = FastMCP(
    "unity-mod-helper",
    host=MCP_HOST,
    port=MCP_PORT,
    # Keep SSE streamable responses (Gemini Spark uses GET /mcp event streams).
    json_response=False,
)
# --- Webhook Server Setup ---
flask_app = Flask(__name__)

@flask_app.route("/event", methods=["POST"])
def on_game_event():
    """
    This is the REAL webhook endpoint.
    It puts the event into the AI's event_queue.
    """
    try:
        data = request.get_json(force=False, silent=False)
        if not isinstance(data, dict):
            raise ValueError("Webhook payload must be a JSON object.")
        logger.info(
            "[Webhook] Received event: %s",
            data.get("kind") or data.get("event"),
        )

        # Put the new event into the "mailbox" for the AI
        event_queue.put(data)
        profile_event_queue.put(dict(data))

        return jsonify({"status": "received"}), 200
    except Exception as e:
        logger.error(f"[Webhook] Error processing event: {e}")
        return jsonify({"status": "error"}), 500
# --- GUI-Specific Helper ---
# We need a SYNCHRONOUS version of our request sender
# for the GUI sliders to use.
def send_request_sync(endpoint: str = "", params: dict = None):
    """Synchronous helper to send HTTP GET requests from the GUI."""
    url = f"{HELPER_BASE_URL}/{endpoint}"
    try:
        # Use httpx.get (sync) instead of httpx.AsyncClient (async)
        httpx.get(url, params=params, timeout=5)
        # We don't care about the response, just that it sent.
    except Exception as e:
        logger.error(f"GUI Error sending request: {e}")


# --- NEW: GUI Application Class ---

class ModderHelperGUI(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title("Modder's Helper (AI-Powered)")
        self.geometry("450x600")

        # Create a "control" frame for AI-added widgets
        self.control_frame = tk.LabelFrame(self, text="AI-Generated Controls", padx=10, pady=10)
        self.control_frame.pack(fill="both", expand=True, padx=10, pady=10)

        # Add a simple status label
        self.status_label = tk.Label(self, text="Ready for AI commands...", anchor="w", relief="sunken")
        self.status_label.pack(fill="x", side="bottom")

        # Start the loop that checks our "mailbox"
        self.check_gui_queue()

    def check_gui_queue(self):
        """Checks the gui_job_queue for new jobs from the AI."""
        try:
            # Get a job from the queue without blocking
            job = gui_job_queue.get(block=False)

            # We found a job! Process it.
            self.process_gui_job(job)

        except queue.Empty:
            # No job? No problem.
            pass
        finally:
            # Re-schedule this check 100ms from now
            self.after(100, self.check_gui_queue)

    def process_gui_job(self, job):
        """Dispatches jobs to the correct builder."""
        logger.info(f"GUI: Received job: {job['type']}")
        self.status_label.config(text=f"Building widget: {job['label']}...")

        if job["type"] == "slider":
            self.build_slider(job)
        elif job["type"] == "button":
            self.build_button(job)

        self.status_label.config(text="Ready.")

    def build_slider(self, job):
        """Dynamically builds a new slider widget in the GUI."""

        # Create a new frame to hold the label and slider
        widget_frame = tk.Frame(self.control_frame, borderwidth=1, relief="solid")
        widget_frame.pack(fill="x", pady=5, padx=5)

        label = tk.Label(widget_frame, text=job["label"])
        label.pack(side="top", anchor="w")

        # This is the magic callback.
        # It's called every time the user moves the slider.
        def on_slider_change(new_value):
            # This function runs on the main GUI thread.
            # We need to send the HTTP request in a *new* thread
            # so we don't freeze the GUI!

            # The "value" from the slider is a string, which is perfect.
            params = {
                "id": job["go_id"],
                "componentName": job["comp"],
                "memberName": job["member"],
                "value": new_value
            }

            # Run the (blocking) web request in a new daemon thread
            threading.Thread(
                target=send_request_sync,
                args=("component/set_value", params),
                daemon=True
            ).start()

            # Optional: Update a label in real-time
            # (We can add this later)

        # Create the slider
        slider = tk.Scale(
            widget_frame,
            from_=job["min_val"],
            to=job["max_val"],
            orient="horizontal",
            command=on_slider_change
        )

        try:
            # Try to parse the current_val string from the job
            initial_val = float(job["current_val"])
        except (ValueError, KeyError, TypeError):
            # Fall back to min_val if current_val is missing, empty, or not a number
            logger.warning(f"Could not parse current_val '{job.get('current_val')}', defaulting to min.")
            initial_val = job["min_val"]

        slider.set(initial_val)

        slider.pack(fill="x", padx=5, pady=5)
    def build_button(self, job):
        """Dynamically builds a new button widget in the GUI."""

        # This callback is called when the user clicks the button
        def on_button_click():
            # This runs on the main GUI thread.
            # We must run the web request on a new thread
            # to avoid freezing the GUI.

            logger.info(f"Button '{job['label']}' clicked. Calling method: {job['method']}")
            self.status_label.config(text=f"Calling method: {job['method']}...")

            # The params for our 'call_component_method' command
            params = {
                "id": job["go_id"],
                "componentName": job["comp"],
                "methodName": job["method"],
                "args": job["args_json"] # e.g., "[]" or "[\"5\"]"
            }

            # Run the (blocking) web request in a new daemon thread
            threading.Thread(
                target=send_request_sync,
                args=("component/call_method", params),
                daemon=True
            ).start()

            # We can't easily get the result back from the thread,
            # so we'll just reset the status bar after a second.
            self.after(1000, lambda: self.status_label.config(text="Ready."))

        # Create the button
        button = tk.Button(
            self.control_frame,
            text=job["label"],
            command=on_button_click # Wire up the click event
        )
        button.pack(fill="x", padx=5, pady=5)

# --- NEW: Webhook Server Logic ---
# --- NEW: Webhook Server Logic ---
def start_webhook_server():
    """This function runs on Thread 3."""
    logger.info("Webhook server thread started. Listening for game events on http://localhost:8081...")
    try:
        logging.getLogger("werkzeug").setLevel(logging.ERROR)
        # Use werkzeug make_server — flask_app.run() prints
        # " * Serving Flask app..." to stdout and breaks MCP stdio JSON-RPC
        # (Cursor: invalid character '*' looking for beginning of value).
        from werkzeug.serving import make_server

        server = make_server("0.0.0.0", 8081, flask_app, threaded=True)
        server.serve_forever()

    except Exception as e:
        logger.error(f"Webhook Server crashed: {e}", exc_info=True)

async def _send_request(endpoint: str = "", params: dict = None) -> str:
    """Internal helper to send HTTP GET requests to the in-game mod."""
    parameter_names = sorted((params or {}).keys())
    logger.info(
        "Sending request to game: %s/%s with parameter names: %s",
        HELPER_BASE_URL,
        endpoint,
        parameter_names,
    )
    try:
        return await bridge_client.get(endpoint, params=params)
    except BridgeError as exception:
        logger.error("%s", exception)
        return f"❌ Error: {exception}"


async def _post_batch(
    operations: list[dict[str, Any]],
    stop_on_error: bool = False,
) -> str:
    """Send a bounded, ordered JSON batch to the in-game bridge."""
    logger.info("Sending bridge batch with %d operation(s).", len(operations))
    try:
        return await bridge_client.post_batch(operations, stop_on_error)
    except BridgeError as exception:
        logger.error("%s", exception)
        return f"❌ Error: {exception}"

async def _get_capabilities() -> dict | None:
    """Return structured backend capabilities, or None for an older bridge."""
    response = await _send_request("system/capabilities")
    try:
        parsed = json.loads(response)
    except (TypeError, json.JSONDecodeError):
        return None

    if not isinstance(parsed, dict) or not parsed.get("runtime"):
        return None
    return parsed

@mcp.tool()
async def get_runtime_capabilities() -> str:
    """Reports the connected bridge runtime, versions, architecture, and supported patch types."""
    logger.info("Executing get_runtime_capabilities")
    return await _send_request("system/capabilities")

@mcp.tool()
async def get_game_paths() -> str:
    """Queries the live game via the bridge to retrieve the exact absolute paths to the game directory and its main Assembly-CSharp.dll on disk."""
    logger.info("Executing get_game_paths")
    return await _send_request("system/paths")

@mcp.tool()
async def setup_game_modding_environment(game_directory: str) -> str:
    """Downloads BepInEx, runs the game once to bootstrap files (and generate IL2CPP interop assemblies), builds the MCP C# plugin, and deploys it to the game's plugins folder. Works for both Mono and IL2CPP runtimes."""
    import zipfile
    import shutil
    import subprocess
    
    logger.info(f"Executing setup_game_modding_environment for: {game_directory}")
    
    # 1. Validation
    if not game_directory.strip():
        return "❌ Error: game_directory path is required."
        
    game_dir = os.path.abspath(game_directory)
    if not os.path.isdir(game_dir):
        return f"❌ Error: Game directory does not exist or is not a folder: {game_dir}"
        
    # Find game executable
    executables = [f for f in os.listdir(game_dir) if f.endswith(".exe") and "unitycrashhandler" not in f.lower()]
    if not executables:
        return f"❌ Error: Could not find any game executable (.exe) in {game_dir}"
    
    game_exe = os.path.join(game_dir, executables[0])
    
    # Detect Mono or IL2CPP
    is_il2cpp = os.path.exists(os.path.join(game_dir, "GameAssembly.dll"))
    
    # Verify if it's actually a Unity game
    data_folders = [f for f in os.listdir(game_dir) if os.path.isdir(os.path.join(game_dir, f)) and f.endswith("_Data")]
    if not data_folders:
        return f"❌ Error: {game_dir} is not a valid Unity game root (missing *_Data folder)."
        
    runtime_type = "IL2CPP" if is_il2cpp else "Mono"
    logger.info(f"Detected {runtime_type} game layout.")
    
    status = [f"🎮 Detected Unity {runtime_type} game executable: {executables[0]}"]
    
    # 2. Download BepInEx
    if is_il2cpp:
        bepinex_url = "https://builds.bepinex.dev/projects/bepinex_be/785/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.785+6abdba4.zip"
    else:
        bepinex_url = "https://github.com/BepInEx/BepInEx/releases/download/v5.4.22/BepInEx_x64_5.4.22.0.zip"
        
    zip_path = os.path.join(game_dir, "bepinex_temp.zip")
    status.append(f"📥 Downloading BepInEx for {runtime_type}...")
    
    try:
        async with httpx.AsyncClient(timeout=120.0) as client:
            async with client.stream("GET", bepinex_url) as r:
                r.raise_for_status()
                with open(zip_path, "wb") as f:
                    async for chunk in r.aiter_bytes():
                        f.write(chunk)
        status.append("✅ Download complete.")
    except Exception as e:
        return f"❌ Failed to download BepInEx: {str(e)}"
        
    # 3. Extract BepInEx
    status.append("📂 Extracting BepInEx archive...")
    try:
        with zipfile.ZipFile(zip_path, "r") as zip_ref:
            zip_ref.extractall(game_dir)
        os.remove(zip_path)
        status.append("✅ Extraction complete.")
    except Exception as e:
        if os.path.exists(zip_path):
            os.remove(zip_path)
        return f"❌ Failed to extract BepInEx: {str(e)}"
        
    # 4. Bootstrap BepInEx (Run game once)
    status.append("🚀 Bootstrapping: Launching game once to generate config/interop directories...")
    
    # Auto-detect Steam AppID if in Steam Library
    def find_steam_appid(g_dir: str) -> str:
        current = g_dir
        for _ in range(5):
            parent, name = os.path.split(current)
            if name.lower() == "steamapps":
                for f in os.listdir(current):
                    if f.startswith("appmanifest_") and f.endswith(".acf"):
                        acf_path = os.path.join(current, f)
                        try:
                            with open(acf_path, "r", encoding="utf-8", errors="ignore") as file:
                                content = file.read()
                            import re
                            install_dir_match = re.search(r'"installdir"\s+"([^"]+)"', content, re.IGNORECASE)
                            if install_dir_match:
                                install_dir = install_dir_match.group(1).lower()
                                if install_dir in g_dir.lower():
                                    appid_match = re.search(r'"appid"\s+"(\d+)"', content, re.IGNORECASE)
                                    if appid_match:
                                        return appid_match.group(1)
                        except Exception:
                            pass
            current = parent
        return None

    steam_appid = find_steam_appid(game_dir)
    if steam_appid:
        status.append(f"ℹ️ Auto-detected Steam AppID: {steam_appid}. Creating steam_appid.txt...")
        try:
            with open(os.path.join(game_dir, "steam_appid.txt"), "w") as f:
                f.write(steam_appid)
        except Exception as e:
            logger.warning(f"Failed to write steam_appid.txt: {e}")

    # Launch helper
    async def launch_and_wait(use_dll_name: str) -> bool:
        env = os.environ.copy()
        if steam_appid:
            env["SteamAppId"] = steam_appid
            env["SteamGameId"] = steam_appid
            
        logger.info(f"Launching bootstrap process with: {use_dll_name}")
        proc = subprocess.Popen([game_exe], cwd=game_dir, env=env)
        
        wait_limit = 90 if is_il2cpp else 20
        success = False
        
        for idx in range(wait_limit):
            await asyncio.sleep(1)
            # Check if target files created
            if is_il2cpp:
                hash_file = os.path.join(game_dir, "BepInEx", "interop", "assembly-hash.txt")
                if os.path.isfile(hash_file):
                    success = True
                    break
            else:
                plugins_dir = os.path.join(game_dir, "BepInEx", "plugins")
                if os.path.isdir(plugins_dir):
                    success = True
                    break
            
            # If the process exited in the first few seconds without creating files, fail early to try next option
            if idx > 8 and proc.poll() is not None and not success:
                break
                
        # Cleanup
        proc.terminate()
        try:
            proc.wait(timeout=3)
        except subprocess.TimeoutExpired:
            proc.kill()
            
        return success

    try:
        bootstrap_success = await launch_and_wait("winhttp.dll")
        
        # If winhttp.dll failed to load/generate files, try version.dll fallback
        if not bootstrap_success:
            winhttp_path = os.path.join(game_dir, "winhttp.dll")
            version_path = os.path.join(game_dir, "version.dll")
            
            if os.path.isfile(winhttp_path):
                status.append("⚠️ winhttp.dll initialization timed out. Retrying with version.dll proxy fallback...")
                try:
                    if os.path.isfile(version_path):
                        os.remove(version_path)
                    os.rename(winhttp_path, version_path)
                    bootstrap_success = await launch_and_wait("version.dll")
                except Exception as e:
                    logger.warning(f"Failed version.dll proxy rename: {e}")
                    
        if not bootstrap_success:
            status.append("⚠️ Bootstrapping timed out. (Interop files may still be generating in the background, or the game requires a manual run).")
        else:
            status.append("✅ Bootstrap execution succeeded. BepInEx folder structure and interop assemblies generated.")
            
    except Exception as e:
        return f"❌ Failed during bootstrapping run: {str(e)}"
        
    # 5. Build and Deploy Plugin
    modding_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    bepinex_mcp_dir = os.path.join(modding_root, "BepInExMCP")
    
    if not os.path.isdir(bepinex_mcp_dir):
        return f"❌ Error: BepInExMCP source folder not found at: {bepinex_mcp_dir}"
        
    status.append("🔨 Compiling and deploying BepInExMCP plugin DLL...")
    try:
        if is_il2cpp:
            # Build and deploy using powershell Build-Il2Cpp.ps1 script
            ps_script = os.path.join(bepinex_mcp_dir, "Build-Il2Cpp.ps1")
            if not os.path.isfile(ps_script):
                return f"❌ Error: Build-Il2Cpp.ps1 script not found at: {ps_script}"
                
            cmd = ["powershell", "-ExecutionPolicy", "Bypass", "-File", ps_script, "-GameDir", game_dir, "-Configuration", "Release", "-Deploy"]
            result = subprocess.run(cmd, capture_output=True, text=True, cwd=bepinex_mcp_dir)
            if result.returncode != 0:
                return f"❌ Compilation / deployment failed:\n{result.stderr or result.stdout}"
        else:
            # Build Mono project using dotnet build BepInExMCP.csproj
            project_path = os.path.join(bepinex_mcp_dir, "BepInExMCP.csproj")
            if not os.path.isfile(project_path):
                return f"❌ Error: BepInExMCP.csproj not found at: {project_path}"
                
            cmd = ["dotnet", "build", project_path, "--configuration", "Release"]
            result = subprocess.run(cmd, capture_output=True, text=True, cwd=bepinex_mcp_dir)
            if result.returncode != 0:
                return f"❌ Compilation failed:\n{result.stderr or result.stdout}"
                
            # Deploy Mono plugin DLL
            plugins_dir = os.path.join(game_dir, "BepInEx", "plugins")
            os.makedirs(plugins_dir, exist_ok=True)
            
            mono_dll_src = os.path.join(bepinex_mcp_dir, "bin", "Release", "netstandard2.0", "BepInExMCP.dll")
            if not os.path.isfile(mono_dll_src):
                # Fallback to Debug configuration
                mono_dll_src = os.path.join(bepinex_mcp_dir, "bin", "Debug", "netstandard2.0", "BepInExMCP.dll")
                if not os.path.isfile(mono_dll_src):
                    return "❌ Error: Could not find compiled BepInExMCP.dll output file."
                    
            shutil.copy(mono_dll_src, os.path.join(plugins_dir, "BepInExMCP.dll"))
            
        status.append("✅ Plugin built and deployed successfully.")
        
    except Exception as e:
        return f"❌ Error during compilation / deployment: {str(e)}"
        
    status.append(f"\n🎉 Successfully modded game folder: {game_dir}!")
    status.append("You can now launch the game, and the bridge will automatically run and connect.")
    return "\n".join(status)

@mcp.tool()
async def list_root_gameobjects() -> str:
    """Lists all root GameObjects in the active scene."""
    logger.info("Executing list_root_gameobjects")
    return await _send_request("scene/list_root_gameobjects")

@mcp.tool()
async def list_children(parent_id: str = "") -> str:
    """Lists all immediate children of a GameObject by its Instance ID."""
    logger.info(f"Executing list_children with parent_id: {parent_id}")
    if not parent_id.strip():
        return "❌ Error: 'parent_id' is required."
    return await _send_request("gameobject/list_children", params={"id": parent_id})
@mcp.tool()
async def inspect_components(game_object_id: str = "") -> str:
    """Lists all component names on a GameObject by its Instance ID."""
    logger.info(f"Executing inspect_components with game_object_id: {game_object_id}")
    if not game_object_id.strip():
        return "❌ Error: 'game_object_id' is required."
    return await _send_request("gameobject/inspect_components", params={"id": game_object_id})

@mcp.tool()
async def get_component_details(game_object_id: str = "", component_name: str = "") -> str:
    """Gets all public/private fields/properties from a specific component."""
    logger.info(f"Executing get_component_details for {game_object_id} -> {component_name}")
    if not game_object_id.strip() or not component_name.strip():
        return "❌ Error: 'game_object_id' and 'component_name' are required."
    params = {"id": game_object_id, "componentName": component_name}
    return await _send_request("component/get_details", params=params)

@mcp.tool()
async def set_component_value(game_object_id: str = "", component_name: str = "", member_name: str = "", value: str = "") -> str:
    """Sets the value of a public/private field/property on a component."""
    logger.info(f"Executing set_component_value for {game_object_id} -> {component_name}.{member_name} = {value}")
    if not all([game_object_id.strip(), component_name.strip(), member_name.strip()]):
        return "❌ Error: 'game_object_id', 'component_name', and 'member_name' are required."
    params = { "id": game_object_id, "componentName": component_name, "memberName": member_name, "value": value }
    return await _send_request("component/set_value", params=params)

@mcp.tool()
async def call_component_method(game_object_id: str = "", component_name: str = "", method_name: str = "", args_json: str = "[]") -> str:
    """Calls a public/private method on a component with a JSON array of string arguments."""
    logger.info(f"Executing call_component_method for {game_object_id} -> {component_name}.{method_name} with args {args_json}")
    if not all([game_object_id.strip(), component_name.strip(), method_name.strip()]):
        return "❌ Error: 'game_object_id', 'component_name', and 'method_name' are required."
    params = { "id": game_object_id, "componentName": component_name, "methodName": method_name, "args": args_json }
    return await _send_request("component/call_method", params=params)

@mcp.tool()
async def list_component_methods(game_object_id: str = "", component_name: str = "") -> str:
    """Lists all public/private methods on a specific component."""
    logger.info(f"Executing list_component_methods for {game_object_id} -> {component_name}")
    if not game_object_id.strip() or not component_name.strip():
        return "❌ Error: 'game_object_id' and 'component_name' are required."
    params = {"id": game_object_id, "componentName": component_name}
    return await _send_request("component/list_methods", params=params)

@mcp.tool()
async def find_objects_with_component(component_name: str = "") -> str:
    """Searches the entire game for all GameObjects that have a specific component."""
    logger.info(f"Executing find_objects_with_component for {component_name}")
    if not component_name.strip():
        return "❌ Error: 'component_name' is required."
    params = {"componentName": component_name}
    return await _send_request("scene:find_objects_with_component", params=params)


@mcp.tool()
async def ping_unity_bridge() -> str:
    """Checks bridge health without touching game state."""
    return await _send_request("system/ping")


@mcp.tool()
async def search_gameobjects(
    name: str = "",
    component_name: str = "",
    tag: str = "",
    scene: str = "",
    include_inactive: bool = True,
    limit: int = 100,
) -> str:
    """Searches GameObjects by name, component, tag, and scene."""
    if not 1 <= limit <= 1000:
        return "❌ Error: 'limit' must be between 1 and 1000."
    params = {
        "name": name,
        "componentName": component_name,
        "tag": tag,
        "scene": scene,
        "includeInactive": str(include_inactive).lower(),
        "limit": limit,
    }
    return await _send_request("scene/search_gameobjects", params=params)


@mcp.tool()
async def get_hierarchy_snapshot(
    root_id: str = "",
    depth: int = 3,
    max_nodes: int = 500,
) -> str:
    """Returns a bounded recursive hierarchy snapshot with stable paths."""
    if not 0 <= depth <= 16:
        return "❌ Error: 'depth' must be between 0 and 16."
    if not 1 <= max_nodes <= 1000:
        return "❌ Error: 'max_nodes' must be between 1 and 1000."
    params: dict[str, Any] = {"depth": depth, "maxNodes": max_nodes}
    if root_id.strip():
        params["id"] = root_id
    return await _send_request("scene/hierarchy_snapshot", params=params)


@mcp.tool()
async def resolve_gameobject_selector(selector_json: str = "") -> str:
    """Resolves a stable scene/path/component selector to the current Instance ID."""
    try:
        selector = _parse_json_object(selector_json, "selector_json")
    except ValueError as exception:
        return f"❌ Error: {exception}"
    return await _send_request(
        "scene/resolve_selector",
        params={"selector": json.dumps(selector, separators=(",", ":"))},
    )


@mcp.tool()
async def get_component_member(
    game_object_id: str = "",
    component_name: str = "",
    member_name: str = "",
) -> str:
    """Reads one field or property and reports type and writability metadata."""
    if not all(
        [game_object_id.strip(), component_name.strip(), member_name.strip()]
    ):
        return (
            "❌ Error: 'game_object_id', 'component_name', and "
            "'member_name' are required."
        )
    return await _send_request(
        "component/get_member",
        params={
            "id": game_object_id,
            "componentName": component_name,
            "memberName": member_name,
        },
    )


@mcp.tool()
async def search_runtime_types(
    query: str = "",
    offset: int = 0,
    limit: int = 100,
) -> str:
    """Searches loaded game/runtime types by simple or full name."""
    if offset < 0 or not 1 <= limit <= 500:
        return "❌ Error: offset must be nonnegative and limit must be 1-500."
    return await _send_request(
        "type/search",
        params={"query": query, "offset": offset, "limit": limit},
    )


@mcp.tool()
async def describe_runtime_type(
    type_name: str = "",
    offset: int = 0,
    limit: int = 200,
) -> str:
    """Describes fields, properties, and overload-aware methods on a type."""
    if not type_name.strip():
        return "❌ Error: 'type_name' is required."
    if offset < 0 or not 1 <= limit <= 500:
        return "❌ Error: offset must be nonnegative and limit must be 1-500."
    return await _send_request(
        "type/describe",
        params={"typeName": type_name, "offset": offset, "limit": limit},
    )


@mcp.tool()
async def run_bridge_batch(
    operations_json: str = "",
    stop_on_error: bool = False,
) -> str:
    """Runs 1-100 allowlisted read/set/call/query operations on one Unity frame."""
    try:
        operations = json.loads(operations_json)
    except json.JSONDecodeError as exception:
        return f"❌ Error: operations_json is invalid JSON: {exception}"
    if not isinstance(operations, list) or not 1 <= len(operations) <= 100:
        return "❌ Error: operations_json must be an array with 1-100 items."
    for index, operation in enumerate(operations):
        if not isinstance(operation, dict):
            return f"❌ Error: operation {index} must be an object."
        if not isinstance(operation.get("id"), str) or not operation["id"]:
            return f"❌ Error: operation {index} requires a non-empty string id."
        if not isinstance(operation.get("command"), str):
            return f"❌ Error: operation {index} requires a command."
    return await _post_batch(operations, stop_on_error)


@mcp.tool()
async def watch_component_member(
    selector_json: str = "",
    component_name: str = "",
    member_name: str = "",
    interval_ms: int = 500,
    registration_id: str = "",
) -> str:
    """Creates a stable-selector member watcher that delivers change webhooks."""
    try:
        selector = _parse_json_object(selector_json, "selector_json")
    except ValueError as exception:
        return f"❌ Error: {exception}"
    if not component_name.strip() or not member_name.strip():
        return "❌ Error: 'component_name' and 'member_name' are required."
    if not 100 <= interval_ms <= 60_000:
        return "❌ Error: 'interval_ms' must be between 100 and 60000."
    return await _send_request(
        "watch/member",
        params={
            "selector": json.dumps(selector, separators=(",", ":")),
            "componentName": component_name,
            "memberName": member_name,
            "intervalMs": interval_ms,
            "registrationId": registration_id,
        },
    )


@mcp.tool()
async def watch_active_scene(
    interval_ms: int = 500,
    registration_id: str = "",
) -> str:
    """Creates a watcher that emits a webhook when the active scene changes."""
    if not 100 <= interval_ms <= 60_000:
        return "❌ Error: 'interval_ms' must be between 100 and 60000."
    return await _send_request(
        "watch/scene",
        params={"intervalMs": interval_ms, "registrationId": registration_id},
    )


@mcp.tool()
async def list_watchers() -> str:
    """Lists active member and scene watcher registrations."""
    return await _send_request("watch/list")


@mcp.tool()
async def remove_watcher(registration_id: str = "") -> str:
    """Removes one watcher by registration ID."""
    if not registration_id.strip():
        return "❌ Error: 'registration_id' is required."
    return await _send_request(
        "watch/remove",
        params={"registrationId": registration_id},
    )


@mcp.tool()
async def list_mod_patches() -> str:
    """Lists Harmony subscriptions and dynamic patches with lifecycle IDs."""
    return await _send_request("mod:list_patches")


@mcp.tool()
async def remove_mod_patch(registration_id: str = "") -> str:
    """Unpatches one subscription or dynamic patch by lifecycle ID."""
    if not registration_id.strip():
        return "❌ Error: 'registration_id' is required."
    return await _send_request(
        "mod:remove_patch",
        params={"registrationId": registration_id},
    )


@mcp.tool()
async def get_network_diagnostics(game_object_id: str = "") -> str:
    """Reads common ownership/authority/network state without network writes."""
    if not game_object_id.strip():
        return "❌ Error: 'game_object_id' is required."
    return await _send_request(
        "network/diagnostics",
        params={"id": game_object_id},
    )


def _parse_json_object(value: str, parameter_name: str) -> dict[str, Any]:
    if not value.strip():
        raise ValueError(f"'{parameter_name}' is required.")
    try:
        parsed = json.loads(value)
    except json.JSONDecodeError as exception:
        raise ValueError(f"'{parameter_name}' is invalid JSON: {exception}") from exception
    if not isinstance(parsed, dict):
        raise ValueError(f"'{parameter_name}' must be a JSON object.")
    return parsed


@mcp.tool()
async def gui_start() -> str:
    """
    Launches/shows the Modder's Helper App GUI window on demand.
    The process starts without a window; call this when you want sliders/buttons.
    """
    global gui_app
    if 'gui_app' in globals() and gui_app is not None:
        try:
            gui_app.deiconify()
            gui_app.focus_force()
            return "✅ Modder's Helper GUI is already running and has been focused."
        except Exception:
            gui_app = None

    def run_gui():
        global gui_app
        logger.info("GUI background thread started.")
        try:
            gui_app = ModderHelperGUI()
            gui_app.mainloop()
        except Exception as e:
            logger.error(f"GUI crashed: {e}")
        finally:
            gui_app = None
            logger.info("GUI background thread finished.")

    threading.Thread(target=run_gui, daemon=True).start()
    return "✅ Modder's Helper GUI launched on a background thread."


@mcp.tool()
async def gui_create_slider(label: str = "", go_id: str = "", comp: str = "", member: str = "", min_val: str = "0", max_val: str = "100",current_val: str = "") -> str:
    """
    Requests the GUI to build a new slider.
    Puts a 'slider' job into the thread-safe queue.
    """
    logger.info(f"MCP: Queuing 'slider' job for: {label}")

    # 1. Validate inputs
    if not all([label.strip(), go_id.strip(), comp.strip(), member.strip()]):
        return "❌ Error: 'label', 'go_id', 'comp', and 'member' are required."

    try:
        # 2. Create the job description
        job = {
            "type": "slider",
            "label": label,
            "go_id": go_id,
            "comp": comp,
            "member": member,
            "min_val": float(min_val),
            "max_val": float(max_val),
            "current_val": current_val
        }

        # 3. Put the job into the "mailbox"
        gui_job_queue.put(job)

        return f"✅ Queued GUI slider for '{label}'."

    except ValueError:
        return "❌ Error: 'min_val' and 'max_val' must be valid numbers."
    except Exception as e:
        return f"❌ Error queuing job: {e}"
@mcp.tool()
async def gui_create_button(label: str = "", go_id: str = "", comp: str = "", method: str = "", args_json: str = "[]") -> str:
    """
    Requests the GUI to build a new button.
    This button will call a component method when clicked.
    """
    logger.info(f"MCP: Queuing 'button' job for: {label}")

    # 1. Validate inputs
    if not all([label.strip(), go_id.strip(), comp.strip(), method.strip()]):
        return "❌ Error: 'label', 'go_id', 'comp', and 'method' are required."

    try:
        # 2. Create the job description
        job = {
            "type": "button",
            "label": label,
            "go_id": go_id,
            "comp": comp,
            "method": method,
            "args_json": args_json # Pass along the args for the method call
        }

        # 3. Put the job into the "mailbox"
        gui_job_queue.put(job)

        return f"✅ Queued GUI button for '{label}'."

    except Exception as e:
        return f"❌ Error queuing job: {e}"


# --- NEW TOOL: The Event Subscriber ---
@mcp.tool()
async def mod_subscribe_to_method(
    go_id: str = "",
    comp: str = "",
    method: str = "",
    registration_id: str = "",
) -> str:
    """
    Requests the C# mod to create a Harmony patch on a method.
    This will trigger a webhook event when the method is called.
    """
    logger.info(f"MCP: Requesting Harmony patch for: {comp}::{method}")

    if not all([go_id.strip(), comp.strip(), method.strip()]):
        return "❌ Error: 'go_id', 'comp', and 'method' are required."

    params = {
        "id": go_id,
        "componentName": comp,
        "methodName": method,
        "registrationId": registration_id,
    }

    # This calls the "mod:subscribe_to_method" command on the C# mod
    return await _send_request("mod:subscribe_to_method", params=params)


# --- NEW MCP TOOL: The Event Mailbox Checker ---
@mcp.tool()
async def events_get_new_events() -> str:
    """
    Checks the 'event_queue' for new events from the game.
    This is the AI's "perception loop."
    """
    logger.info("MCP: Checking for new game events...")

    events = []
    # Loop and get all items currently in the queue
    while not event_queue.empty():
        try:
            event = event_queue.get(block=False)
            events.append(event)
        except queue.Empty:
            break # The queue is empty

    if not events:
        return "[]" # Return an empty list string

    # Return the list of events as a JSON string
    return json.dumps(events)

# --- NEW TOOL: Harmony Method Patcher ---
@mcp.tool()
async def mod_patch_method(
    target_class: str = "",
    target_method: str = "",
    parameter_types: str = "",
    patch_type: str = "prefix",
    patch_code: str = "",
    registration_id: str = "",
) -> str:
    """
    Requests the C# mod to create a Harmony patch on a game method.
    This will modify the behavior of the target method.
    
    Args:
        target_class: The full class name (e.g., "Game.PlayerController")
        target_method: The method name to patch
        parameter_types: Comma-separated parameter type names (e.g., "System.Single,System.Boolean")
        patch_type: Backend-supported patch type. IL2CPP supports prefix/postfix;
                    Mono also reports transpiler/finalizer support.
        patch_code: C# code for the patch method (as a string)
    """
    patch_type = patch_type.strip().lower()
    logger.info(f"MCP: Requesting Harmony {patch_type} patch for: {target_class}::{target_method}")
    
    if not all([target_class.strip(), target_method.strip()]):
        return "❌ Error: 'target_class' and 'target_method' are required."
    if not patch_code.strip():
        return "❌ Error: 'patch_code' is required."
    if patch_type not in {"prefix", "postfix", "transpiler", "finalizer"}:
        return "❌ Error: 'patch_type' must be prefix, postfix, transpiler, or finalizer."

    capabilities = await _get_capabilities()
    if capabilities:
        supported_patch_types = {
            str(value).lower()
            for value in capabilities.get("patchTypes", [])
        }
        if supported_patch_types and patch_type not in supported_patch_types:
            runtime = capabilities.get("runtime", "connected")
            supported = ", ".join(sorted(supported_patch_types))
            return (
                f"❌ Error: The {runtime} bridge does not support '{patch_type}' patches. "
                f"Supported patch types: {supported}."
            )
    
    params = {
        "targetClass": target_class,
        "targetMethod": target_method,
        "parameterTypes": parameter_types,
        "patchType": patch_type,
        "patchCode": patch_code,
        "registrationId": registration_id,
    }
    
    # This calls the "mod:patch_method" command on the C# mod
    return await _send_request("mod:patch_method", params=params)


@mcp.tool()
async def mod_inject_class(
    class_code: str = "",
    attach_to_game_object_id: str = "",
) -> str:
    """
    Compiles and loads a C# class string into the game's runtime memory.
    Optionally attaches it to a GameObject as a MonoBehaviour component.

    Args:
        class_code: The C# source code to compile and inject.
        attach_to_game_object_id: The instance ID of the GameObject to attach the compiled MonoBehaviour class to (optional).
    """
    logger.info("Executing mod_inject_class")
    if not class_code.strip():
        return "❌ Error: 'class_code' is required."

    payload = {
        "classCode": class_code,
        "attachToGameObjectId": attach_to_game_object_id
    }

    try:
        # Call the "mod:inject_class" POST endpoint on the C# mod
        return await bridge_client.post("mod:inject_class", json_data=payload)
    except BridgeError as exception:
        logger.error("%s", exception)
        return f"❌ Error: {exception}"


async def _validate_profile_runtime(profile: dict[str, Any]) -> None:
    capabilities = await _get_capabilities()
    if not capabilities:
        raise ProfileValidationError(
            "The connected bridge does not report protocol-v2 capabilities."
        )
    supported = {
        str(value).lower() for value in capabilities.get("patchTypes", [])
    }
    maximum = int(
        capabilities.get("limits", {}).get("maxBatchOperations", 100)
    )
    if len(profile["operations"]) > maximum:
        raise ProfileValidationError(
            f"Profile has more than the bridge limit of {maximum} operations."
        )
    for operation in profile["operations"]:
        if operation["command"] == "mod:patch_method":
            patch_type = operation.get("patchType", "prefix").lower()
            if supported and patch_type not in supported:
                runtime = capabilities.get("runtime", "connected")
                raise ProfileValidationError(
                    f"The {runtime} bridge does not support '{patch_type}' patches."
                )


async def _resolve_profile_selector(selector: dict[str, Any]) -> int:
    response = await bridge_client.get(
        "scene/resolve_selector",
        params={"selector": json.dumps(selector, separators=(",", ":"))},
    )
    parsed = json.loads(response)
    instance_id = parsed.get("id") if isinstance(parsed, dict) else None
    if not isinstance(instance_id, int):
        raise BridgeError("Selector resolution did not return an integer id.")
    return instance_id


async def _apply_profile_data(
    profile: dict[str, Any],
    profile_name: str,
) -> dict[str, Any]:
    await _validate_profile_runtime(profile)
    results: list[dict[str, Any]] = []
    pending: list[dict[str, Any]] = []

    async def flush_batch() -> None:
        if not pending:
            return
        raw = await bridge_client.post_batch(list(pending), stop_on_error=True)
        parsed = json.loads(raw)
        batch_results = parsed.get("results", []) if isinstance(parsed, dict) else []
        results.extend(batch_results)
        pending.clear()
        failed = next(
            (
                item
                for item in batch_results
                if isinstance(item, dict) and not item.get("ok", False)
            ),
            None,
        )
        if failed:
            raise BridgeError(
                f"Profile batch operation failed: {failed.get('error')}"
            )

    for index, operation in enumerate(profile["operations"]):
        command = operation["command"]
        operation_id = f"{profile_name}-{index}"
        if command in BATCH_COMMANDS:
            instance_id = await _resolve_profile_selector(operation["selector"])
            parameters: dict[str, Any] = {
                "id": instance_id,
                "componentName": operation["componentName"],
            }
            if command == "component/set_value":
                parameters.update(
                    memberName=operation["memberName"],
                    value=operation["value"],
                )
            else:
                parameters.update(
                    methodName=operation["methodName"],
                    args=json.dumps(operation.get("args", [])),
                )
            pending.append(
                {
                    "id": operation_id,
                    "command": command,
                    "parameters": parameters,
                }
            )
            continue

        await flush_batch()
        registration_id = operation.get("registrationId") or operation_id
        if command == "watch/member":
            try:
                await bridge_client.get(
                    "watch/remove",
                    params={"registrationId": registration_id},
                )
            except BridgeError:
                pass
            raw = await bridge_client.get(
                "watch/member",
                params={
                    "selector": json.dumps(
                        operation["selector"], separators=(",", ":")
                    ),
                    "componentName": operation["componentName"],
                    "memberName": operation["memberName"],
                    "intervalMs": operation.get("intervalMs", 500),
                    "registrationId": registration_id,
                },
            )
        elif command == "mod:patch_method":
            raw = await bridge_client.get(
                "mod:patch_method",
                params={
                    "targetClass": operation["targetClass"],
                    "targetMethod": operation["targetMethod"],
                    "parameterTypes": operation.get("parameterTypes", ""),
                    "patchType": operation.get("patchType", "prefix"),
                    "patchCode": operation["patchCode"],
                    "registrationId": registration_id,
                },
            )
        else:
            raise ProfileValidationError(
                f"Unsupported profile command '{command}'."
            )
        results.append(
            {
                "id": operation_id,
                "ok": True,
                "result": json.loads(raw),
            }
        )

    await flush_batch()
    return {"profile": profile_name, "ok": True, "results": results}


async def _apply_profile_name(name: str) -> dict[str, Any]:
    profile = profile_store.get(name)
    return await _apply_profile_data(profile, name)


@mcp.tool()
async def save_mod_profile(name: str = "", profile_json: str = "") -> str:
    """Validates and saves a version-1 persistent mod profile."""
    try:
        profile = _parse_json_object(profile_json, "profile_json")
        validated = validate_profile(profile)
        await _validate_profile_runtime(validated)
        validated = profile_store.save(name, validated)
        return json.dumps(validated)
    except (ValueError, OSError, BridgeError) as exception:
        return f"❌ Error: {exception}"


@mcp.tool()
async def get_mod_profile(name: str = "") -> str:
    """Returns one saved profile."""
    try:
        return json.dumps(profile_store.get(name))
    except (ValueError, OSError) as exception:
        return f"❌ Error: {exception}"


@mcp.tool()
async def list_mod_profiles() -> str:
    """Lists profile names, auto-apply state, activation, and operation counts."""
    return json.dumps(profile_store.list())


@mcp.tool()
async def apply_mod_profile(name: str = "") -> str:
    """Re-resolves selectors and applies one saved profile now."""
    try:
        return json.dumps(await _apply_profile_name(name))
    except (ValueError, OSError, BridgeError, json.JSONDecodeError) as exception:
        return f"❌ Error: {exception}"


@mcp.tool()
async def delete_mod_profile(name: str = "") -> str:
    """Deletes one saved profile and deactivates it."""
    try:
        profile_store.delete(name)
        return json.dumps({"status": "ok", "name": name})
    except (ValueError, OSError) as exception:
        return f"❌ Error: {exception}"


@mcp.tool()
async def activate_mod_profile(name: str = "", active: bool = True) -> str:
    """Opts a profile into or out of webhook-triggered auto-application."""
    try:
        profile = profile_store.get(name)
        names = profile_store.set_active(name, active)
        if active and profile.get("autoApply", False):
            try:
                await bridge_client.get(
                    "watch/scene",
                    params={
                        "intervalMs": 500,
                        "registrationId": "profile-auto-scene",
                    },
                )
            except BridgeError as exception:
                if "already exists" not in str(exception).lower():
                    raise
        return json.dumps({"status": "ok", "activeProfiles": names})
    except (ValueError, OSError, BridgeError) as exception:
        return f"❌ Error: {exception}"


async def _profile_selector_snapshot(
    profile: dict[str, Any],
) -> dict[str, int | None]:
    snapshot: dict[str, int | None] = {}
    for operation in profile["operations"]:
        selector = operation.get("selector")
        if not isinstance(selector, dict):
            continue
        key = json.dumps(selector, sort_keys=True, separators=(",", ":"))
        if key in snapshot:
            continue
        try:
            snapshot[key] = await _resolve_profile_selector(selector)
        except (BridgeError, json.JSONDecodeError):
            snapshot[key] = None
    return snapshot


def profile_auto_apply_worker() -> None:
    """Consumes a private webhook queue without draining MCP-visible events."""
    last_event_trigger = 0.0
    selector_states: dict[tuple[str, str], int | None] = {}
    while True:
        try:
            event = profile_event_queue.get(timeout=1.0)
        except queue.Empty:
            event = None
        kind = event.get("kind") if isinstance(event, dict) else None
        event_triggered = kind in {"scene.changed", "watch.target_lost"}
        now = time.monotonic()
        if event_triggered:
            if now - last_event_trigger < 0.5:
                event_triggered = False
            else:
                last_event_trigger = now
        active_names = profile_store.auto_apply_names()
        active_keys: set[tuple[str, str]] = set()
        for name in active_names:
            try:
                profile = profile_store.get(name)
                snapshot = asyncio.run(_profile_selector_snapshot(profile))
                selector_changed = False
                for selector_key, instance_id in snapshot.items():
                    state_key = (name, selector_key)
                    active_keys.add(state_key)
                    if (
                        state_key in selector_states
                        and selector_states[state_key] != instance_id
                    ):
                        selector_changed = True
                    selector_states[state_key] = instance_id
                if not event_triggered and not selector_changed:
                    continue
                result = asyncio.run(_apply_profile_data(profile, name))
                logger.info("Auto-applied profile '%s': %s", name, result["ok"])
            except Exception as exception:
                logger.error(
                    "Auto-apply failed for profile '%s': %s",
                    name,
                    exception,
                )
        for state_key in set(selector_states) - active_keys:
            selector_states.pop(state_key, None)


def start_mcp_server():
    """Blocking MCP serve. Prefer calling this on the main thread for stdio."""
    if TRANSPORT in ("sse", "streamable-http"):
        path = "/sse" if TRANSPORT == "sse" else "/mcp"
        logger.info(
            "MCP server listening (transport=%s) at http://%s:%s%s ...",
            TRANSPORT,
            MCP_HOST,
            MCP_PORT,
            path,
        )
    else:
        logger.info("MCP server listening (transport=%s)...", TRANSPORT)
    try:
        if TRANSPORT == "streamable-http":
            # Gemini Spark-compatible ASGI stack.
            # Logs showed Google IPs hitting us with:
            #   HEAD /mcp -> 405, GET /mcp -> 406 (bad Accept),
            #   GET /.well-known/oauth-protected-resource* -> 404
            # while ListTools sometimes succeeded. Fix those probes.
            import anyio
            import uvicorn
            from starlette.middleware.cors import CORSMiddleware
            from starlette.requests import Request
            from starlette.responses import JSONResponse, PlainTextResponse, Response
            from starlette.routing import Route
            from starlette.types import ASGIApp, Receive, Scope, Send

            class GeminiCompatMiddleware:
                """HEAD + Accept fixes for Gemini Spark / Google MCP probes."""

                def __init__(self, app: ASGIApp) -> None:
                    self.app = app

                async def __call__(
                    self, scope: Scope, receive: Receive, send: Send
                ) -> None:
                    if scope["type"] != "http":
                        await self.app(scope, receive, send)
                        return

                    method = scope.get("method", "GET").upper()
                    path = scope.get("path", "") or ""
                    headers_list = list(scope.get("headers") or [])

                    def _header(name: str) -> str | None:
                        for k, v in headers_list:
                            if k.decode("latin-1").lower() == name:
                                return v.decode("latin-1")
                        return None

                    is_mcp = path == "/mcp" or path.startswith("/mcp/")
                    session_id = _header("mcp-session-id")

                    # Gemini Spark URL validation often does GET/HEAD /mcp with no
                    # session. Bare StreamableHTTP returns 400 → "could not be reached".
                    if is_mcp and method in ("GET", "HEAD") and not session_id:
                        body = (
                            b'{"status":"ok","server":"unity-mod-helper",'
                            b'"transport":"streamable-http","auth":"none",'
                            b'"message":"MCP endpoint ready. Use POST initialize '
                            b'to start a session."}'
                        )
                        if method == "HEAD":
                            body = b""
                        await Response(
                            content=body,
                            status_code=200,
                            media_type="application/json",
                            headers={
                                "Allow": "GET, POST, DELETE, OPTIONS, HEAD",
                                "Cache-Control": "no-store",
                            },
                        )(scope, receive, send)
                        return

                    # Inject Accept when missing/wrong so session GET SSE / POST don't 406.
                    if is_mcp:
                        accept = _header("accept")
                        accept_idx = None
                        for i, (k, v) in enumerate(headers_list):
                            if k.decode("latin-1").lower() == "accept":
                                accept_idx = i
                                break
                        needs_json = "application/json" not in (accept or "").lower()
                        needs_sse = "text/event-stream" not in (accept or "").lower()
                        # Also treat Accept: */* as needing both (startswith fails for */*)
                        if accept is None or accept.strip() == "*/*" or needs_json or needs_sse:
                            fixed = "application/json, text/event-stream"
                            raw = fixed.encode("latin-1")
                            if accept_idx is None:
                                headers_list.append((b"accept", raw))
                            else:
                                headers_list[accept_idx] = (b"accept", raw)
                            scope = dict(scope)
                            scope["headers"] = headers_list

                    await self.app(scope, receive, send)

            async def health(_request: Request) -> JSONResponse:
                return JSONResponse(
                    {
                        "status": "ok",
                        "server": "unity-mod-helper",
                        "transport": "streamable-http",
                        "mcp_path": "/mcp",
                        "auth": "none",
                    }
                )

            async def root(_request: Request) -> PlainTextResponse:
                return PlainTextResponse(
                    "unity-mod-helper MCP is running.\n"
                    "Streamable HTTP endpoint: POST/GET /mcp\n"
                    "Auth: none (open for Gemini Spark tunnel use)\n"
                )

            async def oauth_protected_resource(request: Request) -> JSONResponse:
                # RFC 9728-style metadata. Empty authorization_servers = no OAuth required.
                # Gemini Spark probes both /mcp and root variants.
                base = str(request.base_url).rstrip("/")
                resource = f"{base}/mcp"
                return JSONResponse(
                    {
                        "resource": resource,
                        "authorization_servers": [],
                        "scopes_supported": [],
                        "bearer_methods_supported": [],
                        "resource_documentation": base + "/",
                    }
                )

            async def oauth_authorization_server(request: Request) -> JSONResponse:
                # Advertise that this is not an OAuth AS (clients should proceed without auth).
                base = str(request.base_url).rstrip("/")
                return JSONResponse(
                    {
                        "issuer": base,
                        "authorization_endpoint": f"{base}/authorize",
                        "token_endpoint": f"{base}/token",
                        "registration_endpoint": f"{base}/register",
                        "response_types_supported": ["code"],
                        "grant_types_supported": ["authorization_code"],
                        "code_challenge_methods_supported": ["S256"],
                        "token_endpoint_auth_methods_supported": ["none"],
                        # Hint: open MCP — no real OAuth gate.
                        "service_documentation": base + "/",
                    }
                )

            async def oauth_not_required(_request: Request) -> JSONResponse:
                return JSONResponse(
                    {
                        "error": "unauthorized_client",
                        "error_description": (
                            "This MCP server does not require OAuth. "
                            "Call /mcp directly without a bearer token."
                        ),
                    },
                    status_code=400,
                )

            app = mcp.streamable_http_app()
            # Discovery + health routes Gemini Spark probes (seen in access logs).
            for route in (
                Route("/", endpoint=root, methods=["GET", "HEAD"]),
                Route("/health", endpoint=health, methods=["GET", "HEAD"]),
                Route(
                    "/.well-known/oauth-protected-resource",
                    endpoint=oauth_protected_resource,
                    methods=["GET", "HEAD"],
                ),
                Route(
                    "/.well-known/oauth-protected-resource/mcp",
                    endpoint=oauth_protected_resource,
                    methods=["GET", "HEAD"],
                ),
                Route(
                    "/.well-known/oauth-authorization-server",
                    endpoint=oauth_authorization_server,
                    methods=["GET", "HEAD"],
                ),
                Route("/authorize", endpoint=oauth_not_required, methods=["GET", "POST"]),
                Route("/token", endpoint=oauth_not_required, methods=["POST"]),
                Route("/register", endpoint=oauth_not_required, methods=["POST"]),
            ):
                app.routes.insert(0, route)

            app.add_middleware(GeminiCompatMiddleware)
            app.add_middleware(
                CORSMiddleware,
                allow_origins=["*"],
                allow_methods=["GET", "POST", "DELETE", "OPTIONS", "HEAD"],
                allow_headers=["*"],
                expose_headers=[
                    "Mcp-Session-Id",
                    "mcp-session-id",
                    "MCP-Protocol-Version",
                ],
            )
            config = uvicorn.Config(
                app,
                host=MCP_HOST,
                port=MCP_PORT,
                log_level="info",
                timeout_keep_alive=120,
                # Important behind cloudflared: do not drop long-lived GET streams.
                ws="none",
            )
            server = uvicorn.Server(config)
            anyio.run(server.serve)
        else:
            mcp.run(transport=TRANSPORT)
    except Exception as e:
        logger.error(f"MCP Server crashed: {e}", exc_info=True)
        raise


def start_background_services():
    """Webhook + profile auto-apply (safe alongside stdio MCP)."""
    webhook_thread = threading.Thread(target=start_webhook_server, daemon=True)
    webhook_thread.start()
    profile_thread = threading.Thread(
        target=profile_auto_apply_worker,
        daemon=True,
    )
    profile_thread.start()


# --- Main Application Entry Point ---
if __name__ == "__main__":
    # MCP stdio protocol: stdout is JSON-RPC only. Logs stay on stderr.
    # Flask must NOT print to stdout (see start_webhook_server / make_server).
    logger.info(
        "Starting Modder's Helper App (v3) headless=%s transport=%s...",
        HEADLESS,
        TRANSPORT,
    )

    start_background_services()

    # Remote HTTP transports (Gemini Spark / tunnels): run uvicorn on the
    # main thread so the process stays up for the full HTTP lifetime.
    if TRANSPORT in ("sse", "streamable-http"):
        if not HEADLESS:
            logger.info("Starting GUI on a background thread (HTTP MCP on main)...")
            threading.Thread(
                target=lambda: ModderHelperGUI().mainloop(),
                daemon=True,
            ).start()
        try:
            start_mcp_server()
        except KeyboardInterrupt:
            logger.info("HTTP MCP interrupted. Shutting down.")
    else:
        # stdio: default is headless so MCP hosts do not pop a window.
        # With --gui, MCP runs in the background and the GUI owns main.
        mcp_thread = threading.Thread(target=start_mcp_server, daemon=True)
        mcp_thread.start()

        if HEADLESS:
            logger.info(
                "Running without GUI (default). Use gui_start or --gui to open it. "
                "Press Ctrl+C to stop."
            )
            try:
                while True:
                    time.sleep(1)
            except KeyboardInterrupt:
                logger.info("Headless server interrupted. Shutting down.")
        else:
            logger.info("Starting GUI on main thread (--gui)...")
            app = ModderHelperGUI()
            app.mainloop()
            logger.info("GUI window closed. Shutting down.")

