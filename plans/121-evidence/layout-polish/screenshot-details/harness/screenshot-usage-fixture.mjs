import {pathToFileURL} from 'node:url';
const root=process.env.SQLHARNESS_VISUAL_ROOT;
const {stats}=await import(pathToFileURL(root+'/src/SqlHarness.Dashboard/ui/src/test/fixtures.ts'));
/** Synthetic six-operation cohort; this is presentation evidence, not backend acceptance. */
export function usageData(){
  const data=stats();
  const unique=[...new Map([...data.topSqlByCount,...data.topSqlByDuration].map(r=>[r.sqlHash,r])).values()];
  const rows=unique.map(r=>({...r,logicalReadsMedianSum:r.count*1250,logicalReadsAvailableOperations:r.count,logicalReadsUnavailableOperations:0,logicalReadsAvailableMetrics:r.count,logicalReadsUnavailableMetrics:0}));
  rows.push({sqlHash:'c'.repeat(64),count:1,totalDurationMs:100,maxDurationMs:100,logicalReadsMedianSum:100000,logicalReadsAvailableOperations:1,logicalReadsUnavailableOperations:0,logicalReadsAvailableMetrics:1,logicalReadsUnavailableMetrics:0});
  return {...data,sessionCount:2,topSqlByCount:[...rows].sort((a,b)=>b.count-a.count),topSqlByDuration:[...rows].sort((a,b)=>b.totalDurationMs-a.totalDurationMs),topSqlByLogicalReads:[...rows].sort((a,b)=>b.logicalReadsMedianSum-a.logicalReadsMedianSum)};
}
