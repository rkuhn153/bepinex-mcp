# bepinex-mcp

Live **Unity + BepInEx** bridge for AI agents and tools via **MCP (Model Context Protocol)**.

| Piece | Path | Role |
|--------|------|------|
| Mono plugin | `plugins/` (`BepInExMCP`) | BepInEx 5 / Unity Mono HTTP bridge |
| IL2CPP plugin | `plugins/BepInExMCP.IL2CPP/` | BepInEx 6 / Unity IL2CPP HTTP bridge |
| MCP server | `mcp/ModdersHelperApp.py` | FastMCP stdio (or GUI) client → bridge |

Both plugins expose the same HTTP API. The Python MCP talks to either runtime.

MCP server id: **`bepinex-mcp`**.

## Requirements

- Python 3.11+ (3.13 tested)
- A Unity game with **BepInEx 5 (Mono)** or **BepInEx 6 IL2CPP** installed
- .NET SDK for building plugins (see `plugins/README.IL2CPP.md` for IL2CPP)

## Quick start

### 1. Build & install a plugin

**Mono (BepInEx 5):**

```powershell
cd plugins
dotnet build -c Release
# Copy bin/Release/netstandard2.0/BepInExMCP.dll → <Game>/BepInEx/plugins/
```

**IL2CPP (BepInEx 6):** see [`plugins/README.IL2CPP.md`](plugins/README.IL2CPP.md).

### 2. Run the MCP server

```powershell
cd mcp
pip install -r requirements.txt
python ModdersHelperApp.py --transport=stdio --headless
```

Optional GUI (default without `--headless`):

```powershell
python ModdersHelperApp.py
```

Bridge URL defaults to `http://localhost:8080/mcp` (plugin side). Override with flags in `ModdersHelperApp.py --help`.

### 3. Wire MCP clients

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

## Profiles

Named patch/profile JSON is stored under:

`%LOCALAPPDATA%/unity-mcp-translator/profiles`

(Override with `--profiles-dir`.) The directory name is historical; behavior is unchanged.

## Layout

```
bepinex-mcp/
  plugins/                 # BepInEx plugins (Mono + IL2CPP)
  mcp/                     # FastMCP Python server
  README.md
  LICENSE
  .gitignore
```

## Status

Experimental research tooling. APIs may change. Tested primarily on Windows x64.

## License

MIT — see [LICENSE](LICENSE).
