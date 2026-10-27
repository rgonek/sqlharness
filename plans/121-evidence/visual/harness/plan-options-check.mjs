import { setup, launch } from './app-capture.mjs';
import fs from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
const out=new URL('../../shadcn/plan-options/',import.meta.url);await fs.mkdir(out,{recursive:true});
const browser=await launch(),errors=[],screens=[];
const assert=(condition,message)=>{if(!condition)throw Error(message)};
try{
 const page=await browser.newPage({viewport:{width:1280,height:1000}});await setup(page);
 page.on('pageerror',e=>errors.push(e.message));
 await page.route('**/api/operations/23/plans',route=>route.fulfill({json:{operationId:23,state:'stored',occurrences:['baseline','candidate'].flatMap(variant=>[1,2,3,4].flatMap(repetition=>Array.from({length:16},(_,i)=>({variant,parameterSet:null,caseId:'0',caseLabel:'Case 1 of 1',repetition,documentOrdinal:i,statementIndex:0,spanStart:0,spanLength:20,statementId:1,statementCompId:1,documentSha256:(i+1).toString(16).padStart(64,'a'),shapeHash:'shape',format:'showplan-xml',matchKind:i>=12?'unmatched':'exact',omitted:false,stored:true}))))}}));
 const shot=async name=>{await page.waitForTimeout(200);await page.screenshot({path:fileURLToPath(new URL(name+'.png',out)),fullPage:true,animations:'disabled'});screens.push(name)};
 await page.goto('http://127.0.0.1:48126/sessions/1');await page.getByRole('tab',{name:'Plan',exact:true}).click();
 const candidate=page.getByRole('radio',{name:'v2 B',exact:true});await candidate.waitFor();
 await page.getByRole('button',{name:'Advanced',exact:true}).waitFor();
 const radioStyle=await candidate.evaluate(e=>({opacity:getComputedStyle(e).opacity,width:e.getBoundingClientRect().width,height:e.getBoundingClientRect().height}));
 assert(radioStyle.opacity==='1'&&radioStyle.width>=12&&radioStyle.height>=12,'Radio indicator is hidden or replaced');
 assert(await candidate.isChecked(),'B is not the default radio');
 assert(await page.getByRole('combobox',{name:'Repetition',exact:true}).count()===0,'Repetition is not hidden by default');
 assert(await page.getByText(/unmatched SQL statement/).count()===0,'Unmatched warning list remains');
 assert(await page.getByText(/Batch SQL stays visible/).count()===0,'Static batch hint remains');
 assert(await page.getByText(/SQL linking unavailable/).count()===0,'Diagnostic note remains visible by default');await shot('standard-radio-default');
 await candidate.focus();await page.keyboard.press('ArrowLeft');
 assert(await page.getByRole('radio',{name:'v0 A',exact:true}).isChecked(),'Radio keyboard selection failed');
 assert(await page.getByRole('radio',{name:'v0 A',exact:true}).evaluate(e=>e===document.activeElement),'Radio lost focus when loading its plan');
 await page.keyboard.press('ArrowRight');await page.getByRole('button',{name:'Advanced',exact:true}).click();
 const repetition=page.getByRole('combobox',{name:'Repetition',exact:true});await repetition.waitFor();
 assert((await repetition.innerText()).includes('Repetition 4'),'Latest repetition is not retained');
 await page.getByText('SQL linking unavailable for 4 plan fragments.',{exact:true}).waitFor();await shot('advanced');
 await repetition.click();await page.getByRole('option',{name:'Repetition 1',exact:true}).click();
 assert((await repetition.innerText()).includes('Repetition 1'),'Advanced repetition did not update');
 await page.getByRole('button',{name:'Advanced',exact:true}).click();
 for(const width of [1280,390]){
  await page.setViewportSize({width,height:1000});
  const trigger=page.getByRole('combobox',{name:'Plan document',exact:true});await trigger.click();
  const popup=page.locator('[data-slot=select-content]:visible');await popup.waitFor();
  const item=page.getByRole('option',{name:'Document 16 · aaaaaaaa',exact:true});await item.waitFor({state:'attached'});
  const bounds=await popup.boundingBox();
  assert(bounds.x>=0&&bounds.x+bounds.width<=width,'Document popup is outside viewport');
  const textFits=await item.evaluate(e=>{const text=e.firstElementChild??e;const range=document.createRange();range.selectNodeContents(text);return range.getBoundingClientRect().right<=e.getBoundingClientRect().right-20});
  assert(textFits,'Document name/hash is clipped or overlaps indicator');
  await popup.screenshot({path:fileURLToPath(new URL('document-menu-'+width+'.png',out)),animations:'disabled'});
  await shot('documents-'+width);await page.keyboard.press('Escape');
  const overflow=await page.evaluate(()=>({width:innerWidth,scroll:document.documentElement.scrollWidth,elements:[...document.querySelectorAll('.plan-workbench, .version-plan, .plan-workbench > div, .version-plan > div, .plan-workbench .grid, .monaco-editor')].map(e=>({tag:e.tagName,cls:e.className,right:e.getBoundingClientRect().right,width:e.getBoundingClientRect().width,parentWidth:e.parentElement.getBoundingClientRect().width,minWidth:getComputedStyle(e).minWidth,display:getComputedStyle(e).display}))}));
  if(overflow.scroll>overflow.width)await fs.writeFile(new URL('overflow.json',out),JSON.stringify(overflow,null,2));
  assert(overflow.scroll<=overflow.width,'Document page overflows');
 }
 assert(errors.length===0,errors.join('\n'));
 await fs.writeFile(new URL('results.json',out),JSON.stringify({screens,radioStyle,documentChoices:16,repetitions:4,unmatched:4,errors},null,2));console.log('PASS plan options:',screens.length,'screens');
}finally{await browser.close()}
