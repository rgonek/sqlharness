import { setup, launch } from './app-capture.mjs';
import fs from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
const out=new URL('../../shadcn/mock-alignment/focused/',import.meta.url);await fs.mkdir(out,{recursive:true});
const browser=await launch();
try{
 const mock=await browser.newPage({viewport:{width:1280,height:1400}});
 await mock.goto('http://127.0.0.1:48121/Session%20Optimization.dc.html');
 const frame=mock.locator('[id="4a"] > div').last();
 const panel=frame.getByText('Optimization',{exact:true}).locator('..').locator('..').locator('..');
 const width=Math.round((await panel.boundingBox()).width);
 const app=await browser.newPage({viewport:{width:width+32,height:1400}});await setup(app);await app.goto('http://127.0.0.1:48126/sessions/1');
 const records=[];
 for(const tab of ['SQL diff','IO','Plan']){
  await frame.getByText(tab,{exact:true}).first().click();await app.getByRole('tab',{name:tab,exact:true}).click();
  if(tab==='Plan')await app.locator('[aria-label="Graphical execution plan"] .react-flow__node').first().waitFor();
  if(tab==='SQL diff')await app.locator('.monaco-editor:visible').first().waitFor();
  await Promise.all([mock.evaluate(()=>document.fonts.ready),app.evaluate(()=>document.fonts.ready)]);await app.waitForTimeout(200);
  const key=tab.replaceAll(' ','-');
  await panel.screenshot({path:fileURLToPath(new URL(key+'-mock.png',out))});
  const actual=app.locator('.optimization-card').first();await actual.screenshot({path:fileURLToPath(new URL(key+'-app.png',out))});
  const appWidth=(await actual.boundingBox()).width;
  if(Math.abs(appWidth-width)>1)throw Error('Reference/app widths differ');
  if(tab==='Plan'){
   const radio=await app.getByRole('radiogroup',{name:'Plan version',exact:true}).boundingBox();
   const caseBox=await app.getByRole('combobox',{name:'Case',exact:true}).boundingBox();
   if(Math.abs(radio.height-28)>1||Math.abs(radio.y-caseBox.y)>1)throw Error('Plan controls do not share a compact 28px toolbar');
  }
  records.push({tab,mockWidth:width,appWidth,scaled:false});
 }
 await fs.writeFile(new URL('results.json',out),JSON.stringify({records},null,2));console.log('PASS focused reference/app pairs: same width, no scaling');
}finally{await browser.close()}
