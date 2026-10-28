import fs from 'node:fs/promises';
import {pathToFileURL} from 'node:url';
import {setup,launch,sessions} from './layout-polish-fixtures.mjs';
const root=process.env.SQLHARNESS_VISUAL_ROOT,out=root+'/plans/121-evidence/layout-polish/mock-audit-20261028';
const {stats}=await import(pathToFileURL(root+'/src/SqlHarness.Dashboard/ui/src/test/fixtures.ts'));
await fs.mkdir(out,{recursive:true});
const browser=await launch(),p=await browser.newPage({viewport:{width:1280,height:900}}),errors=[],checks=[],pages=[];
p.on('pageerror',e=>errors.push({url:p.url(),error:e.message}));
await setup(p);
const profiles=[{name:'shop-dev',engine:'SqlServer',server:'localhost,14330',database:'{tenant}_shop',auth:'sql',sqlUser:'harness_reader',passwordEnvVar:'SQLH_SHOP_DEV_PW',tls:'optional',rootCertificate:null,vars:[{name:'tenant',rule:'required · enum: acme | globex | initech'},{name:'region',rule:'eu | us'}],environment:'dev',mutationMode:'yolo',mutationWriterKind:'sameAsReader'},{name:'billing-stg',engine:'SqlServer',server:'sql-stg-02.corp.local',database:'billing',auth:'sql',sqlUser:'billing_ro',passwordEnvVar:'SQLH_BILLING_STG_PW',tls:'strict',rootCertificate:'corp-root-ca.pem',vars:[],environment:'staging',mutationMode:'operator',mutationWriterKind:'separate'}];
await p.route('**/api/profiles',r=>r.fulfill({json:{status:'valid',profiles}}));
await p.route('**/api/stats?*',r=>r.fulfill({json:stats()}));
let note=null;
await p.route('**/api/sessions/1',r=>r.fulfill({json:{session:sessions[0],operations:[],note,conversations:[{agentKind:'codex',conversationId:'01a1672f-afbc-7180-993f-70a149e84628',title:'Optimize orders report',operations:114}],compareSubjects:[],unassignedCompareCount:0}}));
await p.route('**/api/sessions/1/annotation',r=>{const body=r.request().postDataJSON();if('note'in body)note=body.note;return r.fulfill({json:{title:null,note,starred:true,archived:false}})});
async function shot(name){await p.waitForTimeout(300);await p.screenshot({path:out+'/'+name+'.png',fullPage:true,animations:'disabled'});}
function check(value,name){checks.push({name,passed:!!value});}
async function visit(name,url){await p.goto(url);await p.waitForTimeout(1200);await shot(name);pages.push({name,url,title:await p.title(),text:(await p.locator('body').innerText()).slice(0,18000),overflow:await p.evaluate(()=>({width:innerWidth,scroll:document.documentElement.scrollWidth})),buttons:await p.getByRole('button').allTextContents()});}
for(const [name,path]of [['activity','/'],['session','/sessions/1'],['operation','/operations/20'],['operation-compare','/operations/23'],['profiles','/profiles'],['usage','/stats'],['settings','/settings']])await visit(name+'-app','http://127.0.0.1:48138'+path);
await p.goto('http://127.0.0.1:48138/');await p.locator('.activity-session-row').first().waitFor();
let row=p.locator('.activity-session-row').first(),button=row.getByRole('button',{name:/Expand|Collapse/}).first();
const prior=await button.getAttribute('aria-expanded');await row.locator('.activity-last-seen').click();check((await button.getAttribute('aria-expanded'))!==prior,'Activity metadata click toggles');
await button.focus();await p.keyboard.press('Enter');check((await button.getAttribute('aria-expanded'))===prior,'Activity disclosure Enter toggles');
await p.mouse.move(0,0);await p.waitForTimeout(200);const bg=await row.evaluate(e=>getComputedStyle(e).backgroundColor);await row.hover();await p.waitForTimeout(200);check((await row.evaluate(e=>getComputedStyle(e).backgroundColor))!==bg,'Activity hover changes background');await shot('activity-hover-app');
await row.getByRole('link').click();check(new URL(p.url()).pathname==='/sessions/1','Activity title link navigates independently');
await p.getByRole('button',{name:'Session metadata'}).click();await shot('session-details-app');
await p.getByRole('button',{name:'Add note',exact:true}).click();await p.getByPlaceholder('Note for this session').fill('Audit note');await p.getByRole('button',{name:'Save note'}).click();await p.getByRole('button',{name:'Audit note',exact:true}).waitFor();await shot('note-saved-app');
await p.getByRole('button',{name:'Audit note',exact:true}).focus();await p.keyboard.press('Enter');check((await p.getByPlaceholder('Note for this session').inputValue())==='Audit note','Saved note Enter reopens editor');await p.getByRole('button',{name:'Cancel',exact:true}).click();
for(const tab of ['SQL diff','IO','Plan']){await p.getByRole('tab',{name:tab,exact:true}).click();await shot('session-'+tab.replace(' ','-').toLowerCase()+'-app');}
await p.getByRole('tab',{name:'IO',exact:true}).click();row=p.locator('.io-table tbody tr').first();button=row.getByRole('button').first();await button.focus();const old=await button.getAttribute('aria-expanded');await p.keyboard.press('Enter');check((await button.getAttribute('aria-expanded'))!==old,'IO disclosure Enter toggles');await row.locator('td').last().click();check((await button.getAttribute('aria-expanded'))===old,'IO metric cell toggles once');
await p.mouse.move(0,0);await p.waitForTimeout(200);const ioBg=await row.evaluate(e=>getComputedStyle(e).backgroundColor);await row.hover({position:{x:8,y:8}});await p.waitForTimeout(200);check((await row.evaluate(e=>getComputedStyle(e).backgroundColor))!==ioBg,'IO expanded row hover changes background');await shot('session-io-hover-app');
await p.goto('http://127.0.0.1:48138/profiles');await p.getByRole('button',{name:/billing-stg/}).click();check(await p.getByRole('heading',{name:'billing-stg',exact:true}).count()===1,'Profile selection updates detail');await p.getByRole('textbox',{name:'Search profiles'}).fill('zz-no-profile');check(await p.getByText('No profile matches.').count()===1,'Profile empty search state');await shot('profiles-empty-app');
for(const [name,path]of [['activity','/'],['session','/sessions/1'],['operation','/operations/20'],['profiles','/profiles'],['usage','/stats'],['settings','/settings']]){await p.setViewportSize({width:390,height:900});await visit(name+'-mobile-app','http://127.0.0.1:48138'+path);}
await p.setViewportSize({width:1280,height:900});
for(const [name,file]of [['activity','Activity'],['session','Session Optimization'],['operation','Operation v2'],['profiles','Profiles'],['usage','Usage'],['settings','Settings'],['mutation','Mutation Approval']])await visit(name+'-mock','http://127.0.0.1:48137/'+encodeURIComponent(file)+'.dc.html');
await p.goto('http://127.0.0.1:48137/Session%20Optimization.dc.html');await p.getByText('Details',{exact:true}).click();await shot('session-details-mock');
for(const tab of ['IO','Plan']){const candidate=p.getByText(tab,{exact:true}).first();await candidate.click();await shot('session-'+tab.toLowerCase()+'-mock');}
await fs.writeFile(out+'/browser-results.json',JSON.stringify({checks,errors,pages},null,2));await browser.close();console.log(JSON.stringify({checks,errors,pages:pages.length}));
