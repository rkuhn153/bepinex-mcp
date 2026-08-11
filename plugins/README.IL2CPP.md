# BepInExMCP IL2CPP bridge

This repository now contains two in-game bridge assemblies:

- `BepInExMCP.csproj`: BepInEx 5 / Unity Mono (`BaseUnityPlugin`, `netstandard2.0`).
- `BepInExMCP.IL2CPP/BepInExMCP.IL2CPP.csproj`: BepInEx 6 / Unity IL2CPP
  (`BasePlugin`, `net6.0`, Il2CppInterop).

They intentionally remain separate binaries. Mono and IL2CPP use different loaders,
base classes, target frameworks, object models, and Harmony backends. Both implement
the same HTTP API so the existing `mcp/ModdersHelperApp.py` process
can talk to either runtime.

## Supported baseline

The IL2CPP project pins:

- BepInEx Unity IL2CPP `6.0.0-be.785` (`6abdba4`)
- .NET 6
- Microsoft.CodeAnalysis.CSharp `4.3.1`

Build 785 is a bleeding-edge BepInEx build; BepInEx does not currently publish a
stable IL2CPP release. The plugin uses only common Unity core APIs and does not
compile against `Assembly-CSharp`, but runtime support is still bounded by
BepInEx, Cpp2IL, the game's metadata, stripping, and obfuscation.

Validated:

- Windows x64
- Unity `2022.3.62f3`
- BepInEx `6.0.0-be.785`
- Bang Bang Barrage

Read-only API-surface checked, not runtime-validated:

- Unity `2020.3.16f1`
- Ship of Fools (its existing MelonLoader installation was not modified)

No compatibility claim is made for untested Unity versions, x86, Linux, macOS,
encrypted metadata, anti-cheat games, or games that BepInEx itself cannot load.

## Install and build

1. Install the matching official BepInEx 6 IL2CPP distribution into the game root.
   For the validated environment this is:
   `BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.785+6abdba4.zip`.
2. Run the game once. Wait for `BepInEx/interop/assembly-hash.txt` and confirm
   `BepInEx/LogOutput.log` reports successful interop generation.
3. Build and optionally deploy:

```powershell
.\Build-Il2Cpp.ps1 `
  -GameDir "D:\SteamLibrary\steamapps\common\Bang Bang Barrage" `
  -Configuration Release `
  -Deploy
```

The script validates the IL2CPP layout, passes the generated `BepInEx/interop`
directory to MSBuild, and deploys the plugin and its two Roslyn assemblies under:

```text
BepInEx/plugins/BepInExMCP.IL2CPP/
```

The project can also be built directly:

```powershell
dotnet build .\BepInExMCP.IL2CPP\BepInExMCP.IL2CPP.csproj `
  -c Release `
  -p:Il2CppInteropDir="D:\path\to\game\BepInEx\interop"
```

Do not copy one game's `Assembly-CSharp.dll` into this project. Runtime type
discovery uses the interop assemblies generated for the game being run.

## Translator

The Python translator remains the MCP server:

```powershell
python ../mcp/ModdersHelperApp.py --game-ip localhost
```

For a server without the Tk window:

```powershell
python ../mcp/ModdersHelperApp.py --game-ip localhost --headless
```

The tested dependency versions are pinned in `mcp/requirements.txt`.

## HTTP contract

Both plugins listen under `http://<ListenIP>:<ListenPort>/mcp/`. Defaults are
`localhost:8080`. The Python webhook listens on port 8081.

Protocol version is `2.0`. All protocol-1.1 GET routes remain supported.

### Baseline commands

- `system/capabilities`
- `scene/list_root_gameobjects`
- `gameobject/list_children`
- `gameobject/inspect_components`
- `component/get_details`
- `component/set_value`
- `component/call_method`
- `component/list_methods`
- `scene:find_objects_with_component`
- `mod:subscribe_to_method`
- `mod:patch_method`

### Protocol 2.0 productivity commands

- `system/ping`
- `scene/search_gameobjects`
- `scene/hierarchy_snapshot`
- `scene/resolve_selector`
- `component/get_member`
- `type/search`
- `type/describe`
- `network/diagnostics`
- `watch/member`, `watch/scene`, `watch/list`, `watch/remove`
- `mod:list_patches`, `mod:remove_patch`
- `POST /mcp/batch` with ordered operations, `stopOnError`, and bounded body/op counts

Stable selectors use scene + hierarchy path with sibling indices, plus optional
component/name fallbacks. Instance IDs are session-local only.

`system/capabilities` reports protocol version, runtime, Unity/BepInEx versions,
architecture, tools, features, limits, patch types, limitations, and (when
enabled) the IL2CPP diagnostic object's instance ID.

The IL2CPP implementation resolves the concrete native type of each component,
maps it to the generated managed proxy, and casts the wrapper before reflection.
This avoids treating every object returned by `GetComponents<Component>()` as
the base `UnityEngine.Component` proxy.

### Translator profiles

Persistent profiles live in the Python translator under
`%LOCALAPPDATA%/unity-mcp-translator/profiles` (override with `--profiles-dir`
or `UNITY_MCP_PROFILES_DIR`). MCP tools cover save/get/list/apply/delete/activate.
Only profiles marked `autoApply` and activated reapply after scene-change or
target-loss events.

## IL2CPP patching

Il2CppInterop Harmony support permits managed prefixes and postfixes around many
generated interop methods. The IL2CPP bridge supports:

- method-event subscriptions through a managed postfix
- dynamic `prefix` patches
- dynamic `postfix` patches

It rejects `transpiler` and `finalizer`. An IL2CPP transpiler would modify the
generated managed wrapper, not the native game method body, so it is not
equivalent to the Mono feature.

Dynamic source must contain:

```csharp
public static class DynamicPatcher
{
    public static void Prefix()
    {
    }
}
```

Use `Postfix` instead when `patchType=postfix`. Compiler references are assembled
at runtime from the .NET trusted platform assemblies, `BepInEx/core`, loaded
assemblies, and `BepInEx/interop`.

Methods removed by stripping, field accessors without native pointers, ambiguous
overloads, unsupported native signatures, and obfuscated names return explicit
errors rather than being guessed.

## Security boundary

The bridge performs reflection, mutation, method calls, and dynamic code
compilation. It is deliberately bound to `localhost` by default and has no TLS or
authentication. Do not set `ListenIP=*` on an untrusted network.
