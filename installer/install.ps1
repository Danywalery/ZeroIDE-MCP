# zeroide-mcp installer (Windows, per-user, no admin required).
#
# What it does:
#   1. Copies server + tray into %LOCALAPPDATA%\ZeroIDE-MCP
#   2. Downloads a pinned portable Node.js into ZeroIDE-MCP\runtime (if absent)
#   3. Uses bundled server dependencies (npm ci fallback for incomplete dependencies)
#   4. Generates config.env with a fresh MCP token (never printed)
#   5. Generates an SSH keypair only when SshTarget is explicitly supplied
#   6. Creates the tray-config.json and a Start Menu shortcut
# It does NOT enable autostart and does NOT start anything.
# -Update replaces the app, server and helpers of an existing installation and keeps
# config.env, tray-config.json, tokens, connections and logs. The app must be closed.
#
# Run from the unpacked package root:  powershell -ExecutionPolicy Bypass -File installer\install.ps1
# The setup wizard (ZeroIDE-MCP.exe started from the unpacked package) runs this same script.

[CmdletBinding()]
param(
    [string]$ProjectDir = "",
    [int]$Port = 39011,
    [int]$RemotePort = 39003,
    [string]$SshTarget = "",
    [switch]$Update
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $env:LOCALAPPDATA "ZeroIDE-MCP"
$nodeVersion = "v24.22.2"
# SHA-256 of node-v24.22.2-win-x64.zip as published in https://nodejs.org/dist/v24.22.2/SHASUMS256.txt
# (checked 2026-11-10). The download is verified before it is unpacked; update both lines together.
$nodeSha256 = "ca1bff5d8ead13c9f2ea404a176db46bd430704d5b5113734049fa365e7799e2"
$nodeUrl = "https://nodejs.org/dist/$nodeVersion/node-$nodeVersion-win-x64.zip"

function Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }

if ($Update) {
    # In-place update: only the shipped program files change; machine-local state stays.
    if (-not (Test-Path $dest)) { throw "Nothing to update: $dest does not exist. Run without -Update for a fresh installation." }
    $running = Get-Process -Name 'ZeroIDE-MCP' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($dest, [StringComparison]::OrdinalIgnoreCase) }
    if ($running) { throw "ZeroIDE MCP is running. Exit it from the tray icon, then run the update again." }
} else {
    # This is a fresh-install entry point, not an in-place updater (use -Update for that).
    # tray-config.json is written last, so a folder without it is an interrupted installation: finish it instead of refusing.
    if (Test-Path (Join-Path $dest "tray-config.json")) { throw "ZeroIDE-MCP already exists. Stop and ask the owner whether to update (-Update), restore or delete it." }
    if (Test-Path $dest) { Write-Host "An unfinished installation was found in $dest (no tray-config.json); completing it." -ForegroundColor Yellow }
    # Builds made before the rename installed under the working name AgentPower and use the same ports.
    $legacyDest = Join-Path $env:LOCALAPPDATA "AgentPower"
    if (Test-Path $legacyDest) { throw "An earlier installation exists in $legacyDest (old name AgentPower). Stop and ask the owner whether to keep, migrate or delete it; do not run both at once." }
}
# The release zip ships the app at the package root; a source tree builds it into tray\build.
$trayExe = @((Join-Path $root 'ZeroIDE-MCP.exe'), (Join-Path $root 'tray\build\ZeroIDE-MCP.exe')) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $trayExe) { throw "Incomplete release package: ZeroIDE-MCP.exe. Download zeroide-mcp.zip from Releases." }
foreach ($required in @('server\server.mjs','tools\Configure-Connection.ps1','tools\Onboarding.Common.ps1','tools\Onboarding.Ngrok.ps1','tools\Test-LocalMcp.ps1','tools\Setup-Ngrok.ps1','tools\Onboarding.Tailscale.ps1','tools\Setup-Tailscale.ps1','tools\Onboarding.Cloudflare.ps1','tools\Setup-Cloudflare.ps1','docs\AGENT_CONNECTIONS.md')) {
    if (-not (Test-Path (Join-Path $root $required))) { throw "Incomplete release package: $required. Download zeroide-mcp.zip from Releases." }
}
if ($Update) { Step "Updating $dest" } else { Step "Installing into $dest" }
New-Item -ItemType Directory -Force -Path $dest | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $dest "logs") | Out-Null

