# Isolated unit tests: no real tray, provider, downloads or production configuration.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'tools\Onboarding.Common.ps1')
. (Join-Path $root 'tools\Onboarding.Ngrok.ps1')
$count=0
function Check($ok,[string]$Name){if(-not $ok){throw ('FAIL: '+$Name)};$script:count++;Write-Host ('PASS: '+$Name)}
$temp=Join-Path ([IO.Path]::GetTempPath()) ('zeroide-onboard-test-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temp)
$fakeToken='UNIT-TOKEN-DO-NOT-PRINT';$fakeControl='UNIT-CONTROL-DO-NOT-PRINT'
@{Port=39081;ControlPort=39082;ControlSecret=$fakeControl;ServerDir=$temp}|ConvertTo-Json|Set-Content (Join-Path $temp 'tray-config.json') -Encoding UTF8
[IO.File]::WriteAllText((Join-Path $temp 'config.env'),('MCP_TOKEN='+$fakeToken))
$protected=Join-Path $temp 'protected.yml';[IO.File]::WriteAllText($protected,'UNIT-PROVIDER-DO-NOT-PRINT')
$originalSignature=${function:Assert-OnboardNgrokSignature}
$originalUrl=${function:Get-OnboardNgrokUrl}
$originalGetNgrok=${function:Get-OnboardNgrok}
function Reset-Fixture {
 $script:state=[pscustomobject]@{revision=0;serverEnabled=$true;currentUrl='http://127.0.0.1:39081/mcp';pending=$null;retainedPrevious=$false;guide=$null}
 $script:calls=[Collections.Generic.List[string]]::new();$script:bodies=[Collections.Generic.List[object]]::new()
 $script:mode='';$script:foreignProcess=$false;$script:checkMode='json';$script:cleanupAuthorized=$false;$script:httpCalls=[Collections.Generic.List[string]]::new()
}
function Get-Process {param($Name,$ErrorAction) if($script:foreignProcess){[pscustomobject]@{Id=999999}}}
function Get-OnboardNgrok {param($Context,$NgrokPath) return (Join-Path $temp 'ngrok.exe')}
function Get-OnboardInspectionPort {return 4042}
function Get-OnboardNgrokUrl {param($Context,$InspectionPort) return 'https://fixture.ngrok-free.dev/mcp'}
function Invoke-OnboardControl {
 param($Context,$Action,$Body)
 $script:calls.Add($Action);$script:bodies.Add($Body)
 if($Action -eq 'state'){return ($script:state|ConvertTo-Json -Depth 20|ConvertFrom-Json)}
 if($script:mode -eq $Action){throw ('RAW SECRET '+$fakeToken+' '+$fakeControl)}
 switch($Action){
  'progress' {return @{ok=$true}}
  'note' {return @{ok=$true}}
  'secret-request' {if($script:mode -eq 'cancel-key'){return @{stored=$false}};if($script:mode -eq 'stale-key'){$script:state.revision++};return @{stored=$true;configurationFile=$protected}}
  'prepare' {$script:state.revision++;$script:state.pending=@{OperationId='fixture-operation';profile=$Body.profile};if($script:mode -eq 'wrong-operation'){$script:state.pending.OperationId=''};break}
  'start' {if($script:mode -eq 'stale-start'){$script:state.revision++};break}
  'address' {$script:state.revision++;$script:state.pending.profile.Endpoint=$Body.endpoint;break}
  'check' {break}
  'activate' {$script:state.revision++;$script:state.currentUrl=$script:state.pending.profile.Endpoint;$script:state.pending=$null;break}
  default {throw 'Unexpected action'}
 }
 return ($script:state|ConvertTo-Json -Depth 20|ConvertFrom-Json)
}
$required=@('user_message','task_status','take_screenshot','run_command','job_status','job_output','read_file','list_directory','search_files','write_file','edit_file','audit_log','journal_entry','journal_log')
function Invoke-OnboardHttp {
 param($Port,$Path,$Method='GET',$Headers,$Body,$TimeoutSec)
 $script:httpCalls.Add([string]$(if($Method -eq 'DELETE'){'DELETE'}else{$Body.method}))
 if($Method -eq 'DELETE'){$script:cleanupAuthorized=($Headers['Mcp-Session-Id'] -eq 'fixture-session' -and $Headers.Authorization -eq ('Bearer '+$fakeToken));return @{Status=200;Text='';Session=''}}
 if($Body.method -eq 'initialize'){
  if($script:checkMode -eq 'redirect'){return @{Status=302;Text='';Session=''}}
  $version=if($script:checkMode -eq 'protocol'){'invalid'}else{'2025-03-26'}
  $message=@{jsonrpc='2.0';id=1;result=@{protocolVersion=$version;serverInfo=@{name='fixture'}}}|ConvertTo-Json -Compress -Depth 8
 }elseif($Body.method -eq 'notifications/initialized'){return @{Status=202;Text='';Session=''}}
 elseif($Body.method -eq 'tools/list'){
  $tools=if($script:checkMode -eq 'missing'){@(@{name='read_file'})}else{@($required|ForEach-Object {@{name=$_}})}
  $message=@{jsonrpc='2.0';id=2;result=@{tools=$tools}}|ConvertTo-Json -Depth 8 -Compress
 }else{throw 'Unexpected MCP method'}
 if($script:checkMode -eq 'sse'){$message="event: message`ndata: $message`n`n"}
 return @{Status=200;Text=$message;Session='fixture-session'}
}
try {
 foreach($name in @('Onboarding.Common.ps1','Onboarding.Ngrok.ps1','Setup-Ngrok.ps1','Test-LocalMcp.ps1')){
  $e=$null;$t=$null;[void][Management.Automation.Language.Parser]::ParseFile((Join-Path $root ('tools\'+$name)),[ref]$t,[ref]$e)
  Check (@($e).Count -eq 0) ($name+' parses in Windows PowerShell')
 }
 Reset-Fixture;$r=Invoke-OnboardNgrok 'nonexistent' $false
 Check ($r.code -eq 'CHOOSE_CONNECTION' -and $calls.Count -eq 0) 'owner chooses before configuration access'
 Reset-Fixture;$state.serverEnabled=$false;$r=Invoke-OnboardNgrok $temp $true
 Check ($r.code -eq 'ENABLE_MCP' -and ($calls -join ',') -eq 'state') 'helper never enables server for owner'
 foreach($variant in @('public','pending','previous','fragment','foreign-port','userinfo')){
  Reset-Fixture
  switch($variant){'public'{$state.currentUrl='https://existing.example/mcp'};'pending'{$state.pending=@{OperationId='other'}};'previous'{$state.retainedPrevious=$true};'fragment'{$state.currentUrl+='\#bad';$state.currentUrl=$state.currentUrl.Replace('\#','#')};'foreign-port'{$state.currentUrl='http://127.0.0.1:39099/mcp'};'userinfo'{$state.currentUrl='http://user@127.0.0.1:39081/mcp'}}
  $r=Invoke-OnboardNgrok $temp $true
  Check ($r.code -eq 'EXISTING_CONNECTION_OR_DRAFT' -and $calls.Count -eq 1) ('fresh-only guard: '+$variant)
 }
 Reset-Fixture;$state.guide=@{taskId='another';updatedAt=[DateTimeOffset]::UtcNow.ToString('o')};$r=Invoke-OnboardNgrok $temp $true
 Check ($r.code -eq 'OTHER_GUIDE_ACTIVE' -and $calls.Count -eq 1) 'another active guide is not overwritten'
 Reset-Fixture;$foreignProcess=$true;$r=Invoke-OnboardNgrok $temp $true
 Check ($r.code -eq 'NGROK_ALREADY_RUNNING' -and $calls.Count -eq 1) 'existing ngrok is neither killed nor adopted'
 Reset-Fixture;$mode='cancel-key';$r=Invoke-OnboardNgrok $temp $true
 Check ($r.status -eq 'cancelled' -and 'prepare' -notin $calls -and 'start' -notin $calls) 'cancelled key entry never prepares or launches'
 Reset-Fixture;$mode='stale-key';$r=Invoke-OnboardNgrok $temp $true
 Check ($r.status -eq 'needs_attention' -and 'prepare' -notin $calls) 'revision change during key entry stops before prepare'
 Reset-Fixture;$r=Invoke-OnboardNgrok $temp $true
 Check ($r.status -eq 'awaiting_client' -and -not $r.clientOnboardingComplete) 'automated setup does not impersonate real-client success'
 $actions=@($calls|Where-Object {$_ -in @('prepare','start','address','check','activate')})
 Check (($actions -join ',') -eq 'prepare,start,address,check,activate') 'one transactional coordinator sequence, no shell launch'
 $cas=@();for($i=0;$i -lt $calls.Count;$i++){if($calls[$i] -in @('prepare','start','address','check','activate')){$cas+=$bodies[$i].revision}}
 Check (($cas -join ',') -eq '0,1,1,2,2' -and $state.revision -eq 3) 'CAS revisions follow coordinator contract'
 $plan=$bodies[$calls.IndexOf('prepare')].profile.Launch
 Check ($plan.ConfigurationFile -eq $protected -and $plan.Arguments -contains $protected -and ($plan|ConvertTo-Json -Depth 8) -notmatch 'UNIT-PROVIDER') 'launch uses protected config path, never provider secret'
 Check (($r|ConvertTo-Json) -notmatch 'UNIT-(TOKEN|CONTROL|PROVIDER)') 'success output contains no credentials'
 $progress=@();for($i=0;$i -lt $calls.Count;$i++){if($calls[$i] -eq 'progress'){$progress+=$bodies[$i]}}
 Check ($progress.Count -eq 5 -and $progress[-1].steps[4].owner -and $progress[-1].steps[4].state -eq 'current') 'progress ends at owner client handoff, not false completion'
 foreach($failure in @('prepare','start','address','check','activate','stale-start','wrong-operation')){
  Reset-Fixture;$mode=$failure;$r=Invoke-OnboardNgrok $temp $true
  Check ($r.status -eq 'needs_attention' -and 'cancel' -notin $calls -and (@($calls|Where-Object {$_ -eq 'activate'}).Count -le 1)) ('no blind retries or rollback after '+$failure)
  Check ($state.currentUrl -eq 'http://127.0.0.1:39081/mcp' -and ($r|ConvertTo-Json) -notmatch 'UNIT-(TOKEN|CONTROL|PROVIDER)') ('active link and secret isolation after '+$failure)
 }
 foreach($encoding in @('json','sse')){
  Reset-Fixture;$checkMode=$encoding;$r=Invoke-OnboardLocalCheck $temp
  Check ($r.status -eq 'local_ready' -and $r.toolCount -eq 14 -and $r.sessionClosed -and -not $r.clientOnboardingComplete) ('local MCP '+$encoding+' handshake and explicit cleanup')
  Check (($httpCalls -join ',') -eq 'initialize,notifications/initialized,tools/list,DELETE') ('local check protocol order: '+$encoding)
 }
 foreach($failure in @('protocol','missing','redirect')){
  Reset-Fixture;$checkMode=$failure;$r=Invoke-OnboardLocalCheck $temp
  Check ($r.status -eq 'failed' -and ($r|ConvertTo-Json) -notmatch 'UNIT-') ('local verifier fails closed: '+$failure)
  if($failure -ne 'redirect'){Check ($httpCalls -contains 'DELETE' -and $cleanupAuthorized) ('failed session cleaned: '+$failure)}
 }
 Reset-Fixture;$state.serverEnabled=$false;$r=Invoke-OnboardLocalCheck $temp
 Check ($r.code -eq 'ENABLE_MCP' -and $httpCalls.Count -eq 0) 'local verifier waits for owner without calling disabled server'
 Reset-Fixture;[IO.File]::WriteAllText((Join-Path $temp 'config.env'),'MCP_TOKEN=');$r=Invoke-OnboardLocalCheck $temp
 Check ($r.status -eq 'failed' -and $r.code -eq 'local_authorization' -and $httpCalls.Count -eq 0) 'missing bearer never falls back to anonymous verification'
 function Get-AuthenticodeSignature {param($LiteralPath) return $script:signature}
 $signature=@{Status='Valid';SignerCertificate=@{Subject='CN="ngrok, Inc.", O="ngrok, Inc.", L=San Francisco'}}
 & $originalSignature 'fixture.exe';Check $true 'official signed ngrok publisher accepted'
 foreach($sig in @(@{Status='NotSigned';SignerCertificate=$null},@{Status='Valid';SignerCertificate=@{Subject='CN=Impostor Inc.'}})){
  $signature=$sig;$refused=$false;try{& $originalSignature 'fixture.exe'}catch{$refused=$true};Check $refused 'unsigned or foreign-publisher ngrok rejected'
 }
 Reset-Fixture
 function Invoke-OnboardHttp {param($Port,$Path,$TimeoutSec) return @{Status=200;Text='{"tunnels":[{"config":{"addr":"http://127.0.0.1:39081"},"public_url":"https://fixture.ngrok-free.dev"}]}'}}
 $url=& $originalUrl @{Port=39081} 4042
 Check ($url -eq 'https://fixture.ngrok-free.dev/mcp') 'assigned URL discovered from exact local upstream'
 Reset-Fixture;Set-OnboardProgress @{Language='ru'} 'language-fixture' 1
 $ru=$bodies[0]
 Check ([int][char]$ru.steps[0].text[0] -eq 1059 -and $ru.waitingFor.Contains('dashboard.ngrok.com')) 'Russian progress survives Windows PowerShell 5.1 decoding'
 Check ($ru.steps[1].owner -and $ru.steps[1].state -eq 'current' -and $ru.waitingFor.Length -le 200) 'localized progress preserves owner action and API limits'
 Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
 $script:downloadCalls=0;$script:signatureCalls=0;$script:allowSignature=$true
 function Assert-OnboardNgrokSignature {param($Path) $script:signatureCalls++;if(-not $script:allowSignature){throw 'Mock unsigned binary'}}
 function Save-OnboardNgrokDownload {
  param($Destination)
  $script:downloadCalls++;$z=[IO.Compression.ZipFile]::Open($Destination,[IO.Compression.ZipArchiveMode]::Create)
  try{$entry=$z.CreateEntry('ngrok.exe');$stream=$entry.Open();try{$bytes=New-Object byte[] 2048;$stream.Write($bytes,0,$bytes.Length)}finally{$stream.Dispose()};$entry=$z.CreateEntry('../escape.txt');$stream=$entry.Open();$stream.Dispose()}finally{$z.Dispose()}
 }
 $downloadRoot=Join-Path $temp 'download-fixture'
 $binary=& $originalGetNgrok @{Root=$downloadRoot}
 Check ((Test-Path -LiteralPath $binary) -and $downloadCalls -eq 1 -and $signatureCalls -eq 1) 'new executable published only after signature check'
 Check (-not (Test-Path (Join-Path $downloadRoot 'tools\ngrok\escape.txt')) -and -not (Test-Path (Join-Path $downloadRoot 'tools\escape.txt'))) 'ZIP extracts only exact executable entry, not traversal entries'
 Check (@(Get-ChildItem (Split-Path $binary) -Directory -Filter '.download-*').Count -eq 0) 'private download staging removed after success'
 $allowSignature=$false;$refused=$false;try{& $originalGetNgrok @{Root=$downloadRoot}}catch{$refused=$true}
 Check ($refused -and $downloadCalls -eq 1 -and (Test-Path -LiteralPath $binary)) 'untrusted existing binary refused without overwrite or download'
 Write-Host ('TOTAL ONBOARDING: '+$count+' passed; production untouched')
}finally{if(Test-Path -LiteralPath $temp){Remove-Item -LiteralPath $temp -Recurse -Force}}
