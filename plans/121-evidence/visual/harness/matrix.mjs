import {launch,out,setup,settings} from './app-capture.mjs';
import fs from 'node:fs/promises';
const phase=process.argv[2]??'after',origin=process.argv[3]??'http://127.0.0.1:48122';
const b=await launch();const records=[],errors=[];
async function page(width=1280,app=true){const p=await b.newPage({viewport:{width,height:1400},deviceScaleFactor:1,colorScheme:'light'});p.setDefaultTimeout(10000);p.on('pageerror',e=>errors.push(e.message));p.on('console',m=>{if(m.type()==='error')errors.push(m.text());});if(app)await setup(p);return p;}
async function settle(p){await p.evaluate(()=>document.fonts.ready);await p.locator('main').waitFor({state:'visible'});await p.locator('[data-slot=\"skeleton\"]').first().waitFor({state:'hidden',timeout:10000}).catch(()=>{});await p.getByText(/Loading (SQL|plan graph|graphical plan)/).first().waitFor({state:'hidden',timeout:10000}).catch(()=>{});await p.waitForTimeout(250);}
async function snap(p,name,{locator,clip}={}){await settle(p);const path=out+'/'+name+'-'+phase+'.png';if(locator)await locator.screenshot({path});else if(clip)await p.screenshot({path,clip});else await p.screenshot({path,fullPage:true});records.push({name,path,viewport:p.viewportSize(),url:p.url().replace(/\?.*$/,''),size:await p.evaluate(()=>({width:document.documentElement.scrollWidth,height:document.documentElement.scrollHeight}))});}
async function sessionShot(p,name){await settle(p);const box=await p.locator('.optimization-card, section[aria-labelledby=optimization-heading] article').first().boundingBox();await snap(p,name,box?{clip:{x:0,y:0,width:p.viewportSize().width,height:Math.ceil(box.y+box.height+45)}}:{});}
let p=await page();await p.goto(origin+'/');await snap(p,'activity-default');
if(phase==='after'){
 await p.getByRole('button',{name:'Expand all',exact:true}).click();await snap(p,'activity-expanded');await p.getByRole('button',{name:'Collapse all',exact:true}).click();await snap(p,'activity-collapsed');
 await p.getByLabel('Agent',{exact:true}).click();await p.getByRole('checkbox',{name:'claude',exact:true}).check();await p.getByRole('checkbox',{name:'codex',exact:true}).check();await snap(p,'activity-agent-multi');
 await p.getByLabel('Agent',{exact:true}).click();await p.getByRole('textbox',{name:'Search activity'}).fill('orders');await snap(p,'activity-search');await p.getByRole('button',{name:'Reset filters'}).click();await p.getByRole('button',{name:/^Named /}).click();await snap(p,'activity-named');
 await p.getByRole('button',{name:'Reset filters'}).click();await p.getByRole('button',{name:'Include archived',exact:true}).click();await snap(p,'activity-archived');
}
await p.close();
for(const [kind,id,tabs]of [['measure',20,['SQL & plan','IO','Timing','Details']],['query',21,['SQL','Results','Timing']],['mutation',22,['SQL','Approval','Timing']],['compare',23,['SQL diff','IO','Plan','Timing','Details']]]){
 p=await page();await p.goto(origin+'/operations/'+id);for(const tab of tabs){const t=p.getByRole('tab',{name:tab,exact:true});await t.waitFor({state:'visible'});{await t.click();await snap(p,'operation-'+kind+'-'+tab.replaceAll(' ','-').replace('&','and').toLowerCase());}}await p.close();
}
p=await page(1120);await p.goto(origin+'/sessions/1');
for(const tab of ['SQL diff','IO','Plan']){await p.getByRole('tab',{name:tab,exact:true}).click();if(tab==='IO'){await p.getByText('More columns',{exact:true}).click();for(const name of ['Physical','Read-ahead'])await p.getByRole('checkbox',{name,exact:true}).check();await p.getByText('More columns',{exact:true}).click();}if(tab==='Plan')await p.locator('[aria-label=\"Graphical execution plan\"] .react-flow__node').first().waitFor({state:'visible'});await sessionShot(p,'session-'+tab.replaceAll(' ','-').toLowerCase());if(tab==='IO'){
 await p.getByRole('button',{name:'Whole batch',exact:true}).click();await sessionShot(p,'session-io-whole');await p.getByText('More columns',{exact:true}).click();for(const name of ['Physical','Read-ahead','LOB logical'])await p.getByRole('checkbox',{name,exact:true}).check();await p.getByText('More columns',{exact:true}).click();await sessionShot(p,'session-io-whole-columns');await p.getByRole('button',{name:'By statement',exact:true}).click();await sessionShot(p,'session-io-statement-columns');
 }if(tab==='Plan'&&phase==='after'){
 const node=p.getByRole('button',{name:/^Index Seek.*source match$/}).first();await node.waitFor({state:'visible'});await node.click();await sessionShot(p,'session-plan-operator');if(!await p.getByRole('complementary',{name:'Plan operator properties'}).isVisible())throw Error('Operator click did not open properties');
 }}
if(phase==='after'){
 await p.getByRole('radio',{name:'A v1',exact:true}).check();if(!p.url().includes('a=v1'))throw Error('A selection not URL-backed');await sessionShot(p,'session-ab-v1-v2');
 await p.getByRole('tab',{name:'SQL diff',exact:true}).click();await p.getByRole('radio',{name:'Unified',exact:true}).check();await sessionShot(p,'session-sql-unified');await p.getByRole('checkbox',{name:'Ignore formatting',exact:true}).check();await sessionShot(p,'session-sql-ignore-formatting');
}
await p.close();
p=await page();await p.goto(origin+'/settings');await snap(p,'settings-full');for(const key of ['database','mutations','journal','agents','dashboard','prices']){await snap(p,'settings-'+key,{locator:p.locator('#settings-'+key)});}
if(phase==='after'){
 await p.getByRole('link',{name:'Dashboard',exact:true}).click();if(await p.getByRole('link',{name:'Dashboard',exact:true}).getAttribute('aria-current')!=='location')throw Error('Settings section selection missing');
 const field=p.getByRole('spinbutton',{name:'Connection timeout (seconds)'});await field.fill('45');await field.press('Tab');await p.waitForTimeout(300);if(settings.settings.connection.timeoutSeconds!==45)throw Error('Setting did not persist through fixture API');
 await p.getByRole('switch',{name:'Store sensitive data',exact:true}).click();await p.getByRole('switch',{name:'Store sensitive data',exact:true}).click();await p.getByRole('dialog').waitFor();await snap(p,'settings-sensitive-confirmation');await p.getByRole('button',{name:'Cancel',exact:true}).click();
}
await p.close();
for(const route of ['/','/settings','/operations/20','/sessions/1']){p=await page(390);await p.goto(origin+route);if(route==='/sessions/1')await p.getByRole('tab',{name:'IO',exact:true}).click();await snap(p,'narrow-'+(route==='/'?'activity':route.split('/')[1]));const overflow=await p.evaluate(()=>({scroll:document.documentElement.scrollWidth,client:document.documentElement.clientWidth}));if(phase==='after'&&overflow.scroll>overflow.client+1)errors.push('PAGE OVERFLOW '+route+' '+JSON.stringify(overflow));await p.close();}
await fs.writeFile(out+'/matrix-'+phase+'.json',JSON.stringify({phase,origin,records,errors},null,2));await b.close();console.log(JSON.stringify({phase,shots:records.length,errors}));if(errors.length)process.exitCode=1;
