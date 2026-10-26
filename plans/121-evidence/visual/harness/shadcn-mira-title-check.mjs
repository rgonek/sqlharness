import { setup, launch, sessions } from './app-capture.mjs';
import fs from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
const out = new URL('../../shadcn/followup/mira/', import.meta.url);
const browser = await launch();
const results=[];
try {
 const page=await browser.newPage();await setup(page);
 sessions[0].title='Bardzo długi tytuł '.repeat(50);
 for(const width of [1280,768,390]){
  await page.setViewportSize({width,height:900});
  await page.goto('http://127.0.0.1:48126/sessions/1');
  const star=page.getByRole('button',{name:'Unstar session',exact:true});await star.waitFor();
  await page.locator('.monaco-editor:visible').first().waitFor();
  const font=await page.locator('h1').evaluate(e=>getComputedStyle(e).fontFamily);
  if(!font.includes('Inter'))throw Error('Mira font is not Inter: '+font);
  const before=await star.boundingBox();
  await page.getByRole('button',{name:'Edit title',exact:true}).click();
  const after=await star.boundingBox();
  if(Math.abs(before.x-after.x)>1||Math.abs(before.y-after.y)>1)throw Error('Title editing moves actions at '+width);
  if(!await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth))throw Error('Title overflows at '+width);
  await page.screenshot({path:fileURLToPath(new URL('long-title-'+width+'.png',out)),fullPage:true});
  results.push({width,before,after,font});
 }
 await fs.writeFile(new URL('title-results.json',out),JSON.stringify({titleCharacters:sessions[0].title.length,results},null,2));
 console.log('PASS long titles at 3 widths; Inter font; stable actions');
}finally{await browser.close()}
