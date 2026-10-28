import fs from 'node:fs/promises';
import {setup,launch,sessions} from './layout-polish-fixtures.mjs';
const root=process.env.SQLHARNESS_VISUAL_ROOT,out=root+'/plans/121-evidence/layout-polish/default-alignment';
const browser=await launch(),p=await browser.newPage({viewport:{width:390,height:900}}),checks=[],errors=[],requests=[];
await setup(p);
p.on('pageerror',e=>errors.push(e.message));p.on('console',m=>{if(m.type()==='error')errors.push(m.text())});
p.on('request',r=>{if(r.method()!=='GET')requests.push({method:r.method(),url:r.url()})});
const check=(value,name,measurement)=>{checks.push({name,passed:!!value,measurement});if(!value)throw Error(name)};
const base={name:'reporting',engine:'SqlServer',server:'localhost',database:'billing',auth:'sql',sqlUser:'reader',passwordEnvVar:'BILLING_READER_PASSWORD',tls:'verify',vars:[],environment:'dev',mutationMode:'operator',mutationWriterKind:'sameAsReader'};
let profile=base,note=null;
await p.route('**/api/profiles',r=>r.fulfill({json:{status:'valid',profiles:[profile]}}));
await p.route('**/api/sessions/1',r=>r.fulfill({json:{session:sessions[0],operations:[],note,conversations:[],compareSubjects:[],unassignedCompareCount:0}}));
await p.route('**/api/sessions/1/annotation',r=>{const body=r.request().postDataJSON();if('note'in body)note=body.note;return r.fulfill({json:{title:null,note,starred:true,archived:false}})});
for(const mode of ['operator','yolo']){
  profile={...base,mutationMode:mode};await p.goto('http://127.0.0.1:48138/profiles');
  const region=p.getByRole('region',{name:'Mutations',exact:true});await region.waitFor();
  const widths=await region.locator('[data-slot=table-container]').evaluate(e=>({viewport:e.clientWidth,content:e.scrollWidth}));
  check(widths.content<=widths.viewport+1,'Profiles '+mode+' explanations fit mobile table',widths);
  check(await p.evaluate(()=>document.documentElement.scrollWidth<=innerWidth),'Profiles '+mode+' document fits mobile');
  await p.screenshot({path:out+'/profiles-'+mode+'-mobile-edge.png',fullPage:true});
}
const longName='reporting_production_read_only_customer_invoices_north_europe';
profile={...base,name:longName};
for(const width of [390,1280]){
  await p.setViewportSize({width,height:900});await p.goto('http://127.0.0.1:48138/profiles');
  const title=p.getByRole('heading',{name:longName,exact:true});await title.waitFor();
  const dimensions=await p.evaluate(()=>({viewport:innerWidth,document:document.documentElement.scrollWidth}));
  check(dimensions.document<=dimensions.viewport,'Long profile name fits '+width,dimensions);
  check(await title.innerText()===longName,'Long profile identifier preserved '+width);
  await p.screenshot({path:out+'/profiles-long-'+width+'-edge.png',fullPage:true});
}
await p.goto('http://127.0.0.1:48138/sessions/1');const add=p.getByRole('button',{name:'Add note',exact:true});await add.waitFor();
const addSize=await add.boundingBox();check(addSize.width<200,'Empty Add note retains intrinsic width',addSize);
await p.screenshot({path:out+'/session-empty-note-edge.png',fullPage:true});
await add.click();const textarea=p.getByPlaceholder('Note for this session');await textarea.fill('Keep this session note');
check((await textarea.boundingBox()).width>900,'Note editor retains full width');
await p.getByRole('button',{name:'Save note'}).click();const saved=p.getByRole('button',{name:'Keep this session note',exact:true});await saved.waitFor();
check((await saved.boundingBox()).width>900,'Saved note retains full width');await saved.click();
check(await textarea.inputValue()==='Keep this session note','Saved note still reopens');await p.getByRole('button',{name:'Cancel',exact:true}).click();
check(errors.length===0,'No edge-case app page or console errors',errors);
check(!requests.some(r=>r.url.includes('/api/mutations/')),'No mutation decision in edge cases',requests);
await fs.writeFile(out+'/layout-edge-results.json',JSON.stringify({checks,errors,requests,passed:checks.length},null,2));
await browser.close();console.log('PASS '+checks.length+' edge-case checks');
