# Start bepinex-mcp MCP + Cloudflare tunnel for Gemini Spark.
# Detached processes (no stdout pipes) so they survive parent exit.
# Usage:
#   powershell -ExecutionPolicy Bypass -File .\start-gemini-spark.ps1

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Port = 8765
$Python = if (Test-Path "C:\Python313\python.exe") { "C:\Python313\python.exe" } else { "python" }
$Script = Join-Path $Root "ModdersHelperApp.py"
$Cloudflared = Join-Path $Root "tools\cloudflared.exe"
$UrlFile = Join-Path $Root "gemini-spark-url.txt"
$CfLog = Join-Path $Root "gemini-cf.err.log"
$McpLog = Join-Path $Root "gemini-mcp.err.log"
$PidFile = Join-Path $Root "gemini-spark.pids"

Write-Host ""
Write-Host "=== Unity Mod Helper -> Gemini Spark ===" -ForegroundColor Cyan

if (-not (Test-Path $Cloudflared)) {
    Write-Host "Downloading cloudflared..." -ForegroundColor Yellow
    New-Item -ItemType Directory -Force -Path (Split-Path $Cloudflared) | Out-Null
    Invoke-WebRequest -Uri "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe" `
        -OutFile $Cloudflared -UseBasicParsing
}

# Stop previous instance if pid file exists
if (Test-Path $PidFile) {
    Get-Content $PidFile | ForEach-Object {
        $pidVal = 0
        if ([int]::TryParse($_.Trim(), [ref]$pidVal)) {
            Stop-Process -Id $pidVal -Force -ErrorAction SilentlyContinue
        }
    }
}
Get-NetTCPConnection -LocalPort $Port -ErrorAction SilentlyContinue |
    Where-Object { $_.State -eq "Listen" } |
    ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
Get-Process cloudflared -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

Remove-Item $UrlFile, $CfLog, $McpLog, $PidFile -ErrorAction SilentlyContinue

# Launch MCP in its own console (no redirected pipes → survives parent death)
$mcpArgs = @(
    "-NoExit",
    "-Command",
    "& '$Python' '$Script' --headless --transport streamable-http --host 127.0.0.1 --port $Port --game-ip localhost 2>&1 | Tee-Object -FilePath '$McpLog'"
)
$mcp = Start-Process -FilePath "powershell.exe" -ArgumentList $mcpArgs -WorkingDirectory $Root -PassThru -WindowStyle Minimized
Write-Host "MCP console pid=$($mcp.Id)"

$ready = $false
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Milliseconds 250
    try {
        $c = New-Object System.Net.Sockets.TcpClient
        $c.Connect("127.0.0.1", $Port)
        $c.Close()
        $ready = $true
        break
    } catch {}
}
if (-not $ready) {
    Write-Host "MCP did not open port $Port. Check $McpLog" -ForegroundColor Red
    if (Test-Path $McpLog) { Get-Content $McpLog -Tail 40 }
    exit 1
}
Write-Host "MCP listening on :$Port" -ForegroundColor Green

# Launch cloudflared in its own console
$cfArgs = @(
    "-NoExit",
    "-Command",
    "& '$Cloudflared' tunnel --url http://127.0.0.1:$Port 2>&1 | Tee-Object -FilePath '$CfLog'"
)
$cf = Start-Process -FilePath "powershell.exe" -ArgumentList $cfArgs -WorkingDirectory $Root -PassThru -WindowStyle Minimized
Write-Host "cloudflared console pid=$($cf.Id)"

$publicUrl = $null
for ($i = 0; $i -lt 60; $i++) {
    Start-Sleep -Seconds 1
    if (Test-Path $CfLog) {
        $text = Get-Content $CfLog -Raw -ErrorAction SilentlyContinue
        if ($text -match "https://[a-zA-Z0-9-]+\.trycloudflare\.com") {
            $publicUrl = $Matches[0]
            break
        }
    }
}

if (-not $publicUrl) {
    Write-Host "Could not parse Cloudflare URL. Check $CfLog" -ForegroundColor Red
    if (Test-Path $CfLog) { Get-Content $CfLog -Tail 40 }
    exit 1
}

$mcpUrl = "$publicUrl/mcp"
Set-Content -Path $UrlFile -Value $mcpUrl -Encoding utf8
Set-Content -Path $PidFile -Value @($mcp.Id, $cf.Id) -Encoding utf8

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host " Gemini Spark MCP URL - paste this:" -ForegroundColor Green
Write-Host " $mcpUrl" -ForegroundColor White
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Saved to: $UrlFile"
Write-Host ""
Write-Host "Connect:"
Write-Host "  1. https://gemini.google.com (personal account + Spark)"
Write-Host "  2. Settings -> Connected Apps -> Custom apps for Spark"
Write-Host "  3. Add a custom app -> paste URL -> Next"
Write-Host "  4. In chat: @ the app and ask about the Unity game"
Write-Host ""
Write-Host "Two minimized PowerShell windows are running (MCP + tunnel)."
Write-Host "Close those windows to stop. Keep Activity ON in Gemini."
Write-Host "Game bridge must be on localhost:8080 for tools to work."
Write-Host ""
Write-Host "Press Enter to close this launcher (MCP/tunnel keep running)..."
[void][Console]::ReadLine()
