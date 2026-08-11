# Skills for AI clients

These skills teach models how to use **this repo’s** MCP server (`bepinex-mcp`).

| Skill | Requires | Use when |
|--------|----------|----------|
| [`bepinex-mcp`](bepinex-mcp/SKILL.md) | **Only this project** (plugin + Python MCP) | Live bridge: search, set, patch, watch, profiles |
| [`unity-bepinex-modder`](unity-bepinex-modder/SKILL.md) | This project **+ optional** research MCPs | Bigger mod requests; research is optional |

## What ships in this repository

You can use **everything in the main README** with:

1. A BepInEx plugin from `plugins/` in a Unity game  
2. `mcp/ModdersHelperApp.py` as the MCP server  

That is enough for live modding (discover objects, change values, Harmony-style patches, profiles).

## What does *not* ship here

| External MCP | Purpose | Needed for bridge? |
|--------------|---------|---------------------|
| `gamecode-rag` | Semantic search over **dumped Mono** C# | No |
| `il2cpp-decompiler-agent` | Static decompile of **IL2CPP** binaries | No |

Those are **separate** tools. The orchestrator skill mentions them only as an optional “research then apply” path. If they are not installed, AIs should still work: **live discovery + bridge tools only**.

## Install (Cursor / Grok-style)

```powershell
$src = "C:\path\to\bepinex-mcp\skills"
$dst = "$env:USERPROFILE\.cursor\skills"
Copy-Item "$src\bepinex-mcp" "$dst\bepinex-mcp" -Recurse -Force
Copy-Item "$src\unity-bepinex-modder" "$dst\unity-bepinex-modder" -Recurse -Force
```

Wire the MCP server as in the [root README](../README.md).
