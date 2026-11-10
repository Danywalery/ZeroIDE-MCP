param(
 [string]$InstallDir=(Join-Path $env:LOCALAPPDATA 'ZeroIDE-MCP'),
 [switch]$OwnerChoseNgrok,
 [string]$NgrokPath=''
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Onboarding.Common.ps1')
. (Join-Path $PSScriptRoot 'Onboarding.Ngrok.ps1')
$result=Invoke-OnboardNgrok $InstallDir $OwnerChoseNgrok.IsPresent $NgrokPath
$result|ConvertTo-Json -Depth 8
if($result.status -eq 'needs_attention'){exit 1}
