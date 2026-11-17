"""點塊方向 (往上 / 往下揮) 和真人同一張譜的回放比對.
usage: dot_layer_check.py <variants.json> [--maps N]   (和 eval_batch.py 同樣挑譜)
"""
import sys, os, json, subprocess
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bsor_stats import load, qrot
import eval_batch as eb


def dots(path):
    info, fr, notes = load(path); fr = fr[np.r_[True, np.diff(fr[:, 0]) > 0]]; t = fr[:, 0]
    out = {}
    for n in notes:
        if n['type'] != 0 or n['id'] % 10 != 8: continue
        c = (n['id'] // 10) % 10; off = 8 if c == 0 else 15; k = np.searchsorted(t, n['t'])
        if k < 2 or k > len(t) - 3: continue
        def tip(j):
            q = fr[j, off + 3:off + 7]; q = q / np.linalg.norm(q); return fr[j, off:off + 3] + qrot(q[None], np.array([0, 0, 1.0]))[0]
        v = tip(k + 2) - tip(k - 2)
        out[(n['id'], round(n['t'], 1))] = (v[1] > 0, np.degrees(np.arctan2(v[0], v[1])))
    return out


def main():
    variants = json.load(open(sys.argv[1], encoding='utf-8'))
    n = int(sys.argv[sys.argv.index('--maps') + 1]) if '--maps' in sys.argv else 12
    maps = eb.pick_maps(n, '2026-10-18', 1)
    agree = {v['name']: [0, 0] for v in variants}; angerr = {v['name']: [] for v in variants}; absdev = {v['name']: [] for v in variants}
    realdev = []
    for h, diff, rp, song in maps:
        real = dots(rp); line = f'{song[:24]:24s} dots {len(real):4d}'
        realdev += [min(abs(a), 180 - abs(a)) for _, a in real.values()]
        for v in variants:
            out = os.path.join(eb.OUT, f"dot_{v['name']}_{h[:8]}_{diff}.bsor")
            subprocess.run([eb.TOOL, 'generate', h, diff, v['model'], out] + v.get('args', []), capture_output=True)
            g = dots(out); ks = [k for k in g if k in real]
            a = sum(g[k][0] == real[k][0] for k in ks); agree[v['name']][0] += a; agree[v['name']][1] += len(ks)
            angerr[v['name']] += [abs((g[k][1] - real[k][1] + 180) % 360 - 180) for k in ks if g[k][0] == real[k][0]]
            absdev[v['name']] += [min(abs(g[k][1]), 180 - abs(g[k][1])) for k in ks]
            line += f' | {v["name"]} {a / max(1, len(ks)) * 100:5.1f}%'
        print(line, flush=True)
    print('\n=== 點塊上/下方向和真人一致的比例')
    print(f'真人: 偏離正上下 中位 {np.median(realdev):.0f}°  >20° {np.mean(np.array(realdev) > 20) * 100:.0f}%')
    for k, (a, n_) in agree.items():
        e = np.array(angerr[k]); d = np.array(absdev[k])
        print(f'{k:10s} 上下一致 {a / max(1, n_) * 100:5.1f}%  ({n_})   同向時角度差 中位 {np.median(e):.0f}° P75 {np.percentile(e, 75):.0f}°   偏離正上下 中位 {np.median(d):.0f}° >20° {np.mean(d > 20) * 100:.0f}%')


if __name__ == '__main__':
    main()
