import sys, os, json, numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from wrist_fit_lib import feats
M = json.load(open(sys.argv[1]))
# sphere grid
th = np.radians(np.arange(0, 181, 3)); ph = np.radians(np.arange(-180, 180, 3))
T, P = np.meshgrid(th, ph)
f = np.stack([np.sin(T)*np.cos(P), np.sin(T)*np.sin(P), np.cos(T)], -1).reshape(-1, 3)  # angle T from +z, azimuth P (0=right,90=up)
az = np.degrees(np.arctan2(f[:,1], f[:,0])); ang = np.degrees(np.arccos(np.clip(f[:,2],-1,1)))
for name in ('L','R'):
    C = np.array(M[name]['rollC']).T
    cs = feats(f) @ C; mag = np.linalg.norm(cs, axis=1)
    lim = np.array(M[name]['limit_deg'], dtype=float); cen = np.array(M[name]['limit_az_centers'])
    L = np.interp(az, np.concatenate([cen-360, cen, cen+360]), np.concatenate([lim]*3))
    inside = ang <= L
    print(name, 'roll vector magnitude inside reachable cone: min', mag[inside].min().round(3), 'p5', np.percentile(mag[inside],5).round(3), 'p50', np.median(mag[inside]).round(3))
    worst = np.argsort(np.where(inside, mag, 9))[:5]
    print('   weakest directions (angle-from-fwd, azimuth):', [(round(ang[i]), round(az[i])) for i in worst])
def arr(v): return ', '.join(f'{x:.4f}f' for x in v)
print('\n// ---- C# ----')
for name in ('L','R'):
    m = M[name]
    print(f'// {name}')
    print(f'b = new Vector3({arr(m["b"])});')
    for r in m['A']: print(f'A row: {arr(r)}')
    C = np.array(m['rollC'])
    print(f'rollCos = {{ {arr(C[0])} }};')
    print(f'rollSin = {{ {arr(C[1])} }};')
    print(f'limits = {{ {arr(m["limit_deg"])} }};  centers {m["limit_az_centers"]}')
