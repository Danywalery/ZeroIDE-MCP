$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$installer = Join-Path $root 'installer\install.ps1'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('zeroide-installer-' + [Guid]::NewGuid().ToString('N'))
$oldLocal = $env:LOCALAPPDATA
$count = 0
function Check($ok, $name) { if (-not $ok) { throw "FAIL: $name" }; $script:count++; Write-Host "PASS: $name" }
try {
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($installer, [ref]$null, [ref]$errors)
    Check ($errors.Count -eq 0) 'PowerShell installer parses'
    $source = [IO.File]::ReadAllText($installer)
    $writes = $ast.FindAll({param($node) $node -is [System.Management.Automation.Language.CommandAst] -and $node.GetCommandName() -in @('Write-Host','Write-Output','Write-Warning','Write-Error')}, $true)
    Check (-not @($writes | Where-Object {$_.Extent.Text -match '\$(token|controlSecret)\b'}).Count) 'installer output never interpolates token or control secret'
    Check ($source.Contains("'tools\Configure-Connection.ps1'") -and $source.Contains("'docs\AGENT_CONNECTIONS.md'")) 'connection instructions and helper included'
    $workflow=[IO.File]::ReadAllText((Join-Path $root '.github\workflows\release.yml'))
    $manifest=[IO.File]::ReadAllText((Join-Path $root 'agent-manifest.json'))|ConvertFrom-Json
    foreach($helper in @('Onboarding.Common.ps1','Onboarding.Ngrok.ps1','Test-LocalMcp.ps1','Setup-Ngrok.ps1')){
        Check ($source.Contains("'tools\"+$helper+"'") -and $source.Contains("'"+$helper+"'")) ($helper+' required and copied by installer')
        Check ($workflow.Contains('tools/'+$helper) -and ('tools/'+$helper) -in $manifest.artifacts) ($helper+' staged by release and declared in manifest')
    }
    Check ($manifest.pathQuestion -eq 'before-install' -and $manifest.onboarding.localCheck -eq 'tools/Test-LocalMcp.ps1') 'manifest chooses client before installation and uses ready-made check'
    Check ($workflow.Contains('& ./tests/onboarding-helpers.ps1')) 'release CI gates onboarding helper tests'
    Check ($source.Contains('if ($SshTarget -ne')) 'SSH setup optional for local install'
    New-Item -ItemType Directory -Force (Join-Path $temp 'ZeroIDE-MCP') | Out-Null
    $sentinel = Join-Path $temp 'ZeroIDE-MCP\keep.txt'
    [IO.File]::WriteAllText($sentinel, 'unchanged')
    $env:LOCALAPPDATA = $temp
    $refused = $false
    try { & $installer } catch { $refused = $_.Exception.Message -like '*ZeroIDE-MCP already exists*' }
    Check $refused 'existing install is refused before mutation'
    Check ([IO.File]::ReadAllText($sentinel) -eq 'unchanged') 'existing install remains untouched'
    Remove-Item (Join-Path $temp 'ZeroIDE-MCP') -Recurse -Force
    New-Item -ItemType Directory -Force (Join-Path $temp 'AgentPower') | Out-Null
    $legacySentinel = Join-Path $temp 'AgentPower\keep.txt'
    [IO.File]::WriteAllText($legacySentinel, 'unchanged')
    $refused = $false
    try { & $installer } catch { $refused = $_.Exception.Message -like '*earlier installation*' }
    Check $refused 'earlier AgentPower install is refused before mutation'
    Check (([IO.File]::ReadAllText($legacySentinel) -eq 'unchanged') -and -not (Test-Path (Join-Path $temp 'ZeroIDE-MCP'))) 'earlier install untouched and nothing new created'
    Remove-Item (Join-Path $temp 'AgentPower') -Recurse -Force
    $broken = Join-Path $temp 'broken\installer'
    New-Item -ItemType Directory -Force $broken | Out-Null
    Copy-Item $installer (Join-Path $broken 'install.ps1')
    $refused = $false
    try { & (Join-Path $broken 'install.ps1') } catch { $refused = $_.Exception.Message -like '*Incomplete release package*' }
    Check $refused 'incomplete package refused before installing'
    Check (-not (Test-Path (Join-Path $temp 'ZeroIDE-MCP'))) 'incomplete package leaves no partial installation'
    Write-Host ('TOTAL INSTALLER: ' + $count + ' passed; no real installation performed')
} finally {
    $env:LOCALAPPDATA = $oldLocal
    if (Test-Path $temp) { Remove-Item $temp -Recurse -Force }
}
