// Focused native screen-reader, browser zoom and non-text contrast evidence.
// Existing portable NVDA and an isolated synthetic backend are required. No SQL/write requests.
import fs from 'node:fs';
import path from 'node:path';
import {createRequire} from 'node:module';
import {execFileSync,spawn} from 'node:child_process';
const require=createRequire('D:/temp/sqlharness-dashboard-audit/node_modules/audit.cjs');
const {chromium}=require('playwright');
const output=path.resolve(process.argv[2]??'plans/126-evidence/browser/native-accessibility');
const mode=process.env.NATIVE_MODE??'contrast';
if(mode==='screenreader'&&process.env.NATIVE_UNLOCKED!=='1')throw new Error('Native screen-reader pending: an explicit operator report of an unlocked desktop is required. No NVDA or native keys started.');
fs.mkdirSync(output,{recursive:true});
const home=process.env.NATIVE_HOME??'D:/temp/sqlharness-quality-home-20261110';
const endpoint=JSON.parse(fs.readFileSync(path.join(home,'dashboard.json'),'utf8'));
const backendOrigin=`http://127.0.0.1:${endpoint.port}`;
const origin=process.env.NATIVE_ORIGIN??backendOrigin;
const report={startedAt:new Date().toISOString(),mode,origin:origin.replace(/:\d+$/,':<isolated>'),revision:execFileSync('git',['rev-parse','HEAD'],{encoding:'utf8'}).trim(),workingDiffSha256:execFileSync('git',['diff','--','src'],{encoding:'utf8',maxBuffer:10e6}),checks:[],limitations:[]};
report.workingDiffSha256=(await import('node:crypto')).createHash('sha256').update(report.workingDiffSha256).digest('hex');
const headed=mode==='screenreader'||mode==='zoom';
const args=['--window-position=-2000,-2000','--window-size=1440,1000','--force-renderer-accessibility'];
if(mode==='zoom')args.push('--disable-extensions-except=D:/temp/sqlharness-dashboard-audit/zoom-extension','--load-extension=D:/temp/sqlharness-dashboard-audit/zoom-extension');
const profile=`D:/temp/sqlharness-dashboard-audit/native-profile-${Date.now()}`;
const context=await chromium.launchPersistentContext(profile,{headless:!headed,viewport:headed?null:{width:1440,height:1000},args,reducedMotion:'reduce'});
report.browser=context.browser()?.version();
await context.addCookies([{name:'sqlharness_dashboard',value:endpoint.token,url:origin,httpOnly:true,sameSite:'Strict'}]);
await context.route('**/api/**',async r=>{try{if(!['GET','HEAD','OPTIONS'].includes(r.request().method()))return r.abort('blockedbyclient');if(origin===backendOrigin)return r.continue();const u=new URL(r.request().url());if(u.pathname==='/api/live'||u.pathname==='/api/mutations/events')return r.fulfill({status:200,contentType:'text/event-stream',body:': native accessibility style audit\n\n'});const response=await r.fetch({url:backendOrigin+u.pathname+u.search});return r.fulfill({response});}catch{report.routeErrors??=[];report.routeErrors.push({path:new URL(r.request().url()).pathname,error:'Request cancelled or forwarding failed; raw transport details suppressed'});}});
const page=await context.newPage();page.setDefaultTimeout(15000);
if(mode!=='screenreader')await page.addInitScript(()=>{class AuditEvents extends EventTarget{static OPEN=1;static CLOSED=2;static CONNECTING=0;readyState=1;constructor(){super();setTimeout(()=>{this.onopen?.(new Event('open'));this.dispatchEvent(new Event('open'));},10);}close(){this.readyState=2;}}window.EventSource=AuditEvents;});
const wait=ms=>page.waitForTimeout(ms);
const goto=async route=>{await page.goto(origin+route);await page.locator('main').waitFor();await wait(500);};
const active=()=>page.evaluate(()=>{const e=document.activeElement;return {tag:e?.tagName,role:e?.getAttribute('role'),name:e?.getAttribute('aria-label'),text:e?.textContent?.slice(0,160)};});
const foreground=async()=>{await page.evaluate(()=>document.title='SQLHarness native accessibility audit');execFileSync('powershell.exe',['-NoProfile','-Command',"$s=New-Object -ComObject WScript.Shell; if(-not $s.AppActivate('SQLHarness native accessibility audit')){throw 'Owned audit browser not found'}"],{windowsHide:true});};
const nativeKey=async key=>{await foreground();execFileSync('powershell.exe',['-NoProfile','-Command',`Add-Type -TypeDefinition 'using System; using System.Text; using System.Runtime.InteropServices; public class AuditNativeFocus { [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder text, int count); }'; $s=New-Object -ComObject WScript.Shell; if(-not $s.AppActivate('SQLHarness native accessibility audit')){throw 'Owned audit browser not active'}; $auditTitle=New-Object System.Text.StringBuilder 256; [void][AuditNativeFocus]::GetWindowText([AuditNativeFocus]::GetForegroundWindow(),$auditTitle,256); if(-not $auditTitle.ToString().StartsWith('SQLHarness native accessibility audit')){throw 'Owned browser is not native foreground; no key sent'}; $s.SendKeys('${key}')`],{windowsHide:true});};
const api=async route=>{const response=await context.request.get(backendOrigin+route);if(!response.ok())throw new Error('Synthetic API unavailable: '+response.status());return response.json();};
const section=await api('/api/sessions/1/optimization');
const group=section.subjects[0].groups[0],scope=group.scopes[0];
const sessionRoute='/sessions/1?'+new URLSearchParams({subject:'orders',db:'ShopDb',group:group.fingerprint,scope:scope.scope,a:'v48',b:'v49',case:'all',agg:'sum',view:'whole'});
const snapshot=async label=>page.evaluate(label=>{
 const cv=document.createElement('canvas');cv.width=cv.height=1;const cx=cv.getContext('2d',{willReadFrequently:true});
 const rgb=color=>{cx.clearRect(0,0,1,1);cx.fillStyle=color;cx.fillRect(0,0,1,1);return [...cx.getImageData(0,0,1,1).data].map((v,i)=>i===3?v/255:v);};
 const over=(fg,bg)=>fg.slice(0,3).map((v,i)=>v*fg[3]+bg[i]*(1-fg[3])).concat(1);
 const bg=e=>{let chain=[];for(;e;e=e.parentElement)chain.push(e);return chain.reverse().reduce((b,n)=>over(rgb(getComputedStyle(n).backgroundColor),b),[255,255,255,1]);};
 const lum=c=>c.slice(0,3).map(v=>v/255).map(v=>v<=.04045?v/12.92:((v+.055)/1.055)**2.4).reduce((s,v,i)=>s+v*[.2126,.7152,.0722][i],0);
 const ratio=(a,b)=>(Math.max(lum(a),lum(b))+.05)/(Math.min(lum(a),lum(b))+.05);
 const sels=['[data-slot="checkbox"]','[data-slot="radio-group-item"]','input[data-slot="input"]','[data-slot="switch"]','[data-slot="switch-thumb"]','button[aria-label*="tar"] svg','.recharts-bar-rectangle path','.recharts-line path','.react-flow__edge-path','.plan-graph article'];
 const rows=sels.flatMap(selector=>[...document.querySelectorAll(selector)].filter(e=>e.getBoundingClientRect().width).slice(0,6).map(e=>{const s=getComputedStyle(e),back=bg(e.parentElement),inside=bg(e),border=over(rgb(s.borderTopColor),back),isSvg=e instanceof SVGElement,fill=isSvg&&s.fill!=='none'?over(rgb(s.fill),back):null,stroke=isSvg&&s.stroke!=='none'?over(rgb(s.stroke),back):null;return {selector,tag:e.tagName,name:e.getAttribute('aria-label')??e.closest('button')?.getAttribute('aria-label')??e.getAttribute('placeholder')??e.textContent?.slice(0,50),checked:e.getAttribute('aria-checked'),border:s.borderTopColor,background:s.backgroundColor,fill:isSvg?s.fill:null,stroke:isSvg?s.stroke:null,surround:back.slice(0,3),borderRatio:ratio(border,back),borderInnerRatio:ratio(border,inside),backgroundRatio:ratio(inside,back),fillRatio:fill?ratio(fill,back):null,strokeRatio:stroke?ratio(stroke,back):null,focusVisible:e.matches(':focus-visible'),boxShadow:s.boxShadow,outline:s.outline,outlineRatio:ratio(over(rgb(s.outlineColor),back),back)};}));
 const tokens=Object.fromEntries(['--input','--ring','--chart-1','--chart-2','--chart-3','--chart-4','--chart-5'].map(t=>{const color=getComputedStyle(document.documentElement).getPropertyValue(t).trim(),back=bg(document.querySelector('main'));return [t,{color,ratio:ratio(over(rgb(color),back),back),against:back.slice(0,3)}];}));
 return {label,theme:document.documentElement.className,rows,tokens};
},label);
try{
 if(mode==='contrast'){
  for(const theme of ['light','dark']){
   await goto('/settings');await page.getByRole('button',{name:/^Theme:/}).click();await page.getByRole('menuitemradio',{name:theme==='dark'?'Dark':'Light',exact:true}).click();await wait(300);
   report.checks.push(await snapshot('settings'));
   const field=page.locator('input[data-slot="input"]').first();if(await field.count()){await page.keyboard.press('Tab');await field.focus();await wait(100);report.checks.push(await snapshot('settings keyboard focus'));await page.screenshot({path:path.join(output,`focus-${theme}.png`)});}
   await goto(sessionRoute);await page.locator('section[aria-label="Version comparison"][aria-busy="false"]').waitFor();report.checks.push(await snapshot('session A/B'));
   await goto('/');report.checks.push(await snapshot('activity star icons'));
   await page.screenshot({path:path.join(output,`contrast-${theme}.png`)});
  }
 }
 if(mode==='graph'){
  // Explicit intercepted synthetic plan; retained-plan provenance is not established here.
  const {setup,sql}=await import('file:///D:/temp/sqlharness-dashboard-audit/fixture.mjs');await setup(page);
  await page.route('**/source-map*',r=>r.fulfill({status:200,contentType:'application/json',body:JSON.stringify({operationId:20,state:'stored',engine:'sqlserver',variant:'measure',parameterSet:null,caseId:'0',caseLabel:'@CustomerId = 42',repetition:1,executedText:sql,algorithmVersion:1,links:['exact','inferred','ambiguous'].map((state,i)=>({documentSha256:'a'.repeat(64),documentOrdinal:0,link:{nodeId:'0:'+i,spanStart:0,spanLength:sql.length,kind:'statement',state,rule:'synthetic contrast fixture'}})),omittedDocuments:[],errors:[]})}));
  for(const theme of ['light','dark']){
   await goto('/operations/20');await page.getByRole('button',{name:/^Theme:/}).click();await page.getByRole('menuitemradio',{name:theme==='dark'?'Dark':'Light',exact:true}).click();await wait(500);
   const planTab=page.getByRole('tab',{name:/Plan/}).first();if(await planTab.count())await planTab.click();
   await page.locator('.react-flow__edge-path').first().waitFor({state:'attached'});await wait(300);report.checks.push(await snapshot('intercepted synthetic plan graph'));await page.screenshot({path:path.join(output,`graph-${theme}.png`)});
  }
  report.limitations.push('Plan graph uses intercepted synthetic graph/source-map API responses. Only rendered edges and nodes sampled; no retained execution-plan backend contract is claimed.');
 }
 if(mode==='zoom'){
  const worker=context.serviceWorkers()[0]??await context.waitForEvent('serviceworker');
  for(const route of ['/',sessionRoute,'/operations/10000','/settings']){
   await goto(route);if(route==='/')await page.locator('.activity-session-row').first().waitFor();else if(route==='/settings')await page.getByRole('spinbutton',{name:'Connection timeout (seconds)'}).waitFor();else await page.locator('section[aria-label="Version comparison"][aria-busy="false"]').waitFor();
   await worker.evaluate(async origin=>{const tabs=await chrome.tabs.query({});const t=tabs.find(t=>t.url?.startsWith(origin));await chrome.tabs.setZoom(t.id,1);},origin);await wait(100);
   const before=await page.evaluate(()=>({width:innerWidth,dpr:devicePixelRatio}));
   await worker.evaluate(async origin=>{const tabs=await chrome.tabs.query({});const t=tabs.find(t=>t.url?.startsWith(origin));await chrome.tabs.setZoom(t.id,2);},origin);await wait(500);
   const after=await page.evaluate(()=>({width:innerWidth,dpr:devicePixelRatio,scrollWidth:document.documentElement.scrollWidth,zoom:visualViewport.scale}));
   const name=route.split('?')[0].replaceAll('/','-')||'activity';const cdp=await context.newCDPSession(page);const capture=await cdp.send('Page.captureScreenshot',{format:'png',captureBeyondViewport:false,fromSurface:true});fs.writeFileSync(path.join(output,`zoom200${name}.png`),Buffer.from(capture.data,'base64'));await cdp.detach();
   report.checks.push({route:route.split('?')[0],before,after,nativeZoom:after.dpr===before.dpr*2&&after.width===before.width/2,reflow:after.scrollWidth<=after.width+1});
  }
  report.limitations.push('Live EventSource locally held open for this style/zoom audit; disconnection/reconnection is outside its scope. Page-only CDP capture avoids Playwright native-zoom clipping.');
 }
 if(mode==='screenreader'){
  await goto('/');await page.bringToFront();
  // NVDA only runs while this dedicated synthetic browser owns foreground focus.
  const nvda=spawn(process.env.NATIVE_NVDA_EXE,['--no-sr-flag','--minimal','--disable-addons','-c',process.env.NATIVE_NVDA_CONFIG,'-f',process.env.NATIVE_NVDA_LOG,'--log-level','10'],{windowsHide:true,stdio:'ignore'});
  report.nvdaPid=nvda.pid;
  for(let i=0;i<60;i++){await wait(1000);if(fs.existsSync(process.env.NATIVE_NVDA_LOG)&&fs.readFileSync(process.env.NATIVE_NVDA_LOG,'utf8').includes('NVDA initialized'))break;if(i===59)throw new Error('NVDA startup timed out');}
  await foreground();await wait(1500);
  const step=async(name,action)=>{const offset=fs.existsSync(process.env.NATIVE_NVDA_LOG)?fs.statSync(process.env.NATIVE_NVDA_LOG).size:0;await action();await wait(1500);const raw=fs.readFileSync(process.env.NATIVE_NVDA_LOG).subarray(offset).toString();const speech=raw.split(/\r?\n/).filter(line=>line.startsWith('Speaking ')&&/Skip to main|SQLHarness native|Activity|Search activity|A v48|B v49|B v48|Case outcomes|Policy:|SQL editor|CustomerId|read only|main landmark|readonly|SELECT|Copy baseline/.test(line));report.checks.push({name,active:await active(),speech,rawBytes:raw.length});};
  try{
   await step('first Tab exposes skip link',()=>nativeKey('{TAB}'));
   await step('skip link Enter reaches main',()=>nativeKey('{ENTER}'));
   await step('next Tab leaves main via dashboard control',()=>nativeKey('{TAB}'));
   await goto(sessionRoute);await page.locator('section[aria-label="Version comparison"][aria-busy="false"]').waitFor();
   await step('baseline A radio name',()=>page.getByRole('radio',{name:'A v48',exact:true}).focus());
   await step('candidate B radio name',()=>page.getByRole('radio',{name:'B v49',exact:true}).focus());
   await step('B radio keyboard change',()=>nativeKey('{LEFT}'));
   await goto(sessionRoute);await page.locator('section[aria-label="Version comparison"][aria-busy="false"]').waitFor();await foreground();
   await step('case outcome dialog open',async()=>{const trigger=page.locator('section[aria-label="Version comparison"]').getByRole('button',{name:/^Policy:/}).first();await trigger.focus();await page.keyboard.press('Enter');await page.getByRole('dialog',{name:'Case outcomes',exact:true}).waitFor();});
   await step('case outcome dialog first Tab',()=>nativeKey('{TAB}'));
   await step('case outcome Escape returns focus',()=>nativeKey('{ESC}'));
   await goto('/operations/10000');
   await step('SQL read-only editor focused',()=>page.getByRole('textbox',{name:/SQL editor/}).first().focus());
   await foreground();await step('SQL Tab navigation',()=>nativeKey('{TAB}'));
   await step('SQL Shift+Tab return',()=>nativeKey('+{TAB}'));
  }finally{nvda.kill();execFileSync('powershell.exe',['-NoProfile','-Command',"Get-Process | Where-Object Path -like 'D:\\temp\\sqlharness-dashboard-audit\\nvda-portable-clean-20261111\\*' | ForEach-Object {Stop-Process -Id $_.Id -Force}"],{windowsHide:true});await wait(800);}
  report.limitations.push('NVDA actual speech pipeline IO logs with silence synthesizer; no audible listening, braille or blind-user study. Targeted focus setup is explicit; entire workflow is not claimed keyboard-only.');
 }
}catch(error){report.error=String(error.stack);process.exitCode=1;}
finally{report.assets=[];for(const url of await page.evaluate(()=>[...document.querySelectorAll('script[src],link[rel="stylesheet"]')].map(e=>e.src||e.href))){try{const response=await context.request.get(url);report.assets.push({path:new URL(url).pathname,sha256:(await import('node:crypto')).createHash('sha256').update(await response.body()).digest('hex')});}catch{report.assets.push({path:new URL(url).pathname,error:'Asset hash unavailable'});}}report.finishedAt=new Date().toISOString();fs.writeFileSync(path.join(output,mode+'.json'),JSON.stringify(report,null,2));await context.unrouteAll({behavior:'ignoreErrors'});await context.close();}
console.log(JSON.stringify({mode,checks:report.checks.length,error:report.error,output}));
