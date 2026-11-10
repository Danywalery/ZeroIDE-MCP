param(
 [ValidateSet('state','prepare','start','address','check','activate','cancel','progress','note','secret-request')][string]$Action='state',
 [string]$ProfileFile='', [long]$Revision=-1, [string]$OperationId='', [string]$Endpoint='', [string]$BodyFile='',
 [string]$InstallDir=(Join-Path $env:LOCALAPPDATA 'ZeroIDE-MCP')
)
$ErrorActionPreference='Stop'
# Local control credentials never leave the PC or appear in command output.
$config=[IO.File]::ReadAllText((Join-Path $InstallDir 'tray-config.json')) | ConvertFrom-Json
if([string]::IsNullOrWhiteSpace($config.ControlSecret)){throw 'Missing local control secret'}
$port=if($config.ControlPort -gt 0){$config.ControlPort}else{39012}
$headers=@{'X-ZeroIDE-Control'=[string]$config.ControlSecret}
$base="http://127.0.0.1:$port/connections/"
if($Action -eq 'state'){$result=Invoke-RestMethod -Uri ($base+'state') -Headers $headers -TimeoutSec 30}
else {
 if($Action -in @('progress','note','secret-request')){
  if([string]::IsNullOrWhiteSpace($BodyFile)){throw 'BodyFile is required; it must not contain secret values'}
  if((Get-Item -LiteralPath $BodyFile).Length -gt 16384){throw 'Body exceeds 16 KiB'}
  $body=[IO.File]::ReadAllText((Resolve-Path -LiteralPath $BodyFile)) | ConvertFrom-Json
 }else{
  if($Revision -lt 0){throw 'An explicit current revision is required; load state first'}
  $body=@{revision=$Revision}
  if($Action -eq 'prepare'){
   if((Get-Item -LiteralPath $ProfileFile).Length -gt 65536){throw 'Profile exceeds 64 KiB'}
   $body.profile=[IO.File]::ReadAllText((Resolve-Path -LiteralPath $ProfileFile)) | ConvertFrom-Json
  }else{$body.operationId=$OperationId}
  if($Action -eq 'address'){$body.endpoint=$Endpoint}
 }
 $json=$body|ConvertTo-Json -Depth 20 -Compress
 $result=Invoke-RestMethod -Method Post -Uri ($base+$Action) -Headers $headers -ContentType 'application/json; charset=utf-8' -Body ([Text.Encoding]::UTF8.GetBytes($json)) -TimeoutSec 240
}
$result|ConvertTo-Json -Depth 20
