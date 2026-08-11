# IL2CPP plugin build

BepInEx **6** / Unity IL2CPP bridge (`BepInExMCP.IL2CPP/`). Same HTTP API as the Mono plugin; separate binary because the loader and type system differ.

Pinned for this project: **BepInEx Unity IL2CPP `6.0.0-be.785`**, **.NET 6**.

## Build requirements

Unlike Mono, this project **does not build from NuGet alone**. MSBuild needs interop assemblies from a game that already has BepInEx 6 IL2CPP installed and has been launched once:

| Need | Where |
|------|--------|
| Game root | e.g. Steam `...\common\YourGame` |
| `GameAssembly.dll` | game root |
| `BepInEx/interop/` | generated after first run (`assembly-hash.txt` present) |

You do **not** add the game’s `Assembly-CSharp` (or equivalent) to this repo. Types are resolved at runtime from that game’s interop.

## Steps

1. Install [BepInEx 6 Unity IL2CPP](https://github.com/BepInEx/BepInEx) into the game root (zip matching win-x64 + your BepInEx build, ideally `6.0.0-be.785`).
2. Run the game once until `BepInEx/interop/assembly-hash.txt` exists and the log looks healthy.
3. From `plugins/`:

```powershell
.\Build-Il2Cpp.ps1 -GameDir "D:\path\to\game" -Configuration Release -Deploy
```

That passes `BepInEx/interop` into the build and, with `-Deploy`, copies the plugin (and Roslyn deps) to:

```text
BepInEx/plugins/BepInExMCP.IL2CPP/
```

Without the script:

```powershell
dotnet build .\BepInExMCP.IL2CPP\BepInExMCP.IL2CPP.csproj -c Release `
  -p:Il2CppInteropDir="D:\path\to\game\BepInEx\interop"
```

## After install

Start the game with the plugin loaded, then the MCP server from the repo root docs (`mcp/ModdersHelperApp.py`). Bridge default: `http://localhost:8080/mcp`.

## IL2CPP-only notes

- Dynamic patches: **`prefix` / `postfix` only** (no transpiler/finalizer — those don’t mean the same thing as on Mono).
- Stripped/obfuscated methods and odd native signatures fail with explicit errors; don’t expect every Mono workflow to work.
- Listen address defaults to localhost; don’t bind `*` on an untrusted network.
