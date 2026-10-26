import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
const root=path.resolve(import.meta.dirname,'../../../..');
const endpoint=JSON.parse(await fs.readFile(path.join(process.env.SQLHARNESS_HOME||path.join(os.homedir(),'.sqlharness'),'dashboard.json'),'utf8'));
const headers={Cookie:`sqlharness_dashboard=${endpoint.token}`},origin=`http://127.0.0.1:${endpoint.port}`;
const read=async route=>{const response=await fetch(origin+route,{headers});if(!response.ok)throw Error('Dashboard GET status '+response.status);return response.json()};
const page=await read('/api/operations?operation=compare&status=succeeded&limit=1');
const operation=page.items?.[0];
const evidence={readOnly:true,tokenRecorded:false,available:!!operation};
if(operation){
 const comparison=await read(`/api/operations/${operation.id}/optimization`);
 Object.assign(evidence,{operationId:operation.id,basisType:typeof comparison.basis,basis:comparison.basis});
 if(process.argv[2]!=='before'&&!['MeasuredTogether','DifferentRunsSameConditions','ConditionsDiffer','SameSql'].includes(comparison.basis))throw Error('Live comparison basis does not match frontend contract');
}
const phase=process.argv[2]==='before'?'before':'after';
await fs.writeFile(path.join(root,`plans/121-evidence/shadcn/mock-alignment/live-basis-${phase}.json`),JSON.stringify(evidence,null,2));
console.log(operation?`Live GET: basis is ${evidence.basisType} (${evidence.basis})`:'No retained compare available; live contract check unavailable');
