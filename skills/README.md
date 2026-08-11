# Skills for AI clients

These [Agent Skills](https://docs.cursor.com/context/skills) teach models how to use **bepinex-mcp** (and optional research MCPs) without the old mega system prompt.

| Skill | Use when |
|--------|----------|
| [`bepinex-mcp`](bepinex-mcp/SKILL.md) | Live bridge tools: search, set, patch, watch, profiles |
| [`unity-bepinex-modder`](unity-bepinex-modder/SKILL.md) | Full mod request: research → apply → verify (routes Mono vs IL2CPP) |

## Install (Cursor / Grok-style)

Copy a skill folder into your skills directory, e.g.:

```text
%USERPROFILE%\.cursor\skills\bepinex-mcp\
%USERPROFILE%\.cursor\skills\unity-bepinex-modder\
```

Or:

```powershell
$src = "C:\path\to\bepinex-mcp\skills"
$dst = "$env:USERPROFILE\.cursor\skills"
Copy-Item "$src\bepinex-mcp" "$dst\bepinex-mcp" -Recurse -Force
Copy-Item "$src\unity-bepinex-modder" "$dst\unity-bepinex-modder" -Recurse -Force
```

Pair with MCP servers:

| Runtime | Research MCP | Live MCP |
|---------|----------------|----------|
| Mono | `gamecode-rag` | `bepinex-mcp` |
| IL2CPP | `il2cpp-decompiler-agent` | `bepinex-mcp` |

Research skills for those servers live with those projects (not required for bridge-only use).
