from pathlib import Path
import json,html
r=Path('plans/121-evidence/visual')
manifest=json.loads((r/'matrix-after.json').read_text());mock={x['name']:x for x in json.loads((r/'matrix-mock.json').read_text())['records']}
rows=[]
for x in manifest['records']:
 n=x['name'];src=f'{n}-mock.png';before=f'{n}-before-baseline.png';after=f'{n}-after.png';compare=f'compare-{n}.png';links=[]
 for label,f in [('Mock',src),('Before',before),('After',after),('Side by side',compare)]:
  if (r/f).exists():links.append(f'<a href="{html.escape(f)}">{label}</a>')
 rows.append(f'<tr><th>{html.escape(n)}</th><td>{x["viewport"]["width"]} × {x["viewport"]["height"]}</td><td>{" · ".join(links)}</td></tr>')
(r/'index.html').write_text('''<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>SQLHarness visual verification</title><style>body{font:14px system-ui,sans-serif;color:#171717;background:#fff;max-width:1200px;margin:32px auto;padding:0 20px}h1{font-size:24px}p{max-width:900px;line-height:1.6}table{border-collapse:collapse;width:100%}td,th{text-align:left;padding:10px;border-bottom:1px solid #e5e5e5}a{color:#075e42}th{font-weight:500}</style><h1>SQLHarness visual verification</h1><p>Selected references: Activity 3a, Operation v2, Session Optimization 4a with IO, Settings. All after views use actual built application components with isolated fixture responses. Fixture values are illustrative; missing journal fields are explicitly unavailable. No target database writes.</p><p>Open a side-by-side sheet, or compare the original screenshots at 100% zoom. Frames are cropped from the design canvas; comparison sheets remove the 1px outer frame border without scaling app content. The matrix JSON files record actual browser viewports.</p><table><thead><tr><th>Screen / state</th><th>Viewport</th><th>Evidence</th></tr></thead><tbody>'''+''.join(rows)+'</tbody></table></html>',encoding='utf-8')
print(f'Gallery: {len(rows)} states; browser errors: {manifest["errors"]}')
