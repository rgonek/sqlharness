import fs from 'node:fs/promises';import path from 'node:path';
const base='plans/121-evidence/layout-polish/screenshot-details',files=[base+'/report.md',base+'/index.html'];let count=0;
for(const f of files){const s=await fs.readFile(f,'utf8');const links=f.endsWith('.md')?[...s.matchAll(/\]\(([^)]+)\)/g)].map(m=>m[1]):[...s.matchAll(/(?:href|src)="([^"]+)"/g)].map(m=>m[1]);for(const l of links){if(l.startsWith('#')||l.includes('${')||/^https?:/.test(l))continue;await fs.access(path.resolve(path.dirname(f),l.split('#')[0]));count++;}}
const html=await fs.readFile(files[1],'utf8');for(const m of html.matchAll(/\['[^']+','([^']+)','([^']+)'\]/g))for(const name of [m[1],m[2]]){await fs.access(base+'/'+name+'.png');count++;}console.log('PASS '+count+' evidence links/assets');
