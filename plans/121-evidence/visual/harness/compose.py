from PIL import Image,ImageOps,ImageDraw
from pathlib import Path
r=Path(__file__).resolve().parent.parent
for name in ['activity-default','operation-measure-sql-and-plan','operation-query-results','operation-mutation-approval','operation-compare-sql-diff','session-sql-diff','session-io','session-io-whole','session-plan','settings-full','settings-database','settings-mutations']:
 a=Image.open(r/f'{name}-mock.png').convert('RGB');b=Image.open(r/f'{name}-after.png').convert('RGB')
 if a.width==b.width+2:a=a.crop((1,1,a.width-1,a.height-1))
 width=max(a.width,b.width);height=max(a.height,b.height)
 c=Image.new('RGB',(width*2,height+28),'#e5e5e5');c.paste(a,(0,28));c.paste(b,(width,28));d=ImageDraw.Draw(c);d.text((12,8),'REFERENCE',fill='black');d.text((width+12,8),'APPLICATION',fill='black');c.save(r/f'compare-{name}.png')
 if name in ['session-plan','operation-measure-sql-and-plan','activity-default']:
  h=480 if name=='session-plan' else 330
  ac=a.crop((0,0,a.width,min(h,a.height)));bc=b.crop((0,0,b.width,min(h,b.height)));c=Image.new('RGB',(width*2,h+28),'#e5e5e5');c.paste(ac,(0,28));c.paste(bc,(width,28));c.save(r/f'detail-{name}.png')
