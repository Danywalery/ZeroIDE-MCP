param(
 [string]$InstallDir=(Join-Path $env:LOCALAPPDATA 'ZeroIDE-MCP'),
 [switch]$OwnerChoseCloudflare,
 [string]$Hostname=''
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Onboarding.Common.ps1')
. (Join-Path $PSScriptRoot 'Onboarding.Ngrok.ps1')
. (Join-Path $PSScriptRoot 'Onboarding.Tailscale.ps1')
. (Join-Path $PSScriptRoot 'Onboarding.Cloudflare.ps1')
$result=Invoke-OnboardCloudflare $InstallDir $OwnerChoseCloudflare.IsPresent $Hostname
$result|ConvertTo-Json -Depth 8
if($result.status -eq 'needs_attention'){exit 1}
