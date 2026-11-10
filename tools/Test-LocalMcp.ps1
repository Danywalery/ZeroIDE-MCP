param([string]$InstallDir=(Join-Path $env:LOCALAPPDATA 'ZeroIDE-MCP'))
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Onboarding.Common.ps1')
$result=Invoke-OnboardLocalCheck $InstallDir
$result|ConvertTo-Json -Depth 8
if($result.status -eq 'failed'){exit 1}
