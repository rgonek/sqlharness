import {setup,launch,comp} from './app-capture.mjs';
import fs from 'node:fs/promises';
import {fileURLToPath} from 'node:url';
const name=process.argv[2]??'after';
const out=new URL('../../shadcn/audit-recommendations/plan-control/',import.meta.url);await fs.mkdir(out,{recursive:true});
const browser=await launch();
try{
 const page=await browser.newPage({viewport:{width:1280,height:900},deviceScaleFactor:2});await setup(page);
 await page.route('**/api/sessions/1/optimization/compare?*',route=>route.fulfill({json:{...comp,a:'v69',b:'v70'}}));
 await page.goto('http://127.0.0.1:48126/sessions/1');
 await page.getByRole('radio',{name:'Side by side',exact:true}).waitFor({state:'attached'});
 await page.getByRole('radiogroup',{name:'Diff mode',exact:true}).screenshot({path:fileURLToPath(new URL('reference.png',out))});
 await page.getByRole('tab',{name:'Plan',exact:true}).click();const group=page.getByRole('radiogroup',{name:'Plan version',exact:true});await group.waitFor();await page.mouse.move(0,0);
 await group.screenshot({path:fileURLToPath(new URL(name+'.png',out))});
 const geometry=await group.evaluate(e=>({width:e.getBoundingClientRect().width,height:e.getBoundingClientRect().height,labels:[...e.querySelectorAll('[data-slot=label]')].map(l=>({text:l.textContent,height:l.getBoundingClientRect().height,gap:getComputedStyle(l).gap,badge:[...l.querySelectorAll('[data-slot=badge]')].map(b=>({height:b.getBoundingClientRect().height,width:b.getBoundingClientRect().width,font:getComputedStyle(b).fontSize,lineHeight:getComputedStyle(b).lineHeight,overflow:getComputedStyle(b).overflow}))}))}));
 await fs.writeFile(new URL(name+'.json',out),JSON.stringify(geometry,null,2));console.log(name,geometry);
}finally{await browser.close()}
