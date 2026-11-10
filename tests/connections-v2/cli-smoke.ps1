$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$cli = Join-Path $repo 'tools\ConnectionCtl\bin\Release\net8.0\connectionctl.dll'
$root = Join-Path $env:LOCALAPPDATA ('zeroide-cli-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$utf8 = New-Object Text.UTF8Encoding($false)
$count = 0
function Check($yes, $label) { if (-not $yes) { throw ('FAIL: ' + $label) }; $script:count++; Write-Host ('PASS: ' + $label) }
function Invoke-Cli([string[]]$CliArguments, [int]$ExpectedExit = 0) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $lines = & dotnet $cli @CliArguments 2> (Join-Path $root 'stderr.txt')
    $code = $LASTEXITCODE
    $ErrorActionPreference = $previous
    $out = ($lines | Out-String)
    $err = [IO.File]::ReadAllText((Join-Path $root 'stderr.txt'))
    if ($code -ne $ExpectedExit) { throw ('CLI exit mismatch: ' + $code) }
    if (($out + $err).Contains('TOKEN_MUST_NEVER_LEAK')) { throw 'Credential leaked' }
    if ($ExpectedExit -eq 0) { return ($out | ConvertFrom-Json) }
    return $null
}
try {
    $legacy = Join-Path $root 'legacy.json'
    $store = Join-Path $root 'registry.json'
    $profile = Join-Path $root 'profile.json'
    [IO.File]::WriteAllText($legacy, '{"Port":39011,"TunnelEnabled":false,"ControlSecret":"TOKEN_MUST_NEVER_LEAK","NgrokUrl":"https://saved.example"}', $utf8)
    [IO.File]::WriteAllText($profile, '{"Id":"custom","Name":"Other provider","Scope":"Public","Ownership":"External","Adapter":"other","Endpoint":"https://other.example/custom/MCP/","AddressKind":"stable"}', $utf8)
    $hash = (Get-FileHash -LiteralPath $legacy -Algorithm SHA256).Hash
    $c = Invoke-Cli @('init','--store',$store,'--legacy',$legacy)
    Check ($c.ActiveProfileId -eq 'local' -and $c.Revision -eq 0) 'offline migration preserves local selection'
    Check (-not [IO.File]::ReadAllText($store).Contains('TOKEN_MUST_NEVER_LEAK')) 'credentials not copied to registry'
    $v = Invoke-Cli @('validate','--profile',$profile)
    Check ($v.valid -and -not $v.launchesAnything) 'validation does not launch anything'
    $c = Invoke-Cli @('prepare','--store',$store,'--profile',$profile,'--revision','0')
    Check ($c.ActiveProfileId -eq 'local' -and $c.pending.profile.Endpoint -eq 'https://other.example/custom/MCP/') 'prepare preserves route and full custom URL'
    Invoke-Cli @('prepare','--store',$store,'--profile',$profile,'--revision','0') 2 | Out-Null
    $read = Invoke-Cli @('show','--store',$store)
    Check ($read.Revision -eq 1 -and $read.pending.OperationId -eq $c.pending.OperationId) 'stale agent cannot overwrite pending change'
    $c = Invoke-Cli @('cancel','--store',$store,'--operation',$read.pending.OperationId,'--revision','1')
    Check ($null -eq $c.pending -and $c.ActiveProfileId -eq 'local') 'cancel keeps active route'
    Invoke-Cli @('activate','--store',$store) 2 | Out-Null
    Check ((Invoke-Cli @('show','--store',$store)).Revision -eq 2) 'activation deliberately unavailable before runtime integration'
    Check ((Get-FileHash -LiteralPath $legacy -Algorithm SHA256).Hash -eq $hash) 'legacy config remains byte-for-byte unchanged'
    Write-Host ('TOTAL CLI: ' + $count + ' passed')
}
finally { Remove-Item -LiteralPath $root -Recurse -Force }
