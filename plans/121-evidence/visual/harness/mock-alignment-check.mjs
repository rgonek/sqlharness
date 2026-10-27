import { setup, launch } from './app-capture.mjs';
import fs from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
const phase=process.argv[2]??'before';
const out=new URL('../../shadcn/mock-alignment/'+phase+'/',import.meta.url);
await fs.mkdir(out,{recursive:true});
const browser=await launch(),records=[],errors=[];
try{
 const page=await browser.newPage({viewport:{width:1280,height:1400}});
 page.on('pageerror',e=>errors.push(e.message));
 const capture=async(name,locator)=>{
  await page.evaluate(()=>document.fonts.ready);await page.waitForTimeout(250);
  await (locator??page).screenshot({path:fileURLToPath(new URL(name+'.png',out)),...(locator?{}:{fullPage:true})});
  records.push({name,text:await (locator??page.locator('body')).innerText()});
 };
 if(phase==='before'){
  await page.goto('http://127.0.0.1:48121/Session%20Optimization.dc.html');
  const card=page.locator('[id="4a"] > div').last();
  for(const tab of ['SQL diff','IO','Plan']){await card.getByText(tab,{exact:true}).first().click();await capture('mock-session-'+tab.replaceAll(' ','-'),card)}
  await page.goto('http://127.0.0.1:48121/Activity.dc.html');await capture('mock-activity',page.locator('[id="3a"] > div').last());
  await page.goto('http://127.0.0.1:48121/Settings.dc.html');await capture('mock-settings');
  await page.goto('http://127.0.0.1:48121/Operation%20v2.dc.html');
  for(const [kind,tab]of [['Measure','IO'],['Query','Results'],['Mutate','Approval'],['Compare','Plan']]){await page.getByText(kind,{exact:true}).first().click();await page.getByText(tab,{exact:true}).first().click();await capture('mock-operation-'+kind)}
 }
 await setup(page);
 await page.goto('http://127.0.0.1:48126/sessions/1');
 for(const tab of ['SQL diff','IO','Plan']){
  await page.getByRole('tab',{name:tab,exact:true}).click();
  if(tab==='SQL diff')await page.locator('.monaco-editor:visible').first().waitFor();
  if(tab==='Plan')await page.locator('[aria-label="Graphical execution plan"] .react-flow__node').first().waitFor();
  await capture('app-session-'+tab.replaceAll(' ','-'),page.locator('.optimization-card').first());
 }
 if(phase==='after'){
  const planB=page.getByRole('radio',{name:'v2 B',exact:true});
  if(!await planB.isChecked())throw Error('Plan B is not selected');
  await planB.focus();await page.keyboard.press('ArrowLeft');
  if(!await page.getByRole('radio',{name:'v0 A',exact:true}).isChecked())throw Error('Plan radio keyboard selection failed');
  await page.keyboard.press('Space');if(!await page.getByRole('radio',{name:'v0 A',exact:true}).isChecked())throw Error('Plan radio deselected');
  const header=page.locator('.comparison-tab-header').first();
  if((await header.innerText()).includes('· ·'))throw Error('Empty comparison metadata segment');
  const geometry=await header.evaluate(e=>{const tab=e.querySelector('[data-slot=tabs-trigger][data-active]');const line=getComputedStyle(tab,'::after');return {header:e.getBoundingClientRect().toJSON(),tab:tab.getBoundingClientRect().toJSON(),bottom:line.bottom}});
  if(geometry.bottom!=='-1px')throw Error('Tab underline is not aligned with panel edge');
  await fs.writeFile(new URL('tab-geometry.json',out),JSON.stringify(geometry,null,2));
 }
 for(const [name,route]of [['activity','/'],['settings','/settings'],['operation-measure','/operations/20'],['operation-query','/operations/21'],['operation-mutation','/operations/22'],['operation-compare','/operations/23']]){
  await page.goto('http://127.0.0.1:48126'+route);await page.locator('main h1').waitFor();
  if(name==='operation-measure')await page.getByRole('tab',{name:'IO',exact:true}).click();
  if(name==='operation-query')await page.getByRole('tab',{name:'Results',exact:true}).click();
  if(name==='operation-mutation')await page.getByRole('tab',{name:'Approval',exact:true}).click();
  if(name==='operation-compare'){await page.getByRole('tab',{name:'Plan',exact:true}).click();await page.locator('[aria-label="Graphical execution plan"] .react-flow__node').first().waitFor()}
  await capture('app-'+name);
 }
 await page.setViewportSize({width:390,height:900});await page.goto('http://127.0.0.1:48126/sessions/1');await page.getByRole('tab',{name:'Plan',exact:true}).click();await page.locator('[aria-label="Graphical execution plan"] .react-flow__node').first().waitFor();
 if(!await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth))throw Error('Mobile document overflow');
 await capture('app-plan-mobile');
 if(errors.length)throw Error(errors.join('\n'));
 await fs.writeFile(new URL('results.json',out),JSON.stringify({phase,records,errors},null,2));console.log('PASS',phase,records.length,'captured states');
}finally{await browser.close()}
