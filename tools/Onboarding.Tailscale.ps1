# First-time Tailscale workflow. Requires Onboarding.Common.ps1 and Onboarding.Ngrok.ps1 (shared helpers) to be loaded first.
# Publishes the local MCP server inside the owner's tailnet with a foreground "tailscale serve" that the app starts and stops with MCP.
function Get-OnboardTailscaleExe {
 foreach($base in @($env:ProgramFiles,${env:ProgramFiles(x86)})){ if(-not $base){continue}; $exe=Join-Path (Join-Path $base 'Tailscale') 'tailscale.exe'; if(Test-Path -LiteralPath $exe -PathType Leaf){return $exe} }
 return ''
}
function Assert-OnboardPublisherSignature([string]$Path,[string]$Publisher){
 $sig=Get-AuthenticodeSignature -LiteralPath $Path
 if($sig.Status -ne 'Valid' -or -not $sig.SignerCertificate -or $sig.SignerCertificate.Subject -notmatch ('(?i)' + $Publisher)){throw ($Publisher + ' signature or publisher is not trusted')}
}
function Save-OnboardFile([Uri]$Uri,[string[]]$Hosts,[string]$Destination,[long]$MaxBytes){
 $handler=[Net.Http.HttpClientHandler]::new();$handler.AllowAutoRedirect=$false
 $client=[Net.Http.HttpClient]::new($handler);$client.Timeout=[Threading.Timeout]::InfiniteTimeSpan
 $cancel=[Threading.CancellationTokenSource]::new(300000);$response=$null;$stream=$null;$file=$null
 try{
  for($redirect=0;$redirect -le 5;$redirect++){
   if($Uri.Scheme -ne 'https' -or $Uri.DnsSafeHost -notin $Hosts){throw 'Untrusted download destination'}
   $response=$client.GetAsync($Uri,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$cancel.Token).GetAwaiter().GetResult()
   if([int]$response.StatusCode -in @(301,302,303,307,308)){
    if($redirect -eq 5 -or -not $response.Headers.Location){throw 'Download redirect limit'}
    $Uri=[Uri]::new($Uri,$response.Headers.Location);$response.Dispose();$response=$null;continue
   }
   if(-not $response.IsSuccessStatusCode){throw 'Download failed'}
   $stream=$response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();$file=[IO.File]::Open($Destination,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
   $buffer=New-Object byte[] 65536;$total=0
   while(($n=$stream.ReadAsync($buffer,0,$buffer.Length,$cancel.Token).GetAwaiter().GetResult()) -gt 0){$total+=$n;if($total -gt $MaxBytes){throw 'Download too large'};$file.Write($buffer,0,$n)}
   return
  }
 }finally{if($file){$file.Dispose()};if($stream){$stream.Dispose()};if($response){$response.Dispose()};$cancel.Dispose();$client.Dispose();$handler.Dispose()}
}
function Install-OnboardTailscale([hashtable]$Context){
 # Official installer, signature checked, run visibly: it asks for administrator permission itself. Nothing is installed silently.
 $directory=Join-Path (Join-Path $Context.Root 'tools') 'tailscale';[void][IO.Directory]::CreateDirectory($directory)
 $installer=Join-Path $directory ('tailscale-setup-'+[Guid]::NewGuid().ToString('N').Substring(0,8)+'.exe')
 try{
  Save-OnboardFile ([Uri]'https://pkgs.tailscale.com/stable/tailscale-setup-latest.exe') @('pkgs.tailscale.com') $installer 268435456
  Assert-OnboardPublisherSignature $installer 'Tailscale'
  $p=Start-Process -FilePath $installer -PassThru -Wait
  Start-Sleep -Seconds 2
 }finally{try{[IO.File]::Delete($installer)}catch{}}
 return (Get-OnboardTailscaleExe)
}
function Get-OnboardTailscaleStatus([string]$Exe){
 $text=(& $Exe status --json 2>$null) -join "`n"
 if(-not $text -or -not $text.TrimStart().StartsWith('{')){throw 'Tailscale service is not responding'}
 return ($text|ConvertFrom-Json)
}
function Wait-OnboardTailscaleLogin([string]$Exe,[hashtable]$Context){
 # Starts the sign-in, opens the browser once the auth URL is known, waits up to 5 minutes for the tailnet to be Running.
 $status=Get-OnboardTailscaleStatus $Exe
 if($status.BackendState -eq 'Running'){return $status}
 $login=Start-Process -FilePath $Exe -ArgumentList @('login') -WindowStyle Hidden -PassThru
 $opened='';$end=[DateTime]::UtcNow.AddMinutes(5)
 try{
  do{
   Start-Sleep -Seconds 2
   try{$status=Get-OnboardTailscaleStatus $Exe}catch{continue}
   if($status.BackendState -eq 'Running'){return $status}
   if($status.AuthURL -and $status.AuthURL -ne $opened -and $status.AuthURL -like 'https://login.tailscale.com/*'){$opened=[string]$status.AuthURL;Start-Process $opened|Out-Null}
  }while([DateTime]::UtcNow -lt $end)
 }finally{try{if(-not $login.HasExited){$login.Kill()}}catch{}}
 return $null
}
function Get-OnboardTailscaleServePort([string]$Exe){
 # 443, 8443 and 10000 are the HTTPS ports Serve accepts; an existing background Serve/Funnel on a port is never touched.
 $used=@()
 try{$text=(& $Exe serve status --json 2>$null) -join "`n";if($text -and $text.TrimStart().StartsWith('{')){$cfg=$text|ConvertFrom-Json;if($cfg.TCP){$used=@($cfg.TCP.PSObject.Properties.Name)}}}catch{}
 foreach($port in @(443,8443,10000)){if([string]$port -notin $used){return $port}}
 throw 'All Serve HTTPS ports are already configured on this node'
}
function Wait-OnboardEndpoint([string]$Origin){
 # The first HTTPS request provisions the node certificate; any HTTP answer means the proxy is up.
 $end=[DateTime]::UtcNow.AddSeconds(120)
 do{
  try{$null=Invoke-WebRequest -UseBasicParsing -Uri ($Origin+'/health') -TimeoutSec 15 -Headers @{'ngrok-skip-browser-warning'='1'};return $true}
  catch{if($_.Exception.Response){return $true}}
  Start-Sleep -Seconds 3
 }while([DateTime]::UtcNow -lt $end)
 return $false
}
function Set-OnboardTailscaleProgress([hashtable]$Context,[string]$Task,[int]$Stage,[string]$Waiting=''){
 $labels=@('Install Tailscale','Sign in to Tailscale','Enable HTTPS in the Tailscale admin console','Approve the connection on this PC','Check the address','Connect your AI client')
 $owner=@(0,1,2,3,5)
 $steps=@();for($i=0;$i -lt $labels.Count;$i++){$s=if($i -lt $Stage){'done'}elseif($i -eq $Stage){'current'}else{'todo'};$steps+=@{text=$labels[$i];state=$s;owner=($i -in $owner)}}
 $title='Connect your AI through your Tailscale network'
 if($Context.Language -eq 'ru'){
  $title='Подключаем твой AI через сеть Tailscale'
  $ru=@('Установить Tailscale','Войти в Tailscale','Включить HTTPS в консоли Tailscale','Разрешить подключение на этом ПК','Проверить ссылку','Подключить твой AI-клиент')
  for($i=0;$i -lt $steps.Count;$i++){$steps[$i].text=$ru[$i]}
  $Waiting=switch($Stage){0{'Подтверди установку Tailscale в окне установщика (понадобятся права администратора).'};1{'Войди в Tailscale на странице, которая открылась в браузере.'};2{'Включи HTTPS Certificates в консоли Tailscale: DNS → HTTPS Certificates → Enable.'};3{'Проверь окно подключения на этом ПК и нажми «Разрешить», если согласен.'};5{'Добавь ссылку MCP в свой AI-клиент на другом компьютере из той же сети Tailscale.'};default{''}}
 }
 $null=Invoke-OnboardControl $Context 'progress' @{taskId=$Task;title=$title;waitingFor=$Waiting;steps=$steps}
}
function Invoke-OnboardTailscale([string]$InstallDir,[bool]$OwnerChoseTailscale){
 $phase='owner_choice';$ctx=$null;$operation='';$task='';$revision=-1
 try{
  if(-not $OwnerChoseTailscale){return @{status='waiting_for_owner';code='CHOOSE_CONNECTION';message='Explain private-network access first; obtain the owner choice.'}}
  $phase='installation';$ctx=Get-OnboardContext $InstallDir;$phase='preflight';$state=Invoke-OnboardControl $ctx 'state'
  if(-not $state.serverEnabled){return @{status='waiting_for_owner';code='ENABLE_MCP';ownerAction='Enable MCP in the tray. The helper never enables it.'}}
  if(-not (Test-OnboardFirstSetup $state $ctx)){return @{status='needs_attention';code='EXISTING_CONNECTION_OR_DRAFT';message='First setup from local only. Leave the existing connection and draft untouched.'}}
  if($state.guide.taskId -and $state.guide.updatedAt -and ([DateTimeOffset]::UtcNow-[DateTimeOffset]::Parse($state.guide.updatedAt)).TotalMinutes -lt 10){return @{status='needs_attention';code='OTHER_GUIDE_ACTIVE';message='Do not overwrite another active guide.'}}
  $revision=[long]$state.revision;$task='setup-'+[Guid]::NewGuid().ToString('N');$profileId='tailscale-'+[Guid]::NewGuid().ToString('N').Substring(0,12)
  $phase='tailscale_install';$exe=Get-OnboardTailscaleExe
  if(-not $exe){Set-OnboardTailscaleProgress $ctx $task 0 'Confirm the Tailscale installer window (it asks for administrator permission).';$exe=Install-OnboardTailscale $ctx}
  if(-not $exe){$null=Invoke-OnboardControl $ctx 'progress' @{taskId=$task;clear=$true};return @{status='waiting_for_owner';code='INSTALL_TAILSCALE';ownerAction='Tailscale was not installed. Finish the installer, then start again.'}}
  $phase='tailscale_login';Set-OnboardTailscaleProgress $ctx $task 1 'Sign in to Tailscale on the page that opened in the browser.'
  $status=Wait-OnboardTailscaleLogin $exe $ctx
  if(-not $status){$null=Invoke-OnboardControl $ctx 'progress' @{taskId=$task;clear=$true};return @{status='waiting_for_owner';code='LOGIN_TAILSCALE';ownerAction='Sign in to Tailscale (tray icon of Tailscale or the browser page), then start again.'}}
  $phase='tailscale_https';$domains=@($status.CertDomains|Where-Object {$_})
  if($domains.Count -eq 0){Set-OnboardTailscaleProgress $ctx $task 2 'Enable HTTPS certificates in the Tailscale admin console: DNS -> HTTPS Certificates -> Enable.';return @{status='waiting_for_owner';code='ENABLE_HTTPS';url='https://login.tailscale.com/admin/dns';ownerAction='Enable HTTPS Certificates (and MagicDNS) in the Tailscale admin console, then start again.'}}
  Set-OnboardTailscaleProgress $ctx $task 3;$domain=[string]$domains[0];$port=Get-OnboardTailscaleServePort $exe
  $origin='https://'+$domain+$(if($port -eq 443){''}else{':'+$port});$url=$origin+'/mcp'
  $state=Invoke-OnboardControl $ctx 'state';if($state.revision -ne $revision -or -not $state.serverEnabled -or -not (Test-OnboardFirstSetup $state $ctx)){throw 'State changed during preparation'}
  $profile=@{Id=$profileId;Name='Tailscale';Scope='Private';Ownership='Managed';Adapter='tailscale';Endpoint=$url;AddressKind='stable';Launch=@{Executable=$exe;Arguments=@('serve',('--https='+$port),'--yes',('http://127.0.0.1:'+$ctx.Port));WorkingDirectory=(Split-Path $exe -Parent);ConfigurationFile=''}}
  $phase='prepare';$state=Invoke-OnboardControl $ctx 'prepare' @{revision=$revision;profile=$profile};$operation=[string]$state.pending.OperationId;$revision++
  if(-not $operation){throw 'Prepared operation missing'};Assert-OnboardCandidate $state $revision $operation $profileId
  $phase='owner_launch';Set-OnboardTailscaleProgress $ctx $task 3 'Review and approve the connection window on this PC. No program starts before approval.'
  $state=Invoke-OnboardControl $ctx 'start' @{revision=$revision;operationId=$operation};Assert-OnboardCandidate $state $revision $operation $profileId
  $phase='check';Set-OnboardTailscaleProgress $ctx $task 4;$null=Wait-OnboardEndpoint $origin
  $state=Invoke-OnboardControl $ctx 'check' @{revision=$revision;operationId=$operation};Assert-OnboardCandidate $state $revision $operation $profileId
  $phase='activate';$state=Invoke-OnboardControl $ctx 'activate' @{revision=$revision;operationId=$operation}
  if($state.pending -or $state.revision -ne ($revision+1) -or $state.currentUrl -ne $url){throw 'Activation not confirmed; inspect before retrying'}
  $phase='client_handoff';Set-OnboardTailscaleProgress $ctx $task 5 'Add the MCP URL in your AI client on another computer in the same Tailscale network.'
  $note=if($ctx.Language -eq 'ru'){'Приложение запускает и выключает Tailscale Serve вместе с MCP. Ссылка работает только внутри твоей сети Tailscale. Ссылка для AI — в «Подключении», клиенты и отзыв доступа — в «Доступе».'}else{'Tailscale Serve is started and stopped by this app together with MCP. The URL works only inside your Tailscale network. The AI URL is under Connection; clients and revocation are under Access.'}
  $null=Invoke-OnboardControl $ctx 'note' @{text=$note}
  return @{status='awaiting_client';url=$url;taskId=$task;clientOnboardingComplete=$false;next='Connect the chosen AI client from another device in the same tailnet. Prefer OAuth; otherwise create a named scoped token in Access. Never print tokens.'}
 }catch{return @{status='needs_attention';code=$phase;operationId=$operation;taskId=$task;detail=([string]$_.Exception.Message);message='Setup stopped without automatic retry or rollback. Inspect state; do not overwrite settings, adopt another draft, bypass consent, or print raw errors. A matching candidate may remain.'}}
 finally{if($ctx){$ctx.ControlSecret=$null}}
}
