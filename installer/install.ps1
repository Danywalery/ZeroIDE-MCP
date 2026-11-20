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
# -MigrateFrom <folder> turns an installation made under the old name (AgentPower) into this one:
# its settings, tokens, connections, tunnel clients and logs move over, the old folder is removed.
#
# Run from the unpacked package root:  powershell -ExecutionPolicy Bypass -File installer\install.ps1
# The setup wizard (ZeroIDE-MCP.exe started from the unpacked package) runs this same script.

[CmdletBinding()]
param(
    [string]$ProjectDir = "",
    [int]$Port = 39011,
    [int]$RemotePort = 39003,
    [string]$SshTarget = "",
    [switch]$Update,
    [string]$MigrateFrom = "",
    [string]$InstallDir = ""
)

$ErrorActionPreference = "Stop"
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$root = Split-Path -Parent $PSScriptRoot
$defaultDest = Join-Path $env:LOCALAPPDATA "ZeroIDE-MCP"
$dest = if ($InstallDir) { [System.IO.Path]::GetFullPath($InstallDir).TrimEnd('\') } else { $defaultDest }
$isCustomInstallDir = -not [string]::Equals($dest, $defaultDest.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)
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
    if ((Test-Path $legacyDest) -and -not $MigrateFrom) { throw "An earlier installation exists in $legacyDest (old name AgentPower). Stop and ask the owner whether to keep, migrate (-MigrateFrom $legacyDest) or delete it; do not run both at once." }
}
if ($MigrateFrom) {
    # Carry the machine-local state of an earlier installation (old name AgentPower) into $dest and
    # continue as an update: program files come from this package, settings/tokens/connections move
    # over, paths inside them are rewritten, and the old folder is removed at the end.
    $MigrateFrom = (Resolve-Path $MigrateFrom).Path.TrimEnd('\')
    if (-not (Test-Path (Join-Path $MigrateFrom 'tray-config.json'))) { throw "Nothing to migrate: $MigrateFrom has no tray-config.json." }
    if ([string]::Equals($MigrateFrom, $dest, [StringComparison]::OrdinalIgnoreCase)) { throw "Migration source and destination are the same folder; use -Update." }
    $old = Get-Process -Name 'AgentPowerTray','ZeroIDE-MCP' -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($MigrateFrom, [StringComparison]::OrdinalIgnoreCase) }
    if ($old) { throw "The earlier app is still running from $MigrateFrom. Exit it from the tray icon, then run the migration again." }
    # Parse the old settings before anything moves, so a broken file stops the migration while the old folder is still whole.
    $cfg = Get-Content -Raw -LiteralPath (Join-Path $MigrateFrom 'tray-config.json') | ConvertFrom-Json
    Step "Moving settings, tokens and connections from $MigrateFrom"
    New-Item -ItemType Directory -Force -Path $dest, (Join-Path $dest 'server'), (Join-Path $dest 'tools') | Out-Null
    foreach ($rel in @('tray-config.json','tokens.json','oauth.json','permissions.json','telegram.json','connections-v2.json','connections-v2.json.previous','connection-client.json','tunnel_key','tunnel_key.pub','server\config.env')) {
        $src = Join-Path $MigrateFrom $rel
        if (-not (Test-Path $src)) { continue }
        # .NET moves: PowerShell's Move-Item rejects 8.3 short paths such as C:\Users\ABCD~1\AppData\Local.
        $dst = Join-Path $dest $rel
        if ([System.IO.File]::Exists($dst)) { [System.IO.File]::Delete($dst) }
        [System.IO.File]::Move($src, $dst); Write-Host "  $rel"
    }
    # Folders with user data and downloaded tunnel clients (ngrok.yml holds the authtoken). The old
    # Node runtime stays behind: this installer provides its own pinned version.
    # .NET moves throughout: Move-Item/Copy-Item -Path misread 8.3 paths with "~" and -LiteralPath takes no wildcards.
    function Merge-Move([string]$from, [string]$to) {
        if (-not [System.IO.Directory]::Exists($to)) { [System.IO.Directory]::Move($from, $to); return }
        foreach ($entry in [System.IO.Directory]::GetFileSystemEntries($from)) {
            $target = Join-Path $to ([System.IO.Path]::GetFileName($entry))
            if ([System.IO.Directory]::Exists($entry)) { Merge-Move $entry $target }
            else { if ([System.IO.File]::Exists($target)) { [System.IO.File]::Delete($target) }; [System.IO.File]::Move($entry, $target) }
        }
        [System.IO.Directory]::Delete($from, $true)
    }
    foreach ($rel in @('logs','screenshots','backups','tools\ngrok','tools\cloudflare','tools\cloudflared','tools\tailscale')) {
        $src = Join-Path $MigrateFrom $rel
        if (-not (Test-Path $src)) { continue }
        Merge-Move $src (Join-Path $dest $rel)
        Write-Host "  $rel\"
    }
    # Paths inside the moved files pointed into the old folder: rebase them onto $dest (prefix match, any casing).
    $oldPrefixes = @($MigrateFrom, (Join-Path $env:LOCALAPPDATA 'AgentPower')) | Select-Object -Unique
    function Rebase([string]$value) {
        if (-not $value) { return $value }
        foreach ($o in $oldPrefixes) { if ($value.StartsWith($o, [StringComparison]::OrdinalIgnoreCase)) { return $dest + $value.Substring($o.Length) } }
        return $value
    }
    $cfgPath = Join-Path $dest 'tray-config.json'
    foreach ($prop in @($cfg.PSObject.Properties)) { if ($prop.Value -is [string]) { $cfg.($prop.Name) = Rebase $prop.Value } }
    function Set-Prop($obj, [string]$name, $value) { if ($obj.PSObject.Properties[$name]) { $obj.$name = $value } else { $obj | Add-Member -NotePropertyName $name -NotePropertyValue $value } }
    Set-Prop $cfg 'ServerDir' (Join-Path $dest 'server')
    Set-Prop $cfg 'NodeExe' (Join-Path $dest 'runtime\node.exe')
    Set-Prop $cfg 'LogDir' (Join-Path $dest 'logs')
    Set-Prop $cfg 'SetupCompleted' $true   # a migrated installation is already set up: no first-run connect wizard
    [System.IO.File]::WriteAllText($cfgPath, ($cfg | ConvertTo-Json), (New-Object System.Text.UTF8Encoding $false))
    $envPath = Join-Path $dest 'server\config.env'
    if (Test-Path $envPath) {
        # The log dir always follows the new folder; a PROJECT_DIR mangled by an ASCII write of an
        # earlier installer (non-ASCII profile names came out as ????) is taken from tray-config.json.
        $lines = @(Get-Content -LiteralPath $envPath | ForEach-Object {
            if ($_ -match '^MCP_LOG_DIR=') { 'MCP_LOG_DIR=' + (Join-Path $dest 'logs') }
            elseif ($_ -match '^PROJECT_DIR=(.*)$') { $v = Rebase $Matches[1]; if ($v.Contains('?') -and $cfg.ProjectDir) { $v = $cfg.ProjectDir }; 'PROJECT_DIR=' + $v }
            else { $_ }
        })
        [System.IO.File]::WriteAllLines($envPath, $lines, (New-Object System.Text.UTF8Encoding $false))
    }
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

if (($Update -or $MigrateFrom) -and (Test-Path (Join-Path $dest "tray-config.json"))) {
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
    TrayMenuMode         = "panel"
} | ConvertTo-Json
$trayCfg | Set-Content -Path (Join-Path $dest "tray-config.json") -Encoding UTF8
}

if (-not $isCustomInstallDir) {
    Step "Start Menu shortcut"
    $shortcutDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
    $wsh = New-Object -ComObject WScript.Shell
    $sc = $wsh.CreateShortcut((Join-Path $shortcutDir "ZeroIDE MCP.lnk"))
    $sc.TargetPath = Join-Path $dest "ZeroIDE-MCP.exe"
    $sc.WorkingDirectory = $dest
    $sc.Save()
}

if ($MigrateFrom) {
    Step "Removing the earlier installation $MigrateFrom"
    $oldShortcut = Join-Path $shortcutDir 'Agent Power Tray.lnk'
    if ([System.IO.File]::Exists($oldShortcut)) { [System.IO.File]::Delete($oldShortcut) }
    try {
        $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
        $run = Get-Item $runKey
        foreach ($name in $run.GetValueNames()) {
            if ("$($run.GetValue($name))".IndexOf($MigrateFrom, [StringComparison]::OrdinalIgnoreCase) -ge 0) { Remove-ItemProperty -Path $runKey -Name $name }
        }
    } catch { Write-Warning "Autostart entry of the earlier build could not be checked: $($_.Exception.Message)" }
    [System.IO.Directory]::Delete($MigrateFrom, $true)
}

Step "Done"
if ($Update -or $MigrateFrom) { Write-Host "Updated $dest" } else { Write-Host "Installed to $dest" }
Write-Host "Autostart is OFF. Start 'ZeroIDE MCP' from the Start Menu when you want the agent online."
Write-Host "Next: follow AGENT_INSTALL.md, then docs\AGENT_CONNECTIONS.md."
