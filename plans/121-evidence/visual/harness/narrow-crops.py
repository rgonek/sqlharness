from PIL import Image
from pathlib import Path
r=Path('D:/Dev/sqlharness/.worktrees/dashboard-visual-fidelity/plans/121-evidence/visual')
for name in ['narrow-activity','narrow-settings','narrow-operations','narrow-sessions']:
 p=r/f'{name}-after.png';im=Image.open(p);im.crop((0,0,im.width,min(1000,im.height))).save(r/f'{name}-detail.png')
