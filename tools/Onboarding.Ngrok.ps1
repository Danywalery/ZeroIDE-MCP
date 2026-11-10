# First-time ngrok workflow only. Existing connections, drafts and processes are never adopted.
function Test-OnboardFirstSetup($State,[hashtable]$Context){
 if($State.pending -or $State.retainedPrevious){return $false};$uri=$null
 if(-not [Uri]::TryCreate([string]$State.currentUrl,[UriKind]::Absolute,[ref]$uri)){return $false}
 return ($uri.Scheme -eq 'http' -and $uri.Host -in @('127.0.0.1','localhost','[::1]','::1') -and $uri.Port -eq $Context.Port -and $uri.AbsolutePath -eq '/mcp' -and -not $uri.Query -and -not $uri.Fragment -and -not $uri.UserInfo)
}
function Assert-OnboardNgrokSignature([string]$Path){
 $sig=Get-AuthenticodeSignature -LiteralPath $Path
 if($sig.Status -ne 'Valid' -or -not $sig.SignerCertificate -or $sig.SignerCertificate.Subject -notmatch '(?i)(?:CN|O)="?ngrok,? Inc\.?"?(?:,|$)'){throw 'ngrok signature or publisher is not trusted'}
}
function Save-OnboardNgrokDownload([string]$Destination){
 $uri=[Uri]'https://bin.equinox.io/c/bNyj1mQVY4c/ngrok-v3-stable-windows-amd64.zip'
 $handler=[Net.Http.HttpClientHandler]::new();$handler.AllowAutoRedirect=$false
 $client=[Net.Http.HttpClient]::new($handler);$client.Timeout=[Threading.Timeout]::InfiniteTimeSpan
 $cancel=[Threading.CancellationTokenSource]::new(120000);$response=$null;$stream=$null;$file=$null
 try{
  for($redirect=0;$redirect -le 3;$redirect++){
   if($uri.Scheme -ne 'https' -or $uri.DnsSafeHost -notin @('bin.equinox.io','downloads.ngrok.com')){throw 'Untrusted download destination'}
   $response=$client.GetAsync($uri,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$cancel.Token).GetAwaiter().GetResult()
   if([int]$response.StatusCode -in @(301,302,303,307,308)){
    if($redirect -eq 3 -or -not $response.Headers.Location){throw 'Download redirect limit'}
    $uri=[Uri]::new($uri,$response.Headers.Location);$response.Dispose();$response=$null;continue
   }
   if(-not $response.IsSuccessStatusCode){throw 'Download failed'}
   $stream=$response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();$file=[IO.File]::Open($Destination,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
   $buffer=New-Object byte[] 65536;$total=0
   while(($n=$stream.ReadAsync($buffer,0,$buffer.Length,$cancel.Token).GetAwaiter().GetResult()) -gt 0){$total+=$n;if($total -gt 67108864){throw 'Archive too large'};$file.Write($buffer,0,$n)}
   return
  }
 }finally{if($file){$file.Dispose()};if($stream){$stream.Dispose()};if($response){$response.Dispose()};$cancel.Dispose();$client.Dispose();$handler.Dispose()}
}
function Get-OnboardNgrok([hashtable]$Context,[string]$NgrokPath=''){
 $candidates=if($NgrokPath){@([IO.Path]::GetFullPath($NgrokPath))}else{@((Join-Path $Context.Root 'tools\ngrok\ngrok.exe'),(Join-Path $Context.Root 'bin\ngrok.exe'))}
 foreach($p in $candidates){if(Test-Path -LiteralPath $p -PathType Leaf){Assert-OnboardNgrokSignature $p;return $p}}
 if($NgrokPath){throw 'Requested executable missing'}
 $directory=Join-Path $Context.Root 'tools\ngrok';[void][IO.Directory]::CreateDirectory($directory)
 $temp=Join-Path $directory ('.download-'+[Guid]::NewGuid().ToString('N'));[void][IO.Directory]::CreateDirectory($temp)
 try{
  $zip=Join-Path $temp 'ngrok.zip';Save-OnboardNgrokDownload $zip;Add-Type -AssemblyName System.IO.Compression.FileSystem
  $archive=[IO.Compression.ZipFile]::OpenRead($zip)
  try{$entry=$archive.GetEntry('ngrok.exe');if(-not $entry -or $entry.Length -gt 268435456 -or $entry.Length -lt 1024){throw 'Unexpected archive'};[IO.Compression.ZipFileExtensions]::ExtractToFile($entry,(Join-Path $temp 'ngrok.exe'),$false)}finally{$archive.Dispose()}
  $binary=Join-Path $temp 'ngrok.exe';Assert-OnboardNgrokSignature $binary
  $destination=Join-Path $directory 'ngrok.exe';[IO.File]::Move($binary,$destination);return $destination
 }finally{if(Test-Path $temp){Remove-Item -LiteralPath $temp -Recurse -Force}}
}
function Get-OnboardInspectionPort{
 for($port=4042;$port -lt 4063;$port++){$listener=$null;try{$listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,$port);$listener.Start();return $port}catch{}finally{if($listener){$listener.Stop()}}}
 throw 'No free inspection port'
}
function Get-OnboardNgrokUrl([hashtable]$Context,[int]$InspectionPort){
 $end=[DateTime]::UtcNow.AddSeconds(30)
 do{
  try{
   $r=Invoke-OnboardHttp -Port $InspectionPort -Path '/api/tunnels' -TimeoutSec 3
   if($r.Status -ne 200){throw 'Inspection API unavailable'};$data=$r.Text|ConvertFrom-Json
   foreach($tunnel in $data.tunnels){
    if($tunnel.config.addr -ne ('http://127.0.0.1:'+$Context.Port)){continue};$u=$null
    if([Uri]::TryCreate([string]$tunnel.public_url,[UriKind]::Absolute,[ref]$u) -and $u.Scheme -eq 'https' -and $u.Port -eq 443 -and -not $u.UserInfo -and -not $u.Query -and -not $u.Fragment -and $u.AbsolutePath -eq '/' -and $u.DnsSafeHost -match '(?i)\.(ngrok-free\.dev|ngrok-free\.app|ngrok\.app|ngrok\.io)$'){return ($u.GetLeftPart([UriPartial]::Authority)+'/mcp')}
   }
  }catch{}
  Start-Sleep -Milliseconds 500
 }while([DateTime]::UtcNow -lt $end)
 throw 'Assigned URL not found; never guess it'
}
function Set-OnboardProgress([hashtable]$Context,[string]$Task,[int]$Stage,[string]$Waiting=''){
 $labels=@('Install ngrok','Enter the ngrok key in the PC window','Approve the connection on this PC','Check the address','Connect your AI client')
 $steps=@();for($i=0;$i -lt $labels.Count;$i++){$s=if($i -lt $Stage){'done'}elseif($i -eq $Stage){'current'}else{'todo'};$steps+=@{text=$labels[$i];state=$s;owner=($i -in @(1,2,4))}}
 $title='Connect your AI through ngrok'
 if($Context.Language -eq 'ru'){
  $title='Подключаем твой AI через ngrok'
  $ru=@('Установить ngrok','Вставить ключ ngrok в окно на ПК','Разрешить подключение на этом ПК','Проверить ссылку','Подключить твой AI-клиент')
  for($i=0;$i -lt $steps.Count;$i++){$steps[$i].text=$ru[$i]}
  $Waiting=switch($Stage){1{'Открой dashboard.ngrok.com/get-started/your-authtoken, войди и вставь Authtoken в окно на ПК, не в чат.'};2{'Проверь окно подключения на этом ПК и нажми «Разрешить», если согласен.'};4{'Добавь ссылку MCP в свой AI-клиент и войди. Локальная проверка — ещё не подключение клиента.'};default{''}}
 }
 $null=Invoke-OnboardControl $Context 'progress' @{taskId=$Task;title=$title;waitingFor=$Waiting;steps=$steps}
}
function Assert-OnboardCandidate($State,[long]$Revision,[string]$Operation,[string]$ProfileId){
 if($State.revision -ne $Revision -or -not $State.serverEnabled -or -not $State.pending -or $State.pending.OperationId -ne $Operation -or $State.pending.profile.Id -ne $ProfileId -or $State.retainedPrevious){throw 'Setup state changed; do not adopt another operation'}
}
function Invoke-OnboardNgrok([string]$InstallDir,[bool]$OwnerChoseNgrok,[string]$NgrokPath=''){
 $phase='owner_choice';$ctx=$null;$operation='';$task='';$revision=-1
 try{
  if(-not $OwnerChoseNgrok){return @{status='waiting_for_owner';code='CHOOSE_CONNECTION';message='Explain internet access and provider limits; obtain the owner choice first.'}}
  $phase='installation';$ctx=Get-OnboardContext $InstallDir;$phase='preflight';$state=Invoke-OnboardControl $ctx 'state'
  if(-not $state.serverEnabled){return @{status='waiting_for_owner';code='ENABLE_MCP';ownerAction='Enable MCP in the tray. The helper never enables it.'}}
  if(-not (Test-OnboardFirstSetup $state $ctx)){return @{status='needs_attention';code='EXISTING_CONNECTION_OR_DRAFT';message='First setup from local only. Leave the existing connection and draft untouched.'}}
  if($state.guide.taskId -and $state.guide.updatedAt -and ([DateTimeOffset]::UtcNow-[DateTimeOffset]::Parse($state.guide.updatedAt)).TotalMinutes -lt 10){return @{status='needs_attention';code='OTHER_GUIDE_ACTIVE';message='Do not overwrite another active guide.'}}
  if(Get-Process -Name ngrok -ErrorAction SilentlyContinue){return @{status='needs_attention';code='NGROK_ALREADY_RUNNING';message='Leave the process untouched; use the advanced workflow.'}}
  $revision=[long]$state.revision;$task='setup-'+[Guid]::NewGuid().ToString('N');$profileId='ngrok-'+[Guid]::NewGuid().ToString('N').Substring(0,12)
  $phase='ngrok_install';Set-OnboardProgress $ctx $task 0;$binary=Get-OnboardNgrok $ctx $NgrokPath;$inspection=Get-OnboardInspectionPort
  $phase='provider_key';Set-OnboardProgress $ctx $task 1 'Open dashboard.ngrok.com/get-started/your-authtoken, sign in and paste the Authtoken in the PC window, not in chat.'
  $reason=if($ctx.Language -eq 'ru'){'Вставь Authtoken ngrok в это окно, не в чат. Это ключ провайдера, а не пароль MCP.'}else{'Paste the ngrok Authtoken here, not in chat. This is not an MCP password.'}
  $stored=Invoke-OnboardControl $ctx 'secret-request' @{kind='ngrok-authtoken';reason=$reason;apiPort=$inspection}
  if(-not $stored.stored){$null=Invoke-OnboardControl $ctx 'progress' @{taskId=$task;clear=$true};return @{status='cancelled';code='KEY_ENTRY_CANCELLED';activeConnectionChanged=$false}}
  if(-not [IO.Path]::IsPathRooted([string]$stored.configurationFile) -or -not (Test-Path -LiteralPath $stored.configurationFile -PathType Leaf)){throw 'Protected config not stored'}
  $state=Invoke-OnboardControl $ctx 'state';if($state.revision -ne $revision -or -not $state.serverEnabled -or -not (Test-OnboardFirstSetup $state $ctx)){throw 'State changed during key entry'}
  $profile=@{Id=$profileId;Name='ngrok';Scope='Public';Ownership='Managed';Adapter='ngrok';Endpoint='';AddressKind='stable';Launch=@{Executable=$binary;Arguments=@('http',('http://127.0.0.1:'+$ctx.Port),'--config',[string]$stored.configurationFile,'--inspect=false','--log=stdout','--log-format=json');WorkingDirectory=(Split-Path $binary -Parent);ConfigurationFile=[string]$stored.configurationFile}}
  $phase='prepare';$state=Invoke-OnboardControl $ctx 'prepare' @{revision=$revision;profile=$profile};$operation=[string]$state.pending.OperationId;$revision++
  if(-not $operation){throw 'Prepared operation missing'};Assert-OnboardCandidate $state $revision $operation $profileId
  $phase='owner_launch';Set-OnboardProgress $ctx $task 2 'Review and approve the connection window on this PC. No program starts before approval.'
  $state=Invoke-OnboardControl $ctx 'state';Assert-OnboardCandidate $state $revision $operation $profileId
  $state=Invoke-OnboardControl $ctx 'start' @{revision=$revision;operationId=$operation};Assert-OnboardCandidate $state $revision $operation $profileId
  $phase='discover_address';Set-OnboardProgress $ctx $task 3;$url=Get-OnboardNgrokUrl $ctx $inspection
  $state=Invoke-OnboardControl $ctx 'state';Assert-OnboardCandidate $state $revision $operation $profileId
  $phase='address';$state=Invoke-OnboardControl $ctx 'address' @{revision=$revision;operationId=$operation;endpoint=$url};$revision++;Assert-OnboardCandidate $state $revision $operation $profileId
  $phase='check';$state=Invoke-OnboardControl $ctx 'check' @{revision=$revision;operationId=$operation};Assert-OnboardCandidate $state $revision $operation $profileId
  $phase='activate';$state=Invoke-OnboardControl $ctx 'state';Assert-OnboardCandidate $state $revision $operation $profileId
  $state=Invoke-OnboardControl $ctx 'activate' @{revision=$revision;operationId=$operation}
  if($state.pending -or $state.revision -ne ($revision+1) -or $state.currentUrl -ne $url){throw 'Activation not confirmed; inspect before retrying'}
  $phase='client_handoff';Set-OnboardProgress $ctx $task 4 'Add the MCP URL in your AI client and complete sign-in. A local check is not a real client connection.'
  $note=if($ctx.Language -eq 'ru'){'Приложение запускает и выключает ngrok вместе с MCP. Ссылка для AI — в «Подключении», клиенты и отзыв доступа — в «Доступе». Локальная проверка ещё не означает, что AI-клиент подключён.'}else{'ngrok is managed by this app and stops with MCP. Connect AI using the URL in Connection; manage access in Access. A local URL check alone does not complete onboarding.'}
  $null=Invoke-OnboardControl $ctx 'note' @{text=$note}
  return @{status='awaiting_client';url=$url;taskId=$task;clientOnboardingComplete=$false;next='Connect the chosen AI client. Prefer OAuth; otherwise create a named scoped token in Access. Confirm actual client access before clearing progress. Never print tokens.'}
 }catch{return @{status='needs_attention';code=$phase;operationId=$operation;taskId=$task;message='Setup stopped without automatic retry or rollback. Inspect state; do not overwrite settings, adopt another draft, bypass consent, or print raw errors. A matching candidate may remain.'}}
 finally{if($ctx){$ctx.ControlSecret=$null}}
}
