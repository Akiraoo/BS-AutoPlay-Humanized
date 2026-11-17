"""上揮收勢時劍離頭多近: 每個往上切的方塊 (方向 0/4/5), 切到後 0~0.45 s 內切的那隻手劍身離頭心的最近距離.
usage: upswing_head.py <variants.json> [--maps N]   (和 eval_batch.py 同樣挑譜; 生成檔沿用 eval_batch 的輸出)
"""
import sys, os, json, subprocess
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bsor_stats import load, qrot
import eval_batch as eb
from bomb_eval import seg_dist, POMMEL, S1


def up_dists(path):
    info, fr, notes = load(path)
    fr = fr[np.r_[True, np.diff(fr[:, 0]) > 0]]; t = fr[:, 0]
    hq = fr[:, 4:8] / np.linalg.norm(fr[:, 4:8], axis=1, keepdims=True)
    head = fr[:, 1:4] - qrot(hq, np.array([0, 0, 1.0])) * 0.08
    out = []
    for n in notes:
        if n['type'] != 0: continue
        nid = n['id']; d = nid % 10; color = (nid // 10) % 10
        if d not in (0, 4, 5): continue
        m = (t >= n['t']) & (t <= n['t'] + 0.45)
        if m.sum() < 3: continue
        off = 8 if color == 0 else 15
        P = fr[m, off:off + 3]; Q = fr[m, off + 3:off + 7]; Q = Q / np.linalg.norm(Q, axis=1, keepdims=True)
        f = qrot(Q, np.array([0, 0, 1.0])); w = P - f * POMMEL
        dist, _ = seg_dist(head[m], w, w + f * S1)
        out.append(dist.min())
    return np.array(out)


def main():
    variants = json.load(open(sys.argv[1], encoding='utf-8'))
    opt = lambda k, d: sys.argv[sys.argv.index(k) + 1] if k in sys.argv else d
    maps = eb.pick_maps(int(opt('--maps', '12')), opt('--before', '2026-10-18'), int(opt('--seed', '1')))
    res = {'真人': []}
    for h, diff, real_path, song in maps:
        res['真人'].append(up_dists(real_path))
        for v in variants:
            out = os.path.join(eb.OUT, f"up_{v['name']}_{h[:8]}_{diff}.bsor")
            subprocess.run([eb.TOOL, 'generate', h, diff, v['model'], out] + v.get('args', []), capture_output=True)
            res.setdefault(v['name'], []).append(up_dists(out))
    print('上揮收勢: 劍離頭心最近距離 (cm)  中位 / P10 / <20cm / <25cm / <30cm 的比例')
    for k, v in res.items():
        a = np.concatenate(v) * 100
        print(f'{k:10s} n={len(a):5d}  {np.median(a):5.1f} {np.percentile(a,10):5.1f}  '
              + '  '.join(f'{np.mean(a < x)*100:5.1f}%' for x in (20, 25, 30)))


if __name__ == '__main__':
    main()
