---
name: unity-bepinex-modder
description: >-
  End-to-end Unity modding with bepinex-mcp. Bridge-only workflow always works.
  Optionally uses external Mono RAG or IL2CPP decompiler MCPs if the user has them —
  those are not part of the bepinex-mcp repo.
---

# Unity BepInEx Modder (orchestrator)

**Default stack (this repo only):** live game + **`bepinex-mcp`**.

| You have | What to do |
|----------|------------|
| Only `bepinex-mcp` | Live discovery → set / call / patch / profile / verify |
| + Mono code search MCP | Optional offline research before applying |
| + IL2CPP decompiler MCP | Optional static analysis before applying |

Load skill **`bepinex-mcp`** for tool-level rules (lifecycle, selectors, `DynamicPatcher`).

External research servers (if present) are **not published in this repository**. Typical names in some setups: `gamecode-rag` (Mono dumps), `il2cpp-decompiler-agent` (IL2CPP). If those tools are missing, **do not stall** — use the bridge-only plan below.

## Bridge-only plan (always valid)

### Phase 0 — Connect
1. `get_runtime_capabilities()` → `runtime`, `patchTypes`, limits.
2. If bridge is down: help with plugin install / `setup_game_modding_environment` / `ping_unity_bridge`.

### Phase 1 — Find targets in the live game
3. Understand the request (god mode, fly, etc.).
4. Discover with the bridge only:
   - `search_gameobjects` / `find_objects_with_component` / `search_runtime_types`
   - `inspect_components`, `list_component_methods`, `get_component_member`
5. Keep a **stable selector** for anything beyond a one-shot edit.

### Phase 2 — Apply & verify
6. Prefer lightest fix:
   - live value → `set_component_value`
   - action → `call_component_method`
   - future logic → `mod_patch_method` (check `patchTypes` first)
   - multi-step → `run_bridge_batch`
   - reusable → profile save/apply
7. Verify (re-read, in-game test, watch).
8. Report what changed; clean up temp patches/watchers unless asked to keep them.

### Unspawned objects
If no live instance yet, you can still **`mod_patch_method`** on the class when the type name is known from live type search or prior knowledge.

## Optional research (external MCPs only)

Use **only if the tools actually exist** in the session. Never invent servers.

### If Mono research MCP is available (e.g. semantic code index)
- Discover projects → natural-language search → call graph
- Queries should be **full questions**, not keyword bags
- Then apply via `bepinex-mcp` as above

### If IL2CPP decompiler MCP is available
- List/load dump → search symbols → class info → **decompile before patching**
- Prefer MCP decompile over random CLI when that MCP is connected
- Then apply via `bepinex-mcp`
- Classic Mono dump ≠ IL2CPP Remake of the same title

### If neither research MCP is available
Say so briefly if the user expected offline source search, then continue with **live discovery**. Guessing signatures is worse than reading live methods with `list_component_methods` / `describe_runtime_type`.

## Hard rules

1. Capabilities before patching — IL2CPP = `prefix`/`postfix` only (unless capabilities say otherwise).
2. Lifecycle: patching `Awake`/`Start`/`OnEnable` does not rewrite already-running objects; use `set_component_value` for “now”.
3. Prefer reversible mods; no inventing create/destroy GameObject tools.
4. Networked games: if values snap back, `get_network_diagnostics`.
5. Do not block on missing RAG/decompiler — **bepinex-mcp alone is usable**.

## God mode pattern (bridge-only)

1. Capabilities.
2. Search player/health/damage components live.
3. Temp: set invincible/HP — or durable: prefix `TakeDamage` returning `false` with exact live signature.
4. Verify; optional profile.

`DynamicPatcher` shape — see **`bepinex-mcp`** skill.
