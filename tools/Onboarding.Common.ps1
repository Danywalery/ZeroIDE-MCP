# Windows PowerShell 5.1. Loading defines functions only; no installation or network calls.
Add-Type -AssemblyName System.Net.Http
function Get-OnboardContext([string]$InstallDir) {
 $root=[IO.Path]::GetFullPath($InstallDir)
 $cfg=[IO.File]::ReadAllText((Join-Path $root 'tray-config.json'))|ConvertFrom-Json
 if([string]::IsNullOrWhiteSpace($cfg.ControlSecret)){throw 'Missing local control credential'}
 if($cfg.Port -lt 1024 -or $cfg.Port -gt 65535 -or $cfg.ControlPort -lt 1024 -or $cfg.ControlPort -gt 65535){throw 'Invalid ports'}
 return @{Root=$root;ServerDir=$cfg.ServerDir;Port=[int]$cfg.Port;ControlPort=[int]$cfg.ControlPort;ControlSecret=[string]$cfg.ControlSecret;Language=[string]$cfg.Language}
}
function Invoke-OnboardHttp {
 param([int]$Port,[string]$Path,[string]$Method='GET',[hashtable]$Headers=@{},[object]$Body=$null,[int]$TimeoutSec=30)
 if($Port -lt 1024 -or $Port -gt 65535 -or $Path -notmatch '^/[^\r\n]*$' -or $Path.StartsWith('//')){throw 'Invalid local request'}
 $handler=[Net.Http.HttpClientHandler]::new();$handler.AllowAutoRedirect=$false;$handler.UseProxy=$false
 $client=[Net.Http.HttpClient]::new($handler);$client.Timeout=[Threading.Timeout]::InfiniteTimeSpan
 $cancel=[Threading.CancellationTokenSource]::new($TimeoutSec*1000)
 $request=[Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($Method),('http://127.0.0.1:'+$Port+$Path))
 $response=$null;$stream=$null;$memory=[IO.MemoryStream]::new()
 try {
  foreach($key in $Headers.Keys){[void]$request.Headers.TryAddWithoutValidation($key,[string]$Headers[$key])}
  if($null -ne $Body){$json=$Body|ConvertTo-Json -Depth 20 -Compress;$request.Content=[Net.Http.StringContent]::new($json,[Text.Encoding]::UTF8,'application/json')}
  $response=$client.SendAsync($request,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$cancel.Token).GetAwaiter().GetResult()
  $stream=$response.Content.ReadAsStreamAsync().GetAwaiter().GetResult();$buffer=New-Object byte[] 8192
  while(($n=$stream.ReadAsync($buffer,0,$buffer.Length,$cancel.Token).GetAwaiter().GetResult()) -gt 0){if($memory.Length+$n -gt 1048576){throw 'Response too large'};$memory.Write($buffer,0,$n)}
  $session='';if($response.Headers.Contains('Mcp-Session-Id')){$session=[string](@($response.Headers.GetValues('Mcp-Session-Id'))[0])}
  return @{Status=[int]$response.StatusCode;Text=[Text.Encoding]::UTF8.GetString($memory.ToArray());Session=$session}
 } finally {if($stream){$stream.Dispose()};if($response){$response.Dispose()};$memory.Dispose();$request.Dispose();$cancel.Dispose();$client.Dispose();$handler.Dispose()}
}
function Invoke-OnboardControl([hashtable]$Context,[string]$Action,[object]$Body=$null){
 $method=if($Action -eq 'state'){'GET'}else{'POST'}
 $r=Invoke-OnboardHttp -Port $Context.ControlPort -Path ('/connections/'+$Action) -Method $method -Headers @{'X-ZeroIDE-Control'=$Context.ControlSecret} -Body $Body -TimeoutSec 600
 if($r.Status -ne 200){throw 'Coordinator rejected request; inspect state before retrying'}
 return ($r.Text|ConvertFrom-Json)
}
function ConvertFrom-OnboardMcp([string]$Text,[int]$Id){
 $payloads=if($Text.TrimStart().StartsWith('{')){@($Text)}else{@($Text -split '\r?\n'|Where-Object {$_.StartsWith('data:')}|ForEach-Object {$_.Substring(5).Trim()})}
 foreach($payload in $payloads){if(-not $payload -or $payload -eq '[DONE]'){continue};$message=$payload|ConvertFrom-Json;if($message.id -eq $Id){if($message.error){throw 'MCP protocol error'};return $message}}
 throw 'Expected MCP result missing'
}
function Invoke-OnboardLocalCheck([string]$InstallDir){
 $phase='installation';$ctx=$null;$headers=$null;$session='';$closed=$false
 try {
  $ctx=Get-OnboardContext $InstallDir;$phase='owner_enable';$state=Invoke-OnboardControl $ctx 'state'
  if(-not $state.serverEnabled){return @{status='waiting_for_owner';code='ENABLE_MCP';ownerAction='Enable MCP in the tray, then rerun this check. Never enable it for the owner.'}}
  $phase='local_authorization';$token=''
  foreach($line in [IO.File]::ReadAllLines((Join-Path $ctx.ServerDir 'config.env'))){if($line -match '^\s*MCP_TOKEN=(.*)$'){$token=$Matches[1].Trim().Trim([char]34,[char]39);break}}
  if([string]::IsNullOrWhiteSpace($token)){throw 'Local credential required'}
  $headers=@{Authorization=('Bearer '+$token);Accept='application/json, text/event-stream'};$phase='initialize'
  $r=Invoke-OnboardHttp -Port $ctx.Port -Path '/mcp' -Method POST -Headers $headers -Body @{jsonrpc='2.0';id=1;method='initialize';params=@{protocolVersion='2025-03-26';capabilities=@{};clientInfo=@{name='ZeroIDE connection verifier';version='1.0'}}}
  if($r.Status -ne 200){throw 'MCP initialize rejected'}
  $session=$r.Session;if($session -notmatch '^[A-Za-z0-9_-]{1,128}$'){throw 'Invalid MCP session'}
  $headers['Mcp-Session-Id']=$session;$headers['MCP-Protocol-Version']='2025-03-26'
  $init=ConvertFrom-OnboardMcp $r.Text 1
  if($init.result.protocolVersion -ne '2025-03-26' -or -not $init.result.serverInfo){throw 'Unexpected protocol response'}
  $phase='initialized'
  $r=Invoke-OnboardHttp -Port $ctx.Port -Path '/mcp' -Method POST -Headers $headers -Body @{jsonrpc='2.0';method='notifications/initialized'}
  if($r.Status -notin @(200,202,204)){throw 'Initialized notification rejected'}
  $phase='tools_list';$r=Invoke-OnboardHttp -Port $ctx.Port -Path '/mcp' -Method POST -Headers $headers -Body @{jsonrpc='2.0';id=2;method='tools/list';params=@{}}
  if($r.Status -ne 200){throw 'Tools list rejected'}
  $list=ConvertFrom-OnboardMcp $r.Text 2
  $required=@('user_message','task_status','take_screenshot','run_command','job_status','job_output','read_file','list_directory','search_files','write_file','edit_file','audit_log','journal_entry','journal_log')
  $names=@($list.result.tools|ForEach-Object {$_.name})
  if(@($required|Where-Object {$_ -notin $names}).Count){throw 'Required tools missing'}
  $phase='session_cleanup';$r=Invoke-OnboardHttp -Port $ctx.Port -Path '/mcp' -Method DELETE -Headers $headers
  if($r.Status -notin @(200,202,204)){throw 'Session cleanup rejected'};$closed=$true
  return @{status='local_ready';toolCount=$names.Count;sessionClosed=$true;clientOnboardingComplete=$false;next='Continue in the chosen AI client; the local verifier is not a real client sign-in.'}
 }catch{return @{status='failed';code=$phase;message='Local verification stopped. Do not display raw errors or credentials, or modify product files.'}}
 finally{if($session -and -not $closed -and $headers -and $ctx){try{$null=Invoke-OnboardHttp -Port $ctx.Port -Path '/mcp' -Method DELETE -Headers $headers -TimeoutSec 5}catch{}};$token=$null;$headers=$null;if($ctx){$ctx.ControlSecret=$null}}
}
