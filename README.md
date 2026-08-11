# bepinex-mcp

Live **Unity + BepInEx** bridge for AI agents and tools via **MCP (Model Context Protocol)**.

Inspect the running game, change fields, call methods, apply Harmony-style patches, watch values, and save reusable mod profiles — without writing a one-off plugin for every tweak.

| Piece | Path | Role |
|--------|------|------|
| Mono plugin | `plugins/` (`BepInExMCP`) | BepInEx 5 / Unity Mono HTTP bridge |
| IL2CPP plugin | `plugins/BepInExMCP.IL2CPP/` | BepInEx 6 / Unity IL2CPP HTTP bridge |
| MCP server | `mcp/ModdersHelperApp.py` | FastMCP client → bridge |

Both plugins expose the same HTTP API (protocol **2.0**). The Python process is what MCP clients connect to (`bepinex-mcp`).

## What you can do

| Area | Capabilities |
|------|----------------|
| **Discover** | Search GameObjects / components / runtime types; walk hierarchy; resolve stable path selectors |
| **Read / write** | Get or set component fields/properties; call methods; batch several ops in one request |
| **Patch** | Dynamic Harmony-style `prefix` / `postfix` (Mono may also allow transpiler/finalizer if capabilities say so); list/remove patches; method-call subscriptions |
| **Watch** | Watch a member or active scene; pull new events |
| **Profiles** | Save/list/apply/delete named JSON packs (sets, calls, watches, patches); optional auto-reapply |
| **Setup** | Probe paths, one-shot BepInEx-ish setup helper, ping the bridge |
| **GUI** | Optional local sliders/buttons bound to live members (when not headless) |

**Not supported:** inventing create/destroy GameObject tools. Prefer calling the game’s own spawn/API methods.

**IL2CPP limits:** patches are managed detours around interop (not native rewrites). Stripped/obfuscated members may be missing. `mod_inject_class` is **Mono only**.

Always start with **`get_runtime_capabilities`** (or `ping_unity_bridge`) so you know runtime, protocol version, allowed patch types, and limits.

## MCP tools

Names as exposed by `mcp/ModdersHelperApp.py`:

### Connection & setup
- `ping_unity_bridge` — is the in-game plugin up?
- `get_runtime_capabilities` — runtime, protocol, features, limits, patch types
- `get_game_paths` / `setup_game_modding_environment` — paths and install helpers

### Scene & objects
- `list_root_gameobjects` / `list_children`
- `search_gameobjects` / `find_objects_with_component`
- `get_hierarchy_snapshot` / `resolve_gameobject_selector`
- `inspect_components`

### Components & types
- `get_component_details` / `get_component_member`
- `set_component_value` / `call_component_method` / `list_component_methods`
- `search_runtime_types` / `describe_runtime_type`
- `get_network_diagnostics` — when values snap back (ownership / net)
- `run_bridge_batch` — ordered multi-step ops

### Patching & events
- `mod_patch_method` — dynamic C# `DynamicPatcher` with `Prefix`/`Postfix`
- `mod_subscribe_to_method` / `events_get_new_events`
- `list_mod_patches` / `remove_mod_patch`
- `mod_inject_class` — Mono only

### Watchers
- `watch_component_member` / `watch_active_scene`
- `list_watchers` / `remove_watcher`

### Profiles
- `save_mod_profile` / `get_mod_profile` / `list_mod_profiles`
- `apply_mod_profile` / `delete_mod_profile` / `activate_mod_profile`

Stored under `%LOCALAPPDATA%/unity-mcp-translator/profiles` (override with `--profiles-dir`). Folder name is historical.

### Optional GUI
- `gui_start` / `gui_create_slider` / `gui_create_button`

Stable selectors for profiles/watchers use scene + hierarchy path (`Name[siblingIndex]`), not temporary instance IDs. Example:

```json
{
  "scene": "Game",
  "path": "/Player[0]/Camera[0]",
  "component": "MyGame.PlayerHealth"
}
```

