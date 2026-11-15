# First-time Cloudflare Tunnel workflow (owner's own domain). Requires Onboarding.Common.ps1, Onboarding.Ngrok.ps1 and Onboarding.Tailscale.ps1 (shared helpers).
# The owner creates the tunnel and its public hostname in Cloudflare Zero Trust; this PC side (cloudflared, token, launch) is handled here.
function Get-OnboardCloudflared([hashtable]$Context){
 $candidates=@((Join-Path (Join-Path (Join-Path $Context.Root 'tools') 'cloudflared') 'cloudflared.exe'))
 foreach($base in @(${env:ProgramFiles(x86)},$env:ProgramFiles)){ if($base){$candidates+=(Join-Path (Join-Path $base 'cloudflared') 'cloudflared.exe')} }
 foreach($p in $candidates){if(Test-Path -LiteralPath $p -PathType Leaf){Assert-OnboardPublisherSignature $p 'Cloudflare';return $p}}
 $directory=Join-Path (Join-Path $Context.Root 'tools') 'cloudflared';[void][IO.Directory]::CreateDirectory($directory)
 $temp=Join-Path $directory ('.download-'+[Guid]::NewGuid().ToString('N')+'.exe')
 try{
  Save-OnboardFile ([Uri]'https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe') @('github.com','objects.githubusercontent.com','release-assets.githubusercontent.com') $temp 134217728
  Assert-OnboardPublisherSignature $temp 'Cloudflare'
  $destination=Join-Path $directory 'cloudflared.exe';[IO.File]::Move($temp,$destination);return $destination
 }finally{try{if(Test-Path -LiteralPath $temp){[IO.File]::Delete($temp)}}catch{}}
}
function Set-OnboardCloudflareProgress([hashtable]$Context,[string]$Task,[int]$Stage,[string]$Waiting=''){
 $labels=@('Install cloudflared','Enter the tunnel token in the PC window','Approve the connection on this PC','Check the address','Connect your AI client')
 $steps=@();for($i=0;$i -lt $labels.Count;$i++){$s=if($i -lt $Stage){'done'}elseif($i -eq $Stage){'current'}else{'todo'};$steps+=@{text=$labels[$i];state=$s;owner=($i -in @(1,2,4))}}
 $title='Connect your AI through Cloudflare Tunnel'
 if($Context.Language -eq 'ru'){
  $title='Подключаем твой AI через Cloudflare Tunnel'
  $ru=@('Установить cloudflared','Вставить токен туннеля в окно на ПК','Разрешить подключение на этом ПК','Проверить ссылку','Подключить твой AI-клиент')
  for($i=0;$i -lt $steps.Count;$i++){$steps[$i].text=$ru[$i]}
  $Waiting=switch($Stage){1{'Скопируй токен туннеля из Cloudflare Zero Trust и вставь его в окно на ПК, не в чат.'};2{'Проверь окно подключения на этом ПК и нажми «Разрешить», если согласен.'};4{'Добавь ссылку MCP в свой AI-клиент и войди. Локальная проверка — ещё не подключение клиента.'};default{''}}
 }
 $null=Invoke-OnboardControl $Context 'progress' @{taskId=$Task;title=$title;waitingFor=$Waiting;steps=$steps}
}
function Invoke-OnboardCloudflare([string]$InstallDir,[bool]$OwnerChoseCloudflare,[string]$Hostname){
 $phase='owner_choice';$ctx=$null;$operation='';$task='';$revision=-1
 try{
  if(-not $OwnerChoseCloudflare){return @{status='waiting_for_owner';code='CHOOSE_CONNECTION';message='Explain internet access first; obtain the owner choice and the public hostname.'}}
  $Hostname=([string]$Hostname).Trim().TrimEnd('/');if($Hostname -match '^(?i)https?://'){$Hostname=$Hostname.Substring($Hostname.IndexOf('//')+2)}
  if(-not $Hostname -or $Hostname.Length -gt 253 -or [Uri]::CheckHostName($Hostname) -ne 'Dns' -or -not $Hostname.Contains('.')){return @{status='waiting_for_owner';code='HOSTNAME_REQUIRED';ownerAction='Enter the public hostname configured for the tunnel in Cloudflare, for example mcp.example.com.'}}
  $phase='installation';$ctx=Get-OnboardContext $InstallDir;$phase='preflight';$state=Invoke-OnboardControl $ctx 'state'
  if(-not $state.serverEnabled){return @{status='waiting_for_owner';code='ENABLE_MCP';ownerAction='Enable MCP in the tray. The helper never enables it.'}}
  if(-not (Test-OnboardFirstSetup $state $ctx)){return @{status='needs_attention';code='EXISTING_CONNECTION_OR_DRAFT';message='First setup from local only. Leave the existing connection and draft untouched.'}}
  if($state.guide.taskId -and $state.guide.updatedAt -and ([DateTimeOffset]::UtcNow-[DateTimeOffset]::Parse($state.guide.updatedAt)).TotalMinutes -lt 10){return @{status='needs_attention';code='OTHER_GUIDE_ACTIVE';message='Do not overwrite another active guide.'}}
  if(Get-Process -Name cloudflared -ErrorAction SilentlyContinue){return @{status='needs_attention';code='CLOUDFLARED_ALREADY_RUNNING';message='Leave the process untouched; use the advanced workflow.'}}
  $revision=[long]$state.revision;$task='setup-'+[Guid]::NewGuid().ToString('N');$profileId='cloudflare-'+[Guid]::NewGuid().ToString('N').Substring(0,12)
  $phase='cloudflared_install';Set-OnboardCloudflareProgress $ctx $task 0;$binary=Get-OnboardCloudflared $ctx
  $phase='provider_key';Set-OnboardCloudflareProgress $ctx $task 1 'Copy the tunnel token from Cloudflare Zero Trust and paste it in the PC window, not in chat.'
  $reason=if($ctx.Language -eq 'ru'){'Вставь токен туннеля Cloudflare в это окно, не в чат. Это ключ провайдера, а не пароль MCP.'}else{'Paste the Cloudflare tunnel token here, not in chat. This is not an MCP password.'}
  $stored=Invoke-OnboardControl $ctx 'secret-request' @{kind='provider-token';name='cloudflare';reason=$reason}
  if(-not $stored.stored){$null=Invoke-OnboardControl $ctx 'progress' @{taskId=$task;clear=$true};return @{status='cancelled';code='KEY_ENTRY_CANCELLED';activeConnectionChanged=$false}}
  if(-not [IO.Path]::IsPathRooted([string]$stored.configurationFile) -or -not (Test-Path -LiteralPath $stored.configurationFile -PathType Leaf)){throw 'Protected token file not stored'}
  $state=Invoke-OnboardControl $ctx 'state';if($state.revision -ne $revision -or -not $state.serverEnabled -or -not (Test-OnboardFirstSetup $state $ctx)){throw 'State changed during key entry'}
  $url='https://'+$Hostname.ToLowerInvariant()+'/mcp'
  $profile=@{Id=$profileId;Name='Cloudflare Tunnel';Scope='Public';Ownership='Managed';Adapter='cloudflare';Endpoint=$url;AddressKind='stable';Launch=@{Executable=$binary;Arguments=@('tunnel','--no-autoupdate','run','--token-file',[string]$stored.configurationFile);WorkingDirectory=(Split-Path $binary -Parent);ConfigurationFile=[string]$stored.configurationFile}}
  $phase='prepare';$state=Invoke-OnboardControl $ctx 'prepare' @{revision=$revision;profile=$profile};$operation=[string]$state.pending.OperationId;$revision++
  if(-not $operation){throw 'Prepared operation missing'};Assert-OnboardCandidate $state $revision $operation $profileId
  $phase='owner_launch';Set-OnboardCloudflareProgress $ctx $task 2 'Review and approve the connection window on this PC. No program starts before approval.'
  $state=Invoke-OnboardControl $ctx 'start' @{revision=$revision;operationId=$operation};Assert-OnboardCandidate $state $revision $operation $profileId
  $phase='check';Set-OnboardCloudflareProgress $ctx $task 3;$null=Wait-OnboardEndpoint ('https://'+$Hostname.ToLowerInvariant())
  $state=Invoke-OnboardControl $ctx 'check' @{revision=$revision;operationId=$operation};Assert-OnboardCandidate $state $revision $operation $profileId
  $phase='activate';$state=Invoke-OnboardControl $ctx 'activate' @{revision=$revision;operationId=$operation}
  if($state.pending -or $state.revision -ne ($revision+1) -or $state.currentUrl -ne $url){throw 'Activation not confirmed; inspect before retrying'}
  $phase='client_handoff';Set-OnboardCloudflareProgress $ctx $task 4 'Add the MCP URL in your AI client and complete sign-in. A local check is not a real client connection.'
  $note=if($ctx.Language -eq 'ru'){'Приложение запускает и выключает cloudflared вместе с MCP. Туннель и hostname живут в твоём аккаунте Cloudflare. Ссылка для AI — в «Подключении», клиенты и отзыв доступа — в «Доступе».'}else{'cloudflared is started and stopped by this app together with MCP. The tunnel and hostname live in your Cloudflare account. The AI URL is under Connection; clients and revocation are under Access.'}
  $null=Invoke-OnboardControl $ctx 'note' @{text=$note}
  return @{status='awaiting_client';url=$url;taskId=$task;clientOnboardingComplete=$false;next='Connect the chosen AI client. Prefer OAuth; otherwise create a named scoped token in Access. Never print tokens.'}
 }catch{return @{status='needs_attention';code=$phase;operationId=$operation;taskId=$task;detail=([string]$_.Exception.Message);message='Setup stopped without automatic retry or rollback. Inspect state; do not overwrite settings, adopt another draft, bypass consent, or print raw errors. A matching candidate may remain.'}}
 finally{if($ctx){$ctx.ControlSecret=$null}}
}
