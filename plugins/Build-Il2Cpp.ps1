[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$GameDir,

    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [switch]$Deploy
)

$ErrorActionPreference = "Stop"

$resolvedGameDir = (Resolve-Path -LiteralPath $GameDir).Path
$gameAssembly = Join-Path $resolvedGameDir "GameAssembly.dll"
$bepInExDir = Join-Path $resolvedGameDir "BepInEx"
$interopDir = Join-Path $bepInExDir "interop"

if (-not (Test-Path -LiteralPath $gameAssembly -PathType Leaf)) {
    throw "GameAssembly.dll was not found. '$resolvedGameDir' is not a Windows Unity IL2CPP game root."
}

$dataDirs = @(
    Get-ChildItem -LiteralPath $resolvedGameDir -Directory -Filter "*_Data" |
        Where-Object {
            Test-Path -LiteralPath (Join-Path $_.FullName "il2cpp_data") -PathType Container
        }
)

if ($dataDirs.Count -ne 1) {
    throw "Expected exactly one *_Data\il2cpp_data directory under '$resolvedGameDir'; found $($dataDirs.Count)."
}

if (-not (Test-Path -LiteralPath (Join-Path $interopDir "assembly-hash.txt") -PathType Leaf)) {
    throw "BepInEx interop assemblies are missing. Install BepInEx 6 IL2CPP and run the game once first."
}

function Test-ContainsNullableAttribute {
    param([string]$AssemblyPath)

    if (-not (Test-Path -LiteralPath $AssemblyPath -PathType Leaf)) {
        return $false
    }

    $bytes = [System.IO.File]::ReadAllBytes($AssemblyPath)
    $text = [System.Text.Encoding]::ASCII.GetString($bytes)
    return $text.Contains("NullableAttribute")
}

function New-PatchedInteropRefs {
    param(
        [string]$SourceInteropDir,
        [string]$DestinationDir,
        [string]$BepInExCoreDir
    )

    New-Item -ItemType Directory -Path $DestinationDir -Force | Out-Null

    $required = @(
        "Il2Cppmscorlib.dll",
        "Il2CppSystem.dll",
        "UnityEngine.CoreModule.dll"
    )

    foreach ($file in $required) {
        $src = Join-Path $SourceInteropDir $file
        if (-not (Test-Path -LiteralPath $src -PathType Leaf)) {
            throw "Required interop assembly missing: $src"
        }
        Copy-Item -LiteralPath $src -Destination (Join-Path $DestinationDir $file) -Force
    }

    foreach ($optional in @("UnityEngine.dll", "UnityEngine.SceneManagementModule.dll")) {
        $src = Join-Path $SourceInteropDir $optional
        if (Test-Path -LiteralPath $src -PathType Leaf) {
            Copy-Item -LiteralPath $src -Destination (Join-Path $DestinationDir $optional) -Force
        }
    }

    $stripperProject = Join-Path $PSScriptRoot "tools\StripNullableAttribute\StripNullableAttribute.csproj"
    $corePath = Join-Path $DestinationDir "UnityEngine.CoreModule.dll"
    & dotnet run --project $stripperProject -c Release -- $corePath $BepInExCoreDir
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to strip broken NullableAttribute from UnityEngine.CoreModule.dll"
    }

    if (Test-ContainsNullableAttribute -AssemblyPath $corePath) {
        throw "NullableAttribute was still present after patching '$corePath'."
    }

    Write-Output "Patched broken NullableAttribute out of build-time UnityEngine.CoreModule.dll"
}

$buildInteropDir = $interopDir
$coreModule = Join-Path $interopDir "UnityEngine.CoreModule.dll"
if (Test-ContainsNullableAttribute -AssemblyPath $coreModule) {
    $buildInteropDir = Join-Path $PSScriptRoot "BepInExMCP.IL2CPP\build-refs"
    New-PatchedInteropRefs `
        -SourceInteropDir $interopDir `
        -DestinationDir $buildInteropDir `
        -BepInExCoreDir (Join-Path $bepInExDir "core")
}

$project = Join-Path $PSScriptRoot "BepInExMCP.IL2CPP\BepInExMCP.IL2CPP.csproj"
& dotnet build $project `
    --configuration $Configuration `
    "-p:Il2CppInteropDir=$buildInteropDir"

if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}

$outputDir = Join-Path $PSScriptRoot "BepInExMCP.IL2CPP\bin\$Configuration\net6.0"
Write-Output "Built IL2CPP bridge: $outputDir"

if (-not $Deploy) {
    return
}

$destination = Join-Path $bepInExDir "plugins\BepInExMCP.IL2CPP"
New-Item -ItemType Directory -Path $destination -Force | Out-Null

$requiredFiles = @(
    "BepInExMCP.IL2CPP.dll",
    "BepInExMCP.IL2CPP.deps.json",
    "Microsoft.CodeAnalysis.dll",
    "Microsoft.CodeAnalysis.CSharp.dll"
)

foreach ($file in $requiredFiles) {
    $source = Join-Path $outputDir $file
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Required deployment file is missing: $source"
    }

    Copy-Item -LiteralPath $source -Destination (Join-Path $destination $file) -Force
}

$pdb = Join-Path $outputDir "BepInExMCP.IL2CPP.pdb"
if (Test-Path -LiteralPath $pdb -PathType Leaf) {
    Copy-Item -LiteralPath $pdb -Destination $destination -Force
}

Write-Output "Deployed IL2CPP bridge: $destination"
