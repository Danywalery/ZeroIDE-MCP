param(
 [string]$InstallDir=(Join-Path $env:LOCALAPPDATA 'ZeroIDE-MCP'),
 [switch]$OwnerChoseTailscale
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Onboarding.Common.ps1')
. (Join-Path $PSScriptRoot 'Onboarding.Ngrok.ps1')
. (Join-Path $PSScriptRoot 'Onboarding.Tailscale.ps1')
$result=Invoke-OnboardTailscale $InstallDir $OwnerChoseTailscale.IsPresent
$result|ConvertTo-Json -Depth 8
if($result.status -eq 'needs_attention'){exit 1}
