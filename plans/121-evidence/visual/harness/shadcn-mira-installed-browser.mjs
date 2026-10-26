import { setup, launch, settings } from './app-capture.mjs';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
const endpoint=JSON.parse(await fs.readFile(path.join(process.env.SQLHARNESS_HOME||path.join(os.homedir(),'.sqlharness'),'dashboard.json'),'utf8'));
const origin=`http://127.0.0.1:${endpoint.port}`;
const out=new URL('../../shadcn/followup/mira/installed/',import.meta.url);
await fs.mkdir(out,{recursive:true});
const browser=await launch();
try{
 const context=await browser.newContext({viewport:{width:1280,height:900}});
 // Authentication stays in this ephemeral context; no storage state is written.
 await context.addCookies([{name:'sqlharness_dashboard',value:endpoint.token,url:origin,httpOnly:true,sameSite:'Strict'}]);
 const page=await context.newPage();await setup(page);
 const errors=[];page.on('pageerror',e=>errors.push(e.message));
 settings.settings.mutations.defaultMode='yolo';settings.settings.diagnostics.enabled=true;
 const screens=[];
 for(const [name,route]of [['session','/sessions/1'],['settings','/settings'],['activity','/']]){
  await page.goto(origin+route);await page.locator('main h1').waitFor();
  if(name==='session')await page.locator('.monaco-editor:visible').first().waitFor();
  await page.evaluate(()=>document.fonts.ready);
  if(!await page.evaluate(()=>document.fonts.check('14px "Inter Variable"')))throw Error('Installed Inter font did not load');
  await page.screenshot({path:fileURLToPath(new URL(name+'.png',out)),fullPage:true,animations:'disabled'});
  screens.push(name);
 }
 if(errors.length)throw Error(errors.join('\n'));
 await fs.writeFile(new URL('results.json',out),JSON.stringify({origin,pid:endpoint.pid,screens,errors,api:'browser fixtures only',tokenRecorded:false},null,2));
 console.log('PASS installed UI: 3 fixture screens; Inter loaded; no page errors');
}finally{await browser.close()}