## Requirements

- Python 3.11+ (3.13 tested)
- Unity game with **BepInEx 5 (Mono)** or **BepInEx 6 IL2CPP**
- .NET SDK to build plugins

## Compatibility

### Mono plugin (`plugins/` → `BepInExMCP.dll`)

| | |
|--|--|
| **Runtime** | Unity **Mono** only (not IL2CPP) |
| **Loader** | [BepInEx 5](https://docs.bepinex.dev/) for Unity Mono |
| **Unity** | Games on **Unity 2018 or newer** (Mono / .NET Standard 2.0 player). Most 2019–2022 LTS titles are in this range. |
| **Platform** | Windows x64 is the usual setup |

Use this plugin when the game is Mono and BepInEx 5 installs cleanly. For **IL2CPP** games, use the IL2CPP plugin instead.

### IL2CPP plugin (`plugins/BepInExMCP.IL2CPP/`)

Separate build for BepInEx **6** IL2CPP. You need that game’s generated `BepInEx/interop` after one launch — see [`plugins/README.IL2CPP.md`](plugins/README.IL2CPP.md).

## Quick start

### 1. Build & install a plugin

**Mono (BepInEx 5):**

```powershell
cd plugins
dotnet build -c Release
# Copy bin/Release/netstandard2.0/BepInExMCP.dll → <Game>/BepInEx/plugins/
```

**IL2CPP (BepInEx 6):** needs that game’s `BepInEx/interop` after one launch — see [`plugins/README.IL2CPP.md`](plugins/README.IL2CPP.md).

### 2. Run the game, then the MCP server

```powershell
cd mcp
pip install -r requirements.txt
python ModdersHelperApp.py --transport=stdio --headless
```

Without `--headless`, a small Tk GUI can host sliders/buttons. Bridge default: `http://localhost:8080/mcp` (see `--help` for flags).

### 3. Wire an MCP client

**Cursor** (`~/.cursor/mcp.json`):

```json
"bepinex-mcp": {
  "command": "C:/Python313/python.exe",
  "args": [
    "C:/path/to/bepinex-mcp/mcp/ModdersHelperApp.py",
    "--transport=stdio",
    "--headless"
  ]
}
```

**Grok Build** (`~/.grok/config.toml`):

```toml
[mcp_servers.bepinex-mcp]
command = 'C:\Python313\python.exe'
args = [
  'C:\path\to\bepinex-mcp\mcp\ModdersHelperApp.py',
  "--transport=stdio",
  "--headless",
]
enabled = true
```

## AI skills

Agent skills (for Cursor / Grok / similar) live in [`skills/`](skills/):

| Skill | Role |
|--------|------|
| [`skills/bepinex-mcp`](skills/bepinex-mcp/SKILL.md) | Live bridge tools (plugin + this MCP only) |
| [`skills/unity-bepinex-modder`](skills/unity-bepinex-modder/SKILL.md) | End-to-end habits; research MCPs optional if you have them |

**Usable with just this repo** (live bridge only). Optional research MCPs from the same suite:

| Need | Repo |
|------|------|
| Live get/set/patch (this) | [bepinex-mcp](https://github.com/rkuhn153/bepinex-mcp) |
| Mono code search (offline) | [gamecode-rag](https://github.com/rkuhn153/gamecode-rag) |
| IL2CPP static decompile | [il2cpp-decompiler](https://github.com/rkuhn153/il2cpp-decompiler) (needs [Il2CppDumper](https://github.com/Perfare/Il2CppDumper) for dumps) |

Skills do not require research MCPs (see [`skills/README.md`](skills/README.md)).

## Layout

```
bepinex-mcp/
  plugins/     # Mono + IL2CPP BepInEx plugins
  mcp/         # FastMCP Python server
  skills/      # Agent skill markdown for AI clients
  README.md
  LICENSE
  .gitignore
```

## License

MIT — see [LICENSE](LICENSE).
