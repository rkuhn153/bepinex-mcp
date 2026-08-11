import os
import sys
import subprocess
import shutil
import glob
import argparse
import re
from typing import Tuple


def find_managed_dir(game_dir: str) -> str | None:
    """Finds the game's Managed assemblies directory."""
    managed_dir_pattern = os.path.join(game_dir, "*_Data", "Managed")
    managed_dirs = glob.glob(managed_dir_pattern)

    if not managed_dirs:
        # Fallback for some Unity games (e.g., Valheim)
        managed_dir_pattern = os.path.join(game_dir, "unstripped_corlibs")
        managed_dirs = glob.glob(managed_dir_pattern)

    if not managed_dirs:
        print(f"Error: Could not find game's Managed assembly directory.", file=sys.stderr)
        print(f"Looked for pattern: {managed_dir_pattern}", file=sys.stderr)
        return None

    return os.path.abspath(managed_dirs[0])


def detect_tfm(managed_dir: str) -> str | None:
    """
    Detects the Target Framework Moniker (TFM) based on BepInEx documentation.
    """
    if not managed_dir:
        return None

    netstandard_dll = os.path.join(managed_dir, "netstandard.dll")
    if os.path.exists(netstandard_dll):
        print("Found netstandard.dll, setting TFM to 'netstandard2.0'")
        return "netstandard2.0"
    else:
        # Fallback as per docs: "As a general rule, you can always target net35."
        print("Did not find netstandard.dll, falling back to TFM 'net35'")
        return "net35"


def detect_unity_version(game_dir: str) -> str | None:
    """
    Detects the Unity version by reading the globalgamemanagers file.
    """
    data_dir_pattern = os.path.join(game_dir, "*_Data")
    data_dirs = glob.glob(data_dir_pattern)

    if not data_dirs:
        print(f"Error: Could not find game's *_Data directory.", file=sys.stderr)
        return None

    ggm_path = os.path.join(data_dirs[0], "globalgamemanagers")

    if not os.path.exists(ggm_path):
        print(f"Error: Could not find 'globalgamemanagers' file at {ggm_path}", file=sys.stderr)
        print("Unable to auto-detect Unity version.")
        return None

    try:
        with open(ggm_path, 'rb') as f:
            # Read the first 1kb, which is more than enough for the version string
            data = f.read(1024)

        # Regex to find a Unity version string like '5.6.3', '2019.4.40', etc.
        # This is the 'X.Y.Z' format mentioned in the docs.
        match = re.search(b'(\d{1,4}\.\d{1,2}\.\d{1,3})', data)

        if match:
            version_string = match.group(1).decode('utf-8')
            print(f"Found Unity Version: {version_string}")
            return version_string
        else:
            print(f"Error: Could not find Unity version string in {ggm_path}", file=sys.stderr)
            return None

    except Exception as e:
        print(f"Error reading {ggm_path}: {e}", file=sys.stderr)
        return None


def run_mod_creation_logic(game_dir: str, mod_name: str, author: str) -> Tuple[bool, str]:
    """
    Runs the core logic for creating a mod.
    This function is importable and returns a (success, message) tuple.
    """
    # --- 1. Sanitize and Define Variables ---
    safe_author = re.sub(r'\s+', '', author)
    safe_mod_name = re.sub(r'\s+', '', mod_name)
    mod_guid = f"{safe_author}.{safe_mod_name}"
    game_dir_abs = os.path.abspath(game_dir)

    log = []
    log.append(f"--- Starting Mod Creation Logic ---")
    log.append(f"Game Dir: {game_dir_abs}")
    log.append(f"Mod Name: {safe_mod_name}")
    log.append(f"Mod GUID: {mod_guid}")

    # --- 2. Auto-Detect Values ---
    managed_dir = find_managed_dir(game_dir_abs)
    if not managed_dir:
        return (False, "Error: Could not find game's Managed assembly directory.")

    tfm = detect_tfm(managed_dir)
    if not tfm:
        return (False, "Error: Could not detect TFM.")

    unity_version = detect_unity_version(game_dir_abs)
    if not unity_version:
        return (False, "Error: Could not detect Unity version.")

    # --- 3. Build and Run dotnet new Command ---
    command = [
        "dotnet", "new", "bepinex5plugin",
        "-n", safe_mod_name,
        "-T", tfm,
        "-U", unity_version,
        "-PluginGUID", mod_guid
    ]

    log.append("\n" + ("-" * 30))
    log.append(f"Running command:")
    log.append(f"  {' '.join(command)}")
    log.append(f"In directory: {os.getcwd()}")
    log.append(("-" * 30) + "\n")

    try:
        # We capture stdout and stderr to return as part of the message
        result = subprocess.run(command, check=True, capture_output=True, text=True, encoding='utf-8')

        log.append("--- dotnet new stdout ---")
        log.append(result.stdout)

        success_message = f"\n--- SUCCESS! ---"
        success_message += f"\nYour new mod project '{safe_mod_name}' has been created."
        log.append(success_message)

        return (True, "\n".join(log))

    except subprocess.CalledProcessError as e:
        log.append(f"Error: 'dotnet new' command failed.")
        log.append("--- dotnet new stdout ---")
        log.append(e.stdout)
        log.append("--- dotnet new stderr ---")
        log.append(e.stderr)
        log.append("Please ensure the .NET SDK and BepInEx.Templates are installed.")
        log.append("To install templates, run: 'dotnet new --install BepInEx.Templates'")
        return (False, "\n".join(log))

    except FileNotFoundError:
        error_msg = "Error: 'dotnet' command not found. Please ensure the .NET SDK is installed and accessible in your PATH."
        log.append(error_msg)
        return (False, "\n".join(log))
    except Exception as e:
        log.append(f"An unexpected error occurred: {e}")
        return False, "\n".join(log)


def main_cli():
    """
    Main function FOR COMMAND-LINE USE.
    Parses args and calls the core logic function.
    """
    parser = argparse.ArgumentParser(description="Smart BepInEx Mod Starter")
    parser.add_argument("--game_dir", required=True, help="Full path to the game's root directory")
    parser.add_argument("--mod_name", required=True, help="Name of your mod (e.g., MyAwesomeMod)")
    parser.add_argument("--author", required=True, help="Your author name/username")

    args = parser.parse_args()

    success, message = run_mod_creation_logic(args.game_dir, args.mod_name, args.author)

    print(message)  # Print the full log to the console

    if not success:
        sys.exit(1)
    else:
        # Print next steps for CLI users
        print(f"\nNext steps:")
        print(f"  1. cd {re.sub(r'\\s+', '', args.mod_name)}")
        print(f"  2. (Optional) Edit {re.sub(r'\\s+', '', args.mod_name)}.csproj to add more game DLL references.")
        print(f"  3. dotnet build")


if __name__ == "__main__":
    main_cli()