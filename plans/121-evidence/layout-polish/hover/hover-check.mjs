import fs from 'node:fs/promises';
import {setup,launch} from './layout-polish-fixtures.mjs';
const out=process.env.SQLHARNESS_VISUAL_ROOT+'/plans/121-evidence/layout-polish/hover';
await fs.mkdir(out,{recursive:true});
const browser=await launch(),page=await browser.newPage({viewport:{width:1280,height:900}}),checks=[],errors=[];
page.on('pageerror',e=>errors.push(e.message));
page.on('console',m=>{if(m.type()==='error')errors.push(m.text())});
await setup(page);
async function inspect(row,name){
  await page.mouse.move(0,0);await page.waitForTimeout(250);
  const before=await row.evaluate(e=>getComputedStyle(e).backgroundColor);
  await row.hover({position:{x:8,y:8}});await page.waitForTimeout(250);
  const hover=await row.evaluate(e=>getComputedStyle(e).backgroundColor);
  if(before===hover)throw Error(name+': hover background unchanged: '+before);
  await page.screenshot({path:out+'/'+name+'.png'});
  await page.mouse.move(0,0);await page.waitForTimeout(250);
  const after=await row.evaluate(e=>getComputedStyle(e).backgroundColor);
  if(after!==before)throw Error(name+': background did not reset');
  checks.push({name,before,hover,after});
}
for(const dark of [false,true]){
  await page.goto('http://127.0.0.1:48138/');
  await page.locator('.activity-session-row').first().waitFor();
  await page.evaluate(d=>document.documentElement.classList.toggle('dark',d),dark);
  await inspect(page.locator('.activity-session-row').first(),'activity-'+(dark?'dark':'light'));
  await page.locator('.activity-session-row').first().getByRole('link').click();
  await page.getByRole('tab',{name:'IO',exact:true}).click();
  const row=page.locator('.io-table tbody tr').first();await row.waitFor();
  const button=row.getByRole('button').first();
  for(const open of [true,false]){
    if((await button.getAttribute('aria-expanded')==='true')!==open)await button.click();
    await inspect(row,'io-'+(open?'open':'closed')+'-'+(dark?'dark':'light'));
  }
}
if(errors.length)throw Error(JSON.stringify(errors));
await fs.writeFile(out+'/results.json',JSON.stringify({checks,errors},null,2));
console.log(JSON.stringify({checks,errors},null,2));await browser.close();
