---
name: bepinex-mcp
description: >-
  Live Unity BepInEx bridge MCP (bepinex-mcp): scene search, component get/set,
  method calls, Harmony-style patches, watchers, profiles. Use when a Unity game is
  running with the bridge plugin, or when applying/verifying mods after Mono RAG or
  IL2CPP decompile research.
---

# bepinex-mcp (live bridge)

MCP server: **`bepinex-mcp`**. Same tool names for **Mono and IL2CPP**.

For end-to-end mod requests (research + apply), also load **`unity-bepinex-modder`**.

## Always first

1. `ping_unity_bridge` or **`get_runtime_capabilities`**
2. Note: `runtime`, `protocolVersion` (~`2.0`), `patchTypes`, `features`, `limits`

Also: `get_game_paths`, `setup_game_modding_environment(game_directory)`.

## Critical rules

### Unity lifecycle
`Awake` / `Start` / `OnEnable` / init methods have **already run** on objects already in the scene.

- Harmony on those methods only affects **future** spawns.
- For current objects: **`set_component_value`** (or call a method), don’t only patch init.

### Selectors vs instance IDs
Instance IDs are temporary. For profiles, watchers, or anything that must survive respawn/scene change, use a **stable selector**:

```json
{
  "scene": "Game",
  "path": "/Player[0]/Root[0]",
  "name": "Player",
  "component": "MyGame.PlayerHealth"
}
```

Path segments: `Name[siblingIndex]`. Re-resolve with `resolve_gameobject_selector`.

### Discovery
Prefer search over blind tree walking:

- `search_gameobjects`, `find_objects_with_component`, `search_runtime_types`
- `get_hierarchy_snapshot`, `list_root_gameobjects` / `list_children`
- `inspect_components`, `describe_runtime_type`, `list_component_methods`

Prefer **`get_component_member`** over full `get_component_details` when you need one value.

### Mutate
- `set_component_value` / `call_component_method`
- `run_bridge_batch` for ordered multi-step ops (respect `maxBatchOperations`)
- If values **snap back**, `get_network_diagnostics` (ownership / authority)

### Do not invent tools
No create/destroy GameObject APIs. Prefer the game’s own spawn/API via `call_component_method`.

## `mod_patch_method`

`patch_code` = complete C# class named **`DynamicPatcher`** with static `Prefix` / `Postfix` matching `patch_type`.

| Runtime | Allowed patch types |
|---------|---------------------|
| **IL2CPP** | `prefix`, `postfix` only |
| **Mono** | those plus `transpiler` / `finalizer` **if** capabilities list them |

```csharp
using HarmonyLib;
using System;

public class DynamicPatcher
{
    public static bool Prefix()
    {
        return false; // skip original
    }
}
```

- Use exact live type/method names and overload `parameter_types` when needed.
- Class can be patched with **no live instances** (blueprint).
- Manage: `list_mod_patches` / `remove_mod_patch`.

## `mod_inject_class`

**Mono only.** IL2CPP → `NotSupportedException`. Optional `attach_to_game_object_id` attaches first compiled `MonoBehaviour`.

## Watchers & events

`watch_component_member`, `watch_active_scene`, `list_watchers`, `remove_watcher`, `events_get_new_events`, `mod_subscribe_to_method`.

Clean up temporary watches/patches unless the user wants them kept.

## Profiles

Reusable packs: `save_mod_profile` → `apply_mod_profile`.  
`activate_mod_profile(..., true)` only with `autoApply: true` when the user wants scene/respawn reapply.

Ops typically: set value, call method, watch member, patch method.

Default store: `%LOCALAPPDATA%/unity-mcp-translator/profiles` (historical name; override with `--profiles-dir`).

## GUI (optional)

`gui_start`, `gui_create_slider`, `gui_create_button` when a local control is useful (non-headless).

## Limits

- Stripped/obfuscated IL2CPP members may be unreadable/uncallable
- IL2CPP patches are managed detours around interop, not native rewrites
- Keep the bridge on localhost for normal use
- Prefer reversible mods
