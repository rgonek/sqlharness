import { setup, launch } from './app-capture.mjs';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
const baseline=process.argv[2]==='baseline';
const browser=await launch();
try{
 const page=await browser.newPage({viewport:{width:1120,height:900}});await setup(page);
 await page.addInitScript(()=>{window.visualWindowErrors=[];addEventListener('error',e=>window.visualWindowErrors.push(e.message))});
 let origin='http://127.0.0.1:48126';
 if(baseline){
  const endpoint=JSON.parse(await fs.readFile(path.join(process.env.SQLHARNESS_HOME||path.join(os.homedir(),'.sqlharness'),'dashboard.json'),'utf8'));
  origin=`http://127.0.0.1:${endpoint.port}`;
  await page.context().addCookies([{name:'sqlharness_dashboard',value:endpoint.token,url:origin,httpOnly:true,sameSite:'Strict'}]);
 }
 await page.goto(origin+'/sessions/1');
 await page.getByRole('tab',{name:'Plan',exact:true}).click();await page.locator('[aria-label="Graphical execution plan"] .react-flow__node').first().waitFor();
 for(const width of [1280,768,390,1120]){
  await page.setViewportSize({width,height:900});
  await page.getByRole(baseline?'button':'radio',{name:baseline?'v0 · A':'v0 A',exact:true}).click();await page.waitForTimeout(250);
  await page.getByRole(baseline?'button':'radio',{name:baseline?'v2 · B':'v2 B',exact:true}).click();await page.waitForTimeout(250);
 }
 const errors=await page.evaluate(()=>window.visualWindowErrors);
 await fs.writeFile(new URL('../../shadcn/mock-alignment/resize-'+(baseline?'baseline':'result')+'.json',import.meta.url),JSON.stringify({runtime:baseline?'previous installed production UI':'production dist served without Vite',widths:[1280,768,390,1120],windowErrors:errors},null,2));
 console.log(JSON.stringify({windowErrors:errors}));
 if(errors.length)process.exitCode=1;
}finally{await browser.close()}
