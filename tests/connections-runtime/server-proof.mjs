import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import http from 'node:http';
import crypto from 'node:crypto';
import {spawn} from 'node:child_process';
const temp=fs.mkdtempSync(path.join(os.tmpdir(),'zeroide-proof-'));
const dependencies=path.resolve('server/node_modules');
if(!fs.existsSync(dependencies))throw Error('Run npm ci --omit=dev in server before this test');
const source=path.resolve('server/server.mjs');
fs.copyFileSync(source,path.join(temp,'server.mjs'));
fs.symlinkSync(dependencies,path.join(temp,'node_modules'),'junction');
const token=crypto.randomBytes(32).toString('hex'), secret=crypto.randomBytes(32).toString('hex');
const local='http://127.0.0.1:39079';
const env={...process.env,PORT:'39079',MCP_HOST:'127.0.0.1',MCP_TOKEN:token,TRAY_CONTROL_SECRET:secret,TRAY_CONTROL_PORT:'39113',TRAY_HEARTBEAT_SEC:'120',TRAY_HEARTBEAT_MISSES:'100',PROJECT_DIR:temp,MCP_LOG_DIR:path.join(temp,'logs'),MCP_PERMISSIONS_FILE:path.join(temp,'permissions.json'),MCP_TOKENS_FILE:path.join(temp,'tokens.json'),MCP_OAUTH_FILE:path.join(temp,'oauth.json'),MCP_CONFIG_FILE:path.join(temp,'nonexistent.env'),MCP_OAUTH_ISSUER:'',MCP_OAUTH_ENABLED:'true'};
let child,control;let count=0;
function check(v,name){if(!v)throw Error(name);console.log('PASS: '+name);count++;}
async function request(route,{headers={},method='GET',body}={}){return fetch(local+route,{headers,method,body,redirect:'manual',signal:AbortSignal.timeout(5000)});}
try{
 child=spawn(process.execPath,[path.join(temp,'server.mjs')],{env,cwd:temp,stdio:'ignore',windowsHide:true});
 let ready=false;for(let i=0;i<60;i++){try{if((await request('/health')).ok){ready=true;break;}}catch{}await new Promise(r=>setTimeout(r,150));}check(ready,'isolated server starts');
 const base=await(await request('/health')).json();check(Object.keys(base).sort().join(',')==='name,ok,version','health minimal even on direct localhost');
 for(const route of ['/local/status','/local/connection-proof?nonce='+'a'.repeat(64)]){check((await request(route)).status===403,route.split('?')[0]+' denies missing secret');check((await request(route,{headers:{'X-ZeroIDE-Control':secret,'X-Forwarded-Host':'example.test'}})).status===403,route.split('?')[0]+' denies forwarded requests');}
 check((await request('/local/status',{headers:{'X-ZeroIDE-Control':secret}})).ok,'tray status with control secret succeeds');
 const nonce=crypto.randomBytes(32).toString('hex');
 const expected=await(await request('/local/connection-proof?nonce='+nonce,{headers:{'X-ZeroIDE-Control':secret}})).json();
 const publicProof=await(await request('/mcp?zeroide-route-proof='+nonce,{headers:{Host:'public.example.test','X-Forwarded-Host':'public.example.test'}})).json();
 check(expected.proof===publicProof.proof&&/^[a-f0-9]{64}$/.test(expected.proof),'public route proves same per-boot server without credentials');
 const other=await(await request('/mcp?zeroide-route-proof='+'b'.repeat(64))).json();check(other.proof!==publicProof.proof,'nonce changes possession proof');
 check((await request('/mcp?zeroide-route-proof=invalid')).status===400,'invalid challenge rejected');
 const init={jsonrpc:'2.0',id:1,method:'initialize',params:{protocolVersion:'2025-03-26',capabilities:{},clientInfo:{name:'ZeroIDE connection verifier',version:'1'}}};
 check((await request('/mcp',{method:'POST',headers:{'Content-Type':'application/json',Accept:'application/json, text/event-stream'},body:JSON.stringify(init)})).status===401,'route proof never bypasses MCP authorization');
 const response=await request('/mcp',{method:'POST',headers:{'Content-Type':'application/json',Accept:'application/json, text/event-stream',Authorization:'Bearer '+token},body:JSON.stringify(init)});
 check(response.ok,'authenticated MCP initialize works');const text=await response.text();check(text.includes('serverInfo')&&text.includes('protocolVersion'),'MCP initialize returns actual protocol data');
 const session=response.headers.get('mcp-session-id');check(!!session,'MCP session created');
 const verifierNotice=await request('/mcp',{method:'POST',headers:{'Content-Type':'application/json',Accept:'application/json, text/event-stream',Authorization:'Bearer '+token,'Mcp-Session-Id':session,'MCP-Protocol-Version':'2025-03-26'},body:JSON.stringify({jsonrpc:'2.0',method:'notifications/initialized'})});
 check(verifierNotice.ok&&!fs.existsSync(path.join(temp,'connection-client.json')),'even initialized local verifier cannot impersonate real-client success');
 const close=await request('/mcp',{method:'DELETE',headers:{Authorization:'Bearer '+token,'Mcp-Session-Id':session,'MCP-Protocol-Version':'2025-03-26'}});check(close.ok,'verification session explicitly closed');
 const marker=path.join(temp,'connection-client.json');
 check(!fs.existsSync(marker),'local verifier initialize never creates real-client evidence');
 // Exercise the shipped Windows PowerShell helper against this real, isolated MCP server.
 fs.writeFileSync(path.join(temp,'config.env'),'MCP_TOKEN='+token+'\n');
 fs.writeFileSync(path.join(temp,'tray-config.json'),JSON.stringify({Port:39079,ControlPort:39113,ControlSecret:secret,ServerDir:temp}));
 let controlReads=0;
 control=http.createServer((req,res)=>{
  if(req.url!=='/connections/state'||req.method!=='GET'||req.headers['x-zeroide-control']!==secret){res.writeHead(403);res.end();return;}
  controlReads++;res.setHeader('Content-Type','application/json');res.end(JSON.stringify({serverEnabled:true}));
 });
 await new Promise((resolve,reject)=>{control.once('error',reject);control.listen(39113,'127.0.0.1',resolve);});
 const helper=path.resolve('tools/Test-LocalMcp.ps1');
 const tested=await new Promise((resolve,reject)=>{
  const p=spawn('powershell.exe',['-NoProfile','-ExecutionPolicy','Bypass','-File',helper,'-InstallDir',temp],{windowsHide:true,stdio:['ignore','pipe','pipe']});
  let out='',err='';const timer=setTimeout(()=>{p.kill();reject(Error('Local helper timed out'));},45000);
  p.stdout.on('data',b=>out+=b);p.stderr.on('data',b=>err+=b);
  p.once('error',e=>{clearTimeout(timer);reject(e);});p.once('exit',code=>{clearTimeout(timer);resolve({code,out,err});});
 });
 check(!tested.out.includes(token)&&!tested.err.includes(token)&&!tested.out.includes(secret)&&!tested.err.includes(secret),'real helper never prints local credentials');
 check(tested.code===0&&controlReads===1,'shipped helper authenticates to isolated control API');
 let helperResult;try{helperResult=JSON.parse(tested.out.replace(/^\uFEFF/,''));}catch{throw Error('Local helper did not return JSON');}
 check(helperResult.status==='local_ready'&&helperResult.toolCount===14&&helperResult.sessionClosed===true,'real helper completes initialize, tools/list and session cleanup');
 check(helperResult.clientOnboardingComplete===false&&!fs.existsSync(marker),'real helper cannot manufacture client-onboarding evidence');
 await new Promise(resolve=>control.close(resolve));control=null;
 const h={'Content-Type':'application/json',Accept:'application/json, text/event-stream',Authorization:'Bearer '+token,'X-Forwarded-Host':'client.example.test'};
 const realInit={...init,id:2,params:{...init.params,clientInfo:{name:'real-test-client',version:'1'}}};
 const real=await request('/mcp',{method:'POST',headers:h,body:JSON.stringify(realInit)});await real.text();check(real.ok,'actual client initialized');
 check(!fs.existsSync(marker),'initialize response alone is not completed client onboarding');
 const realSession=real.headers.get('mcp-session-id');
 const initialized=await request('/mcp',{method:'POST',headers:{...h,'Mcp-Session-Id':realSession,'MCP-Protocol-Version':'2025-03-26'},body:JSON.stringify({jsonrpc:'2.0',method:'notifications/initialized'})});
 check(initialized.ok,'client initialized notification accepted');
 const evidence=JSON.parse(fs.readFileSync(marker,'utf8'));check(evidence.host==='client.example.test'&&evidence.path==='/mcp'&&!!evidence.at,'server records exact client address without credentials');
 check(!fs.readFileSync(marker,'utf8').includes(token),'client evidence has no authorization token');
 await request('/mcp',{method:'DELETE',headers:{Authorization:'Bearer '+token,'Mcp-Session-Id':realSession,'MCP-Protocol-Version':'2025-03-26'}});
 console.log('TOTAL SERVER: '+count+' passed; production untouched');
}finally{if(control){await new Promise(r=>control.close(r));}if(child&&child.exitCode===null&&child.signalCode===null){const ended=new Promise(r=>child.once('exit',r));child.kill();await ended;}fs.rmSync(path.join(temp,'node_modules'));fs.rmSync(temp,{recursive:true,force:true});}
