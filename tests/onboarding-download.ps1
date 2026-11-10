# Optional network test. Downloads to a unique temp directory; never executes ngrok or touches the tray.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'tools\Onboarding.Common.ps1')
. (Join-Path $root 'tools\Onboarding.Ngrok.ps1')
$temp=Join-Path ([IO.Path]::GetTempPath()) ('zeroide-download-test-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temp)
try {
 $binary=Get-OnboardNgrok @{Root=$temp}
 if(-not (Test-Path -LiteralPath $binary -PathType Leaf)){throw 'Downloaded executable missing'}
 Assert-OnboardNgrokSignature $binary
 if(-not $binary.StartsWith($temp+[IO.Path]::DirectorySeparatorChar)){throw 'Test escaped temporary directory'}
 Write-Host 'TOTAL DOWNLOAD: official HTTPS download, ZIP extraction and Windows publisher signature passed; executable NOT launched'
}finally{if(Test-Path -LiteralPath $temp){Remove-Item -LiteralPath $temp -Recurse -Force}}
