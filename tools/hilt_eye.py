"""劍柄插眼睛: 劍柄 (手腕 ~ 控制器往前 0.05 m) 離頭顯 (眼睛) 多近.
usage: hilt_eye.py <variants.json> [--maps N]   (和 eval_batch.py 同樣挑譜)
每分鐘 劍柄離眼睛 < R 的次數 (間隔 0.15 s 內算同一次), 以及每隻手離眼睛最近的分佈.
"""
import sys, os, json, subprocess
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bsor_stats import load, qrot
import eval_batch as eb
from bomb_eval import seg_dist, POMMEL

RADII = (0.10, 0.15, 0.20)


def hilt(path):
    info, fr, notes = load(path)
    fr = fr[np.r_[True, np.diff(fr[:, 0]) > 0]]
    ts = [n['t'] for n in notes if n['type'] == 0]
    fr = fr[(fr[:, 0] >= min(ts)) & (fr[:, 0] <= max(ts))]; t = fr[:, 0]
    eye = fr[:, 1:4]
    best = np.full(len(t), 9.0); rel = np.zeros((len(t), 3))
    hq = fr[:, 4:8] / np.linalg.norm(fr[:, 4:8], axis=1, keepdims=True)
    for off in (8, 15):
        P = fr[:, off:off + 3]; Q = fr[:, off + 3:off + 7] / np.linalg.norm(fr[:, off + 3:off + 7], axis=1, keepdims=True)
        f = qrot(Q, np.array([0, 0, 1.0])); w = P - f * POMMEL
        d, q = seg_dist(eye, w, P + f * 0.05)
        m = d < best; best = np.where(m, d, best); rel[m] = (q - eye)[m]
    mins = (t[-1] - t[0]) / 60
    ev = {}
    for R in RADII:
        idx = np.where(best < R)[0]
        ev[R] = (0 if not len(idx) else 1 + int(np.sum(np.diff(t[idx]) > 0.15))) / mins
    return ev, best, rel


def main():
    variants = json.load(open(sys.argv[1], encoding='utf-8'))
    opt = lambda k, d: sys.argv[sys.argv.index(k) + 1] if k in sys.argv else d
    maps = eb.pick_maps(int(opt('--maps', '12')), opt('--before', '2026-10-18'), int(opt('--seed', '1')))
    names = ['真人'] + [v['name'] for v in variants]
    tot = {n: [] for n in names}; close = {n: [] for n in names}
    for h, diff, real_path, song in maps:
        paths = [('真人', real_path)]
        for v in variants:
            out = os.path.join(eb.OUT, f"hilt_{v['name']}_{h[:8]}_{diff}.bsor")
            if subprocess.run([eb.TOOL, 'generate', h, diff, v['model'], out] + v.get('args', []), capture_output=True).returncode == 0:
                paths.append((v['name'], out))
        for n, p in paths:
            ev, best, rel = hilt(p); tot[n].append(ev); close[n].append(rel[best < 0.20])
    print('每分鐘 劍柄離眼睛 < 10 / 15 / 20 cm 的次數; < 20 cm 時劍柄在眼睛的哪裡 (x 往外不分左右, y 上, z 前) 中位')
    for n in names:
        c = np.concatenate(close[n]) if close[n] else np.zeros((0, 3))
        print(f'{n:10s} ' + '  '.join(f'{np.mean([e[R] for e in tot[n]]):5.2f}' for R in RADII)
              + (f'   x {np.median(np.abs(c[:,0]))*100:4.1f} y {np.median(c[:,1])*100:5.1f} z {np.median(c[:,2])*100:5.1f} cm' if len(c) else ''))


if __name__ == '__main__':
    main()
