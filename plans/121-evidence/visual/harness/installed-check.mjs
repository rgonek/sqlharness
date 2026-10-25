import {launch,setup,out,root} from './app-capture.mjs';
import fs from 'node:fs/promises';
import crypto from 'node:crypto';
const home='D:/temp/sqlharness-visual-installed-home',origin='http://127.0.0.1:48125';
const token=(await fs.readFile(home+'/dashboard-token','utf8')).trim();
const browser=await launch(),context=await browser.newContext({viewport:{width:1280,height:1400},deviceScaleFactor:1,colorScheme:'light'});
await context.addCookies([{name:'sqlharness_dashboard',value:token,url:origin,httpOnly:true,sameSite:'Strict'}]);
const page=await context.newPage(),errors=[],assets=[];
page.on('pageerror',e=>errors.push(e.message));page.on('console',m=>{if(m.type()==='error')errors.push(m.text());});
const response=await context.request.get(origin+'/');if(response.status()!==200)throw Error('Installed index not authenticated');
const html=await response.text();
for(const path of [...html.matchAll(/(?:src|href)="(\/assets\/[^\"]+)"/g)].map(m=>m[1])){
 const response=await context.request.get(origin+path);if(response.status()!==200)throw Error('Missing installed asset');
 const body=await response.body(),expected=await fs.readFile(root+'/src/SqlHarness.Dashboard/ui/dist'+path);
 if(!body.equals(expected))throw Error('Installed UI asset differs: '+path);
 assets.push({path,sha256:crypto.createHash('sha256').update(body).digest('hex')});
}
await setup(page);
for(const [name,path,width] of [['activity','/',1280],['settings','/settings',1280],['operation','/operations/20',1280],['session','/sessions/1',1120]]){
 await page.setViewportSize({width,height:1400});await page.goto(origin+path);await page.locator('main').waitFor();await page.evaluate(()=>document.fonts.ready);await page.waitForTimeout(1000);
 await page.screenshot({path:out+'/installed-'+name+'.png',fullPage:true});
}
await browser.close();if(errors.length)throw Error(JSON.stringify(errors));
await fs.writeFile(out+'/installed-ui.json',JSON.stringify({origin,isolatedHome:true,apiFixtures:true,assets,errors,screens:['activity','settings','operation','session'],tokenRecorded:false},null,2));
console.log(JSON.stringify({verifiedAssets:assets.length,errors,screens:4}));
