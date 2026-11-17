"""點塊實際的切割角度 (劍尖在 x-y 平面的速度方向) 和前後音符位置的關係.
usage: dot_angle_stats.py [--n 150]
角度: 0 = 往上, 順時針為正 (90 = 往右, 180 = 往下), 左手映象成右手 (往外為正).
"""
import sys, os, glob, random
import numpy as np
from bsor_stats import load, qrot
import eval_batch as eb

ANG = {0: 0, 1: 180, 2: 270, 3: 90, 4: 315, 5: 45, 6: 225, 7: 135}


def collect(n_files=150, seed=2, before=None):
    files = [f for f in glob.glob(os.path.join(eb.GAME, 'UserData', 'BeatLeader', 'Replays', '76561199440901687-*.bsor'))
             if 'exit-' not in f and '-Standard-' in f]
    random.Random(seed).shuffle(files)
    rows = []
    for f in files[:n_files]:
        try: info, fr, notes = load(f)
        except Exception: continue
        if info['leftHanded']: continue
        fr = fr[np.r_[True, np.diff(fr[:, 0]) > 0]]; t = fr[:, 0]
        for c in (0, 1):
            off = 8 if c == 0 else 15; m = -1 if c == 0 else 1
            seq = sorted([n for n in notes if n['type'] == 0 and (n['id'] // 10) % 10 == c and n['id'] // 10000 - 2 == 1], key=lambda n: n['t'])
            for j in range(1, len(seq) - 1):
                n = seq[j]; i = n['id']
                if i % 10 != 8: continue
                k = np.searchsorted(t, n['t'])
                if k < 3 or k > len(t) - 4: continue
                def tip(q_):
                    q = fr[q_, off + 3:off + 7]; q = q / np.linalg.norm(q); return fr[q_, off:off + 3] + qrot(q[None], np.array([0, 0, 1.0]))[0]
                v = tip(k + 2) - tip(k - 2)
                if np.hypot(v[0], v[1]) < 0.05: continue
                ang = np.degrees(np.arctan2(v[0] * m, v[1]))       # 0 = 上, 往外為正
                pos = lambda nn: np.array([((nn['id'] // 1000) % 10 - 1.5) * 0.6 * m, ((nn['id'] // 100) % 10) * 0.53])
                p0, p1, p2 = pos(seq[j - 1]), pos(n), pos(seq[j + 1])
                rows.append(dict(ang=ang, dprev=p1 - p0, dnext=p2 - p1, gprev=n['t'] - seq[j - 1]['t'], gnext=seq[j + 1]['t'] - n['t'],
                                 layer=(i // 100) % 10, lane=(i // 1000) % 10, prevdir=seq[j - 1]['id'] % 10, nextdir=seq[j + 1]['id'] % 10))
    return rows


if __name__ == '__main__':
    R = collect()
    a = np.array([r['ang'] for r in R]); down = np.abs(a) > 90
    dev = np.where(down, np.sign(a) * (180 - np.abs(a)), a)     # 偏離垂直的角度 (往下時: 正 = 往外下)
    print(f'{len(R)} 個點塊.  往下 {down.mean()*100:.0f}%')
    print(f'偏離垂直 |dev|: 中位 {np.median(np.abs(dev)):.0f}°  P75 {np.percentile(np.abs(dev),75):.0f}°  P90 {np.percentile(np.abs(dev),90):.0f}°  >20° {np.mean(np.abs(dev)>20)*100:.0f}%  >35° {np.mean(np.abs(dev)>35)*100:.0f}%')
    for nm, msk in (('往上', ~down), ('往下', down)):
        print(f'  {nm}: dev 平均 {dev[msk].mean():+.1f}° (正 = 往外)')
    # 回歸: dev ~ 前一塊->這塊 的水平位移 / 這塊->下一塊 的水平位移 (往外為正), 按上下分開
    for nm, msk in (('往上', ~down), ('往下', down)):
        X = np.array([[1, r['dprev'][0], r['dnext'][0], r['dprev'][1], r['dnext'][1]] for r, k in zip(R, msk) if k])
        y = dev[msk]
        w, *_ = np.linalg.lstsq(X, y, rcond=None); pred = X @ w
        print(f'  {nm}: dev = {w[0]:+.1f} {w[1]:+.1f}*dx_prev {w[2]:+.1f}*dx_next {w[3]:+.1f}*dy_prev {w[4]:+.1f}*dy_next   (每米, 度)  R2 {1-np.var(y-pred)/np.var(y):.2f}  殘差中位 {np.median(np.abs(y-pred)):.0f}°')
