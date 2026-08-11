---
name: unity-bepinex-modder
description: >-
  Orchestrates Unity game modding: routes Mono vs IL2CPP research, then applies
  live changes through bepinex-mcp. Use for god mode, fly, noclip, spawn, patches,
  Remake vs Classic, or any multi-step Unity BepInEx mod request.
---

# Unity BepInEx Modder (orchestrator)

Combine research + live bridge. Always route by **runtime**.

| Runtime | Research | Live apply |
|---------|----------|------------|
| **Mono** | `gamecode-rag` | `bepinex-mcp` |
| **IL2CPP** | `il2cpp-decompiler-agent` | `bepinex-mcp` |

Load skill **`bepinex-mcp`** for live tool rules. Classic (Mono) and Remake (IL2CPP) of the same title are **different runtimes** — never cross research tools.

## Skill routing

1. Read this skill.
2. `get_runtime_capabilities()` on `bepinex-mcp` (or note bridge down).
3. Research:
   - Mono → `gamecode-rag` (need valid `project_id` from `list_available_projects`)
   - IL2CPP → `il2cpp-decompiler-agent` (**MCP first**, not random CLI)
4. Apply / verify → `bepinex-mcp`

## Master plan

### Phase 0 — Identify

1. **`get_runtime_capabilities()`** → `runtime`, `patchTypes`, limits.
2. Discover sources:
   - **Mono:** `list_available_projects` — do not RAG until you have a real `project_id`.
   - **IL2CPP:** `list_dumps` first. If empty, dump once, then **return to MCP** (`load_project`). Missing dump ≠ abandon MCP.

### Phase 1 — Research

3. Understand the request.
4. Research with the **correct** server only:

**Mono (`gamecode-rag`):**
- `code_search_and_rerank(project_id, query)` — query must be a **full natural-language question**, not keywords.
- `code_graph_search(project_id, node_id)` — callers/callees of the best hit.

**IL2CPP (`il2cpp-decompiler-agent`):**
- `list_dumps` → `load_project(GameAssembly.dll, script.json)`
- `search_symbols` → `get_class_info` → **`decompile_method` before patching**
- Do **not** use Mono RAG for an IL2CPP remake, or patch from interop field names alone.

5. Live discovery: `search_gameobjects` / `find_objects_with_component` / `search_runtime_types`.
6. Inspect members/methods; keep a **stable selector**, not only instance IDs.

### Phase 2 — Execute

7. Prefer the lightest fix that works:
   - live value → `set_component_value`
   - action → `call_component_method`
   - future logic → `mod_patch_method`
   - multi-step → `run_bridge_batch`
   - reusable → profile save/apply
8. Verify (re-read member, take damage, watch, etc.).
9. Report what changed, temporary vs profile, networking/IL2CPP limits.
10. Clean up temp patches/watchers unless the user wants them kept.

### Unspawned objects

If research found the class but no live instance (boss not spawned), you can still **`mod_patch_method`** — patches attach to the type blueprint.

## Hard rules

1. Capabilities before patching — IL2CPP = `prefix`/`postfix` only.
2. Never Mono RAG ↔ IL2CPP decompiler swapped.
3. Lifecycle: patching `Awake`/`Start`/`OnEnable` does not rewrite already-running objects; use `set_component_value` for “now”.
4. RAG queries = full questions.
5. Prefer reversible mods; no inventing create/destroy GameObject tools.
6. Networked games: if values snap back, `get_network_diagnostics`.

## God mode pattern (example)

1. Capabilities → mono vs il2cpp.
2. Research damage/health (RAG or decompile).
3. Live: find player/health component; confirm method names.
4. Either:
   - temp: `set_component_value(..., invincible/hp, ...)`
   - durable: `mod_patch_method` prefix on `TakeDamage` returning `false` with exact signature.
5. Verify; optional `save_mod_profile`.

`DynamicPatcher` shape — see **`bepinex-mcp`** skill.

## Tool map (quick)

| Need | Server | Tool |
|------|--------|------|
| Mono project list | gamecode-rag | `list_available_projects` |
| Semantic code search | gamecode-rag | `code_search_and_rerank` |
| Call graph | gamecode-rag | `code_graph_search` |
| IL2CPP dumps | il2cpp-decompiler-agent | `list_dumps` / `load_project` |
| Binary symbols / decompile | il2cpp-decompiler-agent | `search_symbols`, `get_class_info`, `decompile_method` |
| Live scene / set / patch | bepinex-mcp | see **bepinex-mcp** skill |
