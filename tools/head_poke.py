"""光剑戳头: 剑身 (含握把) 离头中心多近.
usage: head_poke.py <variants.json> [--maps N] [--before YYYY-MM-DD] [--seed S]
和 eval_batch.py 一样挑谱 / 生成, 另外也算真人回放本身.
头 = 头部位置往后 0.08 m 的球心; 剑 = 手腕往前 0..1.6 m 的线段.
统计: 每分钟 "戳头" 次数 (剑离头心 < R 的连续片段, 间隔 0.15 s 内算同一次) 和最近距离分布.
"""
import sys, os, json, subprocess
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bsor_stats import load, qrot
import eval_batch as eb
from bomb_eval import seg_dist, POMMEL, S1

RADII = (0.06, 0.10, 0.14)


def poke(path):
    info, fr, notes = load(path)
    fr = fr[np.r_[True, np.diff(fr[:, 0]) > 0]]
    ts = [n['t'] for n in notes if n['type'] == 0]
    m = (fr[:, 0] >= min(ts)) & (fr[:, 0] <= max(ts)); fr = fr[m]; t = fr[:, 0]
    hq = fr[:, 4:8] / np.linalg.norm(fr[:, 4:8], axis=1, keepdims=True)
    head = fr[:, 1:4] - qrot(hq, np.array([0, 0, 1.0])) * 0.08
    best = np.full(len(t), 9.0); who = np.zeros(len(t), int)
    for k, off in enumerate((8, 15)):
        P = fr[:, off:off + 3]; Q = fr[:, off + 3:off + 7] / np.linalg.norm(fr[:, off + 3:off + 7], axis=1, keepdims=True)
        f = qrot(Q, np.array([0, 0, 1.0])); wrist = P - f * POMMEL
        d, _ = seg_dist(head, wrist, wrist + f * S1)
        who = np.where(d < best, k, who); best = np.minimum(best, d)
    mins = (t[-1] - t[0]) / 60
    res = {}
    for R in RADII:
        idx = np.where(best < R)[0]
        ev = 0 if not len(idx) else 1 + int(np.sum(np.diff(t[idx]) > 0.15))
        res[R] = ev / mins
    return res, best, t, who


def main():
    variants = json.load(open(sys.argv[1], encoding='utf-8'))
    opt = lambda k, d: sys.argv[sys.argv.index(k) + 1] if k in sys.argv else d
    maps = eb.pick_maps(int(opt('--maps', '12')), opt('--before', '2026-10-18'), int(opt('--seed', '1')))
    os.makedirs(eb.OUT, exist_ok=True)
    names = ['真人'] + [v['name'] for v in variants]
    tot = {n: {R: [] for R in RADII} for n in names}
    for h, diff, real_path, song in maps:
        line = f'{song[:24]:24s} {diff:10s}'
        paths = [('真人', real_path)]
        for v in variants:
            out = os.path.join(eb.OUT, f"poke_{v['name']}_{h[:8]}_{diff}.bsor")
            r = subprocess.run([eb.TOOL, 'generate', h, diff, v['model'], out] + v.get('args', []), capture_output=True)
            if r.returncode == 0: paths.append((v['name'], out))
        for n, p in paths:
            res, *_ = poke(p)
            for R in RADII: tot[n][R].append(res[R])
            line += f' | {n} {res[0.10]:5.2f}'
        print(line, flush=True)
    print('\n=== 每分鐘戳頭次數 (劍離頭心 < 6 / 10 / 14 cm), 各譜平均')
    for n in names:
        print(f'{n:16s} ' + '  '.join(f'{np.mean(tot[n][R]):6.2f}' for R in RADII))


if __name__ == '__main__':
    main()
