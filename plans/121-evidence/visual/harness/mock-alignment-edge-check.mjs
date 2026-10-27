import { setup, launch, comp } from './app-capture.mjs';
import fs from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
const out=new URL('../../shadcn/mock-alignment/edge/',import.meta.url);await fs.mkdir(out,{recursive:true});
const browser=await launch(),errors=[],records=[];
try{
 const page=await browser.newPage({viewport:{width:1120,height:900}});await setup(page);
 page.on('pageerror',e=>errors.push(e.message));page.on('console',m=>{if(m.type()==='error')errors.push(m.text())});
 for(const [basis,label]of [['SameSql','same SQL'],['ConditionsDiffer','conditions differ']]){
  comp.basis=basis;comp.equivalenceLabel=label;
  await page.goto('http://127.0.0.1:48126/sessions/1');await page.getByRole('tab',{name:'SQL diff',exact:true}).waitFor();
  const text=await page.locator('.comparison-tab-header .comparison-context').innerText();
  if(text.split(label).length!==2||text.includes('· ·'))throw Error('Repeated/empty context: '+text);
  if(basis==='ConditionsDiffer'){
   await page.getByRole('tab',{name:'IO',exact:true}).click();
   await page.getByText('conditions differ; SQL diff is the available comparison.').first().waitFor();
   if(await page.getByRole('table',{name:'IO before and after',exact:true}).count())throw Error('Conditions-differ IO must stay unavailable');
  }
  await page.screenshot({path:fileURLToPath(new URL(basis+'.png',out)),fullPage:true});records.push({basis,text});
 }
 if(errors.length)throw Error(errors.join('\n'));
 await fs.writeFile(new URL('results.json',out),JSON.stringify({records,errors},null,2));console.log('PASS duplicate metadata and conditions-differ evidence gating');
}finally{await browser.close()}