Step "Copying server"
# Contents are merged into an existing server folder; the package never contains config.env.
New-Item -ItemType Directory -Force -Path (Join-Path $dest "server") | Out-Null
Copy-Item -Recurse -Force (Join-Path $root "server\*") (Join-Path $dest "server")

Step "Copying tray"
Copy-Item -Force $trayExe (Join-Path $dest "ZeroIDE-MCP.exe")

Step "Copying agent connection helper and instructions"
New-Item -ItemType Directory -Force -Path (Join-Path $dest 'tools'), (Join-Path $dest 'docs') | Out-Null
foreach ($helper in @('Configure-Connection.ps1','Onboarding.Common.ps1','Onboarding.Ngrok.ps1','Test-LocalMcp.ps1','Setup-Ngrok.ps1','Onboarding.Tailscale.ps1','Setup-Tailscale.ps1','Onboarding.Cloudflare.ps1','Setup-Cloudflare.ps1')) {
    Copy-Item -Force (Join-Path $root ('tools\'+$helper)) (Join-Path $dest 'tools')
}
Copy-Item -Force (Join-Path $root 'docs\AGENT_CONNECTIONS.md') (Join-Path $dest 'docs')
Copy-Item -Force (Join-Path $root 'AGENT_INSTALL.md') $dest
Copy-Item -Force (Join-Path $root 'agent-manifest.json') $dest

Step "Portable Node.js $nodeVersion"
$runtimeDir = Join-Path $dest "runtime"
$nodeExe = Join-Path $runtimeDir "node.exe"
if (-not (Test-Path $nodeExe)) {
    $zip = Join-Path $env:TEMP "node-win.zip"
    Invoke-WebRequest -UseBasicParsing -Uri $nodeUrl -OutFile $zip
    $nodeActualSha256 = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($nodeActualSha256 -ne $nodeSha256) {
        # .NET calls instead of Remove-Item: Remove-Item fails on 8.3 short TEMP paths such as
        # C:\Users\ABCD~1\AppData\Local\Temp (seen on a machine with a non-ASCII profile name).
        try { [System.IO.File]::Delete($zip) } catch { }
        throw "Node.js download failed SHA-256 verification (expected $nodeSha256, got $nodeActualSha256). Nothing from it was unpacked. Delete $dest and run the installer again; if it repeats, check the network or proxy."
    }
    $extract = Join-Path $env:TEMP "node-win-extract"
    if ([System.IO.Directory]::Exists($extract)) { [System.IO.Directory]::Delete($extract, $true) }
    Expand-Archive -LiteralPath $zip -DestinationPath $extract
    New-Item -ItemType Directory -Force -Path $runtimeDir | Out-Null
    Copy-Item -Force (Join-Path $extract "node-$nodeVersion-win-x64\node.exe") $nodeExe
    Copy-Item -Force (Join-Path $extract "node-$nodeVersion-win-x64\npm.cmd") $runtimeDir -ErrorAction SilentlyContinue
    Copy-Item -Recurse -Force (Join-Path $extract "node-$nodeVersion-win-x64\node_modules") $runtimeDir
    try { [System.IO.File]::Delete($zip); [System.IO.Directory]::Delete($extract, $true) } catch { }
} else {
    Write-Host "node.exe already present, skipping download."
}

Step "Server dependencies"
if (Test-Path (Join-Path $dest 'server\node_modules\@modelcontextprotocol\sdk\package.json')) {
    Write-Host 'Using dependencies bundled in the release.'
} else {
    Push-Location (Join-Path $dest 'server')
    try {
        & $nodeExe (Join-Path $runtimeDir 'node_modules\npm\bin\npm-cli.js') ci --omit=dev --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed with exit code $LASTEXITCODE" }
    } finally { Pop-Location }
}

Step "Generating config.env (token & control secret)"
$configPath = Join-Path $dest "server\config.env"
$ctrlBytes = New-Object byte[] 24
[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($ctrlBytes)
$controlSecret = -join ($ctrlBytes | ForEach-Object { $_.ToString("x2") })

if (Test-Path $configPath) {
    Write-Warning "config.env already exists -- keeping the existing token."
    $tokenLine = Get-Content $configPath | Where-Object { $_ -match "^MCP_TOKEN=" } | Select-Object -First 1
    $token = $tokenLine -replace "^MCP_TOKEN=", ""
    $ctrlLine = Get-Content $configPath | Where-Object { $_ -match "^TRAY_CONTROL_SECRET=" } | Select-Object -First 1
    if ($ctrlLine) {
        $controlSecret = $ctrlLine -replace "^TRAY_CONTROL_SECRET=", ""
    } else {
        Add-Content -Path $configPath -Value "TRAY_CONTROL_SECRET=$controlSecret" -Encoding ASCII
        Add-Content -Path $configPath -Value "TRAY_CONTROL_PORT=39012" -Encoding ASCII
    }
} else {
    $bytes = New-Object byte[] 32
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    $token = -join ($bytes | ForEach-Object { $_.ToString("x2") })
    @(
        "MCP_TOKEN=$token",
        "PORT=$Port",
        "MCP_HOST=127.0.0.1",
        "PROJECT_DIR=$ProjectDir",
        "MCP_LOG_DIR=$dest\logs",
        "TRAY_CONTROL_PORT=39012",
        "TRAY_CONTROL_SECRET=$controlSecret"
    ) | Set-Content -Path $configPath -Encoding ASCII
    Write-Host ""
    Write-Host "  MCP token created and saved locally. It is never printed in installer output." -ForegroundColor Yellow
    Write-Host "  Use the tray's protected access flow to connect your AI client." -ForegroundColor Yellow
    Write-Host ""
}

$keyPath = Join-Path $dest "tunnel_key"
if ($SshTarget -ne '') {
    Step "SSH keypair for the requested reverse tunnel"
    if (-not (Get-Command ssh-keygen -ErrorAction SilentlyContinue)) { throw 'ssh-keygen is required only for the requested SSH connection.' }
    & ssh-keygen -t ed25519 -f $keyPath -N '""' -C "zeroide-mcp-tunnel" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'SSH key generation failed' }
    Write-Host ('TUNNEL PUBLIC KEY: ' + (Get-Content "$keyPath.pub"))
}

if ($Update -and (Test-Path (Join-Path $dest "tray-config.json"))) {
    Step "tray-config.json kept"
} else {
Step "tray-config.json"
$trayCfg = @{
    ServerDir            = (Join-Path $dest "server")
    NodeExe              = $nodeExe
    Port                 = $Port
    ControlPort          = 39012
    ControlSecret        = $controlSecret
    ProjectDir           = $ProjectDir
    LogDir               = (Join-Path $dest "logs")
    TunnelEnabled        = ($SshTarget -ne "")
    SshTarget            = $SshTarget
    SshKeyPath           = $keyPath
    RemotePort           = $RemotePort
    IdleMinutes          = 0
    NotificationsEnabled = $true
    DefaultGrantMinutes  = 10
    DefaultGrantScope    = "tool"
} | ConvertTo-Json
$trayCfg | Set-Content -Path (Join-Path $dest "tray-config.json") -Encoding UTF8
}

Step "Start Menu shortcut"
$shortcutDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$wsh = New-Object -ComObject WScript.Shell
$sc = $wsh.CreateShortcut((Join-Path $shortcutDir "ZeroIDE MCP.lnk"))
$sc.TargetPath = Join-Path $dest "ZeroIDE-MCP.exe"
$sc.WorkingDirectory = $dest
$sc.Save()

Step "Done"
if ($Update) { Write-Host "Updated $dest" } else { Write-Host "Installed to $dest" }
Write-Host "Autostart is OFF. Start 'ZeroIDE MCP' from the Start Menu when you want the agent online."
Write-Host "Next: follow AGENT_INSTALL.md, then docs\AGENT_CONNECTIONS.md."
