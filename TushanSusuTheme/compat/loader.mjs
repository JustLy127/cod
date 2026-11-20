import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
import {fileURLToPath} from 'node:url';
import {execFile} from 'node:child_process';
import {promisify} from 'node:util';

const execFileAsync=promisify(execFile);
const here=path.dirname(fileURLToPath(import.meta.url));
const root=path.dirname(here),themeRoot=path.join(root,'theme'),stateRoot=path.join(root,'runtime-data');
await fs.mkdir(stateRoot,{recursive:true});
const statusPath=path.join(stateRoot,'compat-status.json');
const disabled=path.join(stateRoot,'compat-disabled');
const args=new Set(process.argv.slice(2));
const restore=args.has('--restore'),probeOnly=args.has('--probe'),watch=args.has('--watch');
const appearancePatch=await fs.readFile(path.join(here,'modern-appearance.js'),'utf8');
const appearanceVersion=6;
const artifact=JSON.parse(await fs.readFile(path.join(here,'adapter-manifest.json'),'utf8'));
const digest=crypto.createHash('sha256').update(artifact.adapterId+'\n'+artifact.probeExpression+'\n'+artifact.source).digest('hex');
if(digest!=='2a6c596810108fc5b5b14fb37c64acdcab8affa467404b06dfc2fe3162748f62'||artifact.sha256!==digest)throw Error('Reviewed legacy cleanup adapter hash mismatch');
const powershell=path.join(process.env.SystemRoot??'C:\\Windows','System32','WindowsPowerShell','v1.0','powershell.exe');
const pause=ms=>new Promise(resolve=>setTimeout(resolve,ms));
const log=async(event,detail={})=>fs.appendFile(path.join(stateRoot,'compat.log'),JSON.stringify({at:new Date().toISOString(),event,...detail})+'\n').catch(()=>{});
let lastStatusAt=0,lastStatusKey='';
async function status(data,force=false){
  const summary=JSON.stringify({state:data.state,eligible:data.eligiblePages,verified:data.verifiedPages,reports:data.reports?.filter(r=>r.error||r.applied||r.restored)});
  if(!force&&summary===lastStatusKey&&Date.now()-lastStatusAt<30000)return;
  const temporary=statusPath+'.'+process.pid+'.'+crypto.randomBytes(4).toString('hex')+'.tmp';
  await fs.writeFile(temporary,JSON.stringify({at:new Date().toISOString(),...data},null,2));
  await fs.rename(temporary,statusPath);lastStatusAt=Date.now();lastStatusKey=summary;
}
let ownersCache=[],discoveredAt=0;
async function discover(force=false){
  if(!force&&ownersCache.length&&Date.now()-discoveredAt<120000)return ownersCache;
  const {stdout}=await execFileAsync(powershell,['-NoProfile','-ExecutionPolicy','Bypass','-File',path.join(here,'Discover-Codex.ps1')],{windowsHide:true,timeout:15000,maxBuffer:2*1024*1024});
  const result=JSON.parse(stdout.replace(/^\uFEFF/,''));
  ownersCache=Array.isArray(result)?result:[result];discoveredAt=Date.now();return ownersCache;
}
async function connect(target,port){
  const url=new URL(target.webSocketDebuggerUrl);
  if(url.protocol!=='ws:'||url.hostname!=='127.0.0.1'||Number(url.port)!==port||url.username||url.password||url.hash)throw Error('Unsafe debugger endpoint');
  const ws=new WebSocket(url);let sequence=0;const pending=new Map();
  await new Promise((resolve,reject)=>{const timer=setTimeout(()=>reject(Error('Connection timeout')),5000);ws.addEventListener('open',()=>{clearTimeout(timer);resolve()},{once:true});ws.addEventListener('error',()=>{clearTimeout(timer);reject(Error('Connection failed'))},{once:true})});
  ws.addEventListener('message',async event=>{
    const message=JSON.parse(typeof event.data==='string'?event.data:await event.data.text());
    const item=pending.get(message.id);if(!item)return;
    pending.delete(message.id);clearTimeout(item.timer);message.error?item.reject(Error(JSON.stringify(message.error))):item.resolve(message.result);
  });
  ws.addEventListener('close',()=>{discoveredAt=0;for(const item of pending.values()){clearTimeout(item.timer);item.reject(Error('Connection closed'))}pending.clear()});
  return {close:()=>ws.close(),evaluate:expression=>new Promise((resolve,reject)=>{
    const id=++sequence;const timer=setTimeout(()=>{pending.delete(id);reject(Error('Evaluation timeout'))},15000);
    pending.set(id,{timer,reject,resolve:result=>result.exceptionDetails?reject(Error(result.exceptionDetails.exception?.description??result.exceptionDetails.text)):resolve(result.result?.value)});
    ws.send(JSON.stringify({id,method:'Runtime.evaluate',params:{expression,returnByValue:true,awaitPromise:true}}));
  })};
}
let payload;
async function themePayload(){
  if(payload)return payload;
  const document=JSON.parse(await fs.readFile(path.join(themeRoot,'theme.json'),'utf8'));
  if(document.id!=='tushan-susu-blossom'||document.version!=='1.0.0')throw Error('Unexpected theme identity');
  const assetName=document.assets.background;
  if(!/^assets\/[A-Za-z0-9][A-Za-z0-9._/-]{0,240}$/.test(assetName)||assetName.split('/').includes('..'))throw Error('Invalid theme asset path');
  const canonical=(await fs.realpath(themeRoot)).toLowerCase()+path.sep;
  const file=await fs.realpath(path.join(themeRoot,...assetName.split('/')));
  if(!file.toLowerCase().startsWith(canonical))throw Error('Theme asset escapes package');
  const asset=await fs.readFile(file);if(asset.length>16*1024*1024)throw Error('Theme asset too large');
  return payload={asset};
}
const shellProbe=String.raw`(()=>({
  mainApp:Boolean(document.querySelector('aside.app-shell-left-panel')&&document.querySelector('main:is([data-app-shell-main-surface],[class*="_MainContentSurface_"],[class^="MainContentSurface-"],[class*=" MainContentSurface-"])')),
  version:window.__susuModernAppearanceCompatibility?.version??0,
  ready:Boolean(document.documentElement?.hasAttribute('data-susu-runtime-appearance')&&document.querySelector('style[data-susu-modern-appearance]')?.isConnected),
  legacy:Boolean(document.querySelector('[data-open-chatgpt-skin="theme"]')||window.__susuModernComposerCompatibility||window.__openChatgptSkinSurfaceObserver),
  owned:Boolean(document.documentElement?.hasAttribute('data-susu-runtime-appearance')||document.querySelector('[data-open-chatgpt-skin="theme"]'))
}))()`;
async function cleanup(c){
  await c.evaluate('(()=>{window.__susuModernComposerCompatibility?.restore();window.__susuModernAppearanceCompatibility?.restore();delete window.__susuModernAppearanceAsset;return true})()');
  const hasLegacy=await c.evaluate('Boolean(window.__openChatgptSkinSurfaceObserver||document.querySelector(\'[data-open-chatgpt-skin="theme"]\'))');
  if(hasLegacy){
    if(await c.evaluate(artifact.source)!==true)throw Error('Legacy cleanup adapter unavailable');
    const expression=await c.evaluate("globalThis.__openChatGPTSkinAdapter.source('remove')");
    if(typeof expression!=='string')throw Error('Legacy cleanup expression missing');
    await c.evaluate(expression);
  }
  return c.evaluate('!document.documentElement.hasAttribute("data-susu-runtime-appearance")&&!window.__susuModernAppearanceCompatibility&&!window.__susuModernComposerCompatibility&&!window.__openChatgptSkinSurfaceObserver&&!document.querySelector("[data-open-chatgpt-skin=\\"theme\\"]")');
}
async function apply(c,probe){
  try{
  if((probe.version||probe.legacy||probe.owned)&&await cleanup(c)!==true)throw Error('Previous theme cleanup incomplete');
  const {asset}=await themePayload();
  await c.evaluate("window.__susuModernAppearanceAsset='data:image/png;base64,';true");
  const encoded=asset.toString('base64');
  for(let offset=0;offset<encoded.length;offset+=192*1024)await c.evaluate('window.__susuModernAppearanceAsset+='+JSON.stringify(encoded.slice(offset,offset+192*1024))+';true');
  if(await c.evaluate(appearancePatch)!==true)throw Error('Native CSS theme application failed');
  const verification=await c.evaluate('window.__susuModernAppearanceCompatibility.verify()');
  if(verification?.valid!==true)throw Error('Native theme verification failed: '+JSON.stringify(verification));
  return verification;
  }catch(error){await cleanup(c).catch(()=>{});throw error}
}
const connections=new Map();
async function tick(){
  const owners=await discover(),seen=new Set(),reports=[];let eligible=0,verified=0,failed=false;
  const stop=restore||await fs.access(disabled).then(()=>true,()=>false);
  for(const owner of owners){
    if(!Number.isInteger(owner.port)||!Number.isInteger(owner.pid))continue;
    let targets;
    try{
      const response=await fetch('http://127.0.0.1:'+owner.port+'/json/list',{signal:AbortSignal.timeout(5000)});
      if(!response.ok)throw Error('Target discovery failed');targets=await response.json();
      if(!Array.isArray(targets))throw Error('Invalid target list');
    }catch(error){failed=true;discoveredAt=0;ownersCache=[];reports.push({error:error.message});continue}
    for(const target of targets){
      if(target.type!=='page'||!target.url?.startsWith('app://-/')||!target.webSocketDebuggerUrl)continue;
      const key=owner.pid+':'+target.id;seen.add(key);let connection=connections.get(key);
      try{
        if(!connection){connection=await connect(target,owner.port);connections.set(key,connection)}
        const probe=await connection.evaluate(shellProbe);
        if(stop){
          if(probe.owned||probe.legacy||probe.version){const clean=await cleanup(connection);if(!clean)throw Error('Theme cleanup incomplete');reports.push({target:target.id,restored:true});await log('native-theme-restored',{target:target.id})}
          continue;
        }
        if(!probe.mainApp)continue;eligible++;
        if(probeOnly){reports.push({target:target.id,mainApp:true,version:probe.version});continue}
        if(probe.version===appearanceVersion&&probe.ready&&!probe.legacy){verified++;continue}
        const verification=await apply(connection,probe);verified++;
        reports.push({target:target.id,applied:true,verification});await log('native-theme-applied',{target:target.id,verification});
      }catch(error){failed=true;connection?.close();connections.delete(key);reports.push({target:target.id,error:error.message});await log('target-error',{target:target.id,error:error.message})}
    }
  }
  for(const [key,connection]of connections)if(!seen.has(key)){connection.close();connections.delete(key)}
  const result={state:stop?(failed?'restore-failed':'restored'):probeOnly?'probe':verified?'applied':'waiting',theme:'tushan-susu-blossom',version:'1.0.0',appearanceVersion,mode:'native-css-lightweight',ownedEndpoints:owners.length,eligiblePages:eligible,verifiedPages:verified,reports};
  await status(result,!watch);return result;
}
let lock;
if(watch){
  const lockPath=path.join(stateRoot,'compat-loader.lock');
  try{lock=await fs.open(lockPath,'wx');await lock.writeFile(JSON.stringify({pid:process.pid,startedAt:new Date().toISOString()}))}
  catch(error){
    if(error.code!=='EEXIST')throw error;
    const prior=JSON.parse(await fs.readFile(lockPath,'utf8'));let alive=true;
    try{process.kill(prior.pid,0)}catch{alive=false}
    if(alive){console.log(JSON.stringify({state:'already-running'}));process.exit(0)}
    await fs.rm(lockPath);lock=await fs.open(lockPath,'wx');await lock.writeFile(JSON.stringify({pid:process.pid,startedAt:new Date().toISOString()}));
  }
}
try{
  let emptySince;
  do{
    try{
      const result=await tick();
      if(!watch){console.log(JSON.stringify(result));if((restore&&result.state!=='restored')||(!probeOnly&&!restore&&!result.verifiedPages))process.exitCode=1;break}
      if(result.state==='restored')break;
      if(!result.ownedEndpoints){emptySince??=Date.now();if(Date.now()-emptySince>90000)break}else emptySince=undefined;
    }catch(error){await log('loader-error',{error:error.message});await status({state:'error',error:error.message},true);if(!watch){console.error(error.message);process.exitCode=1;break}}
    await pause(10000);
  }while(watch);
}finally{
  for(const connection of connections.values())connection.close();
  if(lock){await lock.close();await fs.rm(path.join(stateRoot,'compat-loader.lock'),{force:true})}
}
