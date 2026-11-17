"""比较生成的回放和真人回放 (同一张谱): 剑的指向误差、手的高度、两刀之间的速度、收势 / 预备幅度.
usage: eval_replay.py <生成.bsor> <真人.bsor> [更多真人.bsor ...]
       多份真人回放时, 另外印出真人彼此之间的差异当作基准 (生成的回放离真人多远才算"像人").
"""
import sys, os, re
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bsor_stats import load, qrot

GAP_BINS = [(0, 0.25), (0.25, 0.45), (0.45, 0.8), (0.8, 1.5), (1.5, 99)]
PHASES = [(0, .2), (.2, .4), (.4, .6), (.6, .8), (.8, 1.0)]
TAUS = np.array([-0.4, -0.3, -0.2, -0.15, -0.1, -0.05, 0, 0.05, 0.1, 0.15, 0.2, 0.3, 0.4])


def unit(v): return v / np.linalg.norm(v, axis=-1, keepdims=True)


def prep(p):
    info, fr, notes = load(p)
    fr = fr[np.r_[True, np.diff(fr[:, 0]) > 0]]
    t = fr[:, 0]
    hf = qrot(fr[:, 4:8], np.array([0, 0, 1.0])); psi = np.arctan2(hf[:, 0].mean(), hf[:, 2].mean())
    c, s = np.cos(-psi), np.sin(-psi)
    R = lambda v: np.stack([c * v[:, 0] + s * v[:, 2], v[:, 1], -s * v[:, 0] + c * v[:, 2]], 1)
    d = dict(info=info, notes=notes, t=t, head=R(fr[:, 1:4]))
    for h, off, st in (('L', 8, 0), ('R', 15, 1)):
        d[h + 'pos'] = R(fr[:, off:off + 3]); d[h + 'f'] = R(qrot(fr[:, off + 3:off + 7], np.array([0, 0, 1.0])))
        cuts = np.array(sorted(n['t'] for n in notes if n['type'] == 0 and n['cut'] and n['cut']['stype'] == st))
        sw = [cuts[0]] if len(cuts) else []
        for x in cuts[1:]:
            if x - sw[-1] > 0.06: sw.append(x)
        d[h + 'sw'] = np.array(sw)
    m = re.search(rb'"modelName":"([^"]*)"', open(p, 'rb').read())
    d['model'] = m.group(1).decode() if m else None
    return d


def at(d, k, g): return np.stack([np.interp(g, d['t'], d[k][:, j]) for j in range(3)], 1)


def compare(a, b, label):
    ts = [n['t'] for n in b['notes'] if n['type'] == 0]
    g = np.arange(min(ts), max(ts), 0.01)
    print(f'--- {label}')
    print(f"   頭高中位 {np.median(at(a, 'head', g)[:, 1]):.3f} vs {np.median(at(b, 'head', g)[:, 1]):.3f} m")
    for h in 'LR':
        fa, fb = unit(at(a, h + 'f', g)), unit(at(b, h + 'f', g))
        err = np.degrees(np.arccos(np.clip((fa * fb).sum(1), -1, 1)))
        pa = at(a, h + 'pos', g) - at(a, 'head', g); pb = at(b, h + 'pos', g) - at(b, 'head', g)
        sp = lambda f: np.degrees(np.arccos(np.clip((f[1:] * f[:-1]).sum(1), -1, 1))) / 0.01
        print(f"   {h}: 方向誤差 p50 {np.percentile(err, 50):5.1f}°  p75 {np.percentile(err, 75):5.1f}°  >75° {np.mean(err > 75):5.1%} | "
              f"手比頭低 {pa[:, 1].mean():+.3f} vs {pb[:, 1].mean():+.3f} m | 角速度 p50 {np.percentile(sp(fa), 50):4.0f} vs {np.percentile(sp(fb), 50):4.0f} °/s")


def phase_table(a, b):
    print('--- 兩刀之間的角速度中位數 (°/s, 生成|真人), 依間隔長度 × 位置 (0=剛打完 → 1=下一刀前)')
    for h in 'LR':
        rows = {}
        for nm, d in (('a', a), ('b', b)):
            g = np.arange(d['t'][0], d['t'][-1], 1 / 90)
            F = unit(at(d, h + 'f', g))
            w = np.degrees(np.arccos(np.clip((F[1:] * F[:-1]).sum(1), -1, 1))) * 90
            sw = d[h + 'sw']; gg = g[1:]
            i = np.searchsorted(sw, gg); ok = (i > 0) & (i < len(sw))
            prev = np.where(ok, sw[np.clip(i - 1, 0, len(sw) - 1)], np.nan); nxt = np.where(ok, sw[np.clip(i, 0, len(sw) - 1)], np.nan)
            rows[nm] = (w, nxt - prev, (gg - prev) / (nxt - prev))
        print(f'   {h}:')
        for lo, hi in GAP_BINS:
            cells = []
            for u0, u1 in PHASES:
                v = []
                for nm in 'ab':
                    w, gap, u = rows[nm]; m = (gap >= lo) & (gap < hi) & (u >= u0) & (u < u1)
                    v.append(np.median(w[m]) if m.sum() > 20 else np.nan)
                cells.append(f'{v[0]:5.0f}|{v[1]:<5.0f}')
            print(f'     間隔 {lo:4.2f}-{hi:5.2f}s: ' + ' '.join(cells))


def follow(a, b):
    print('--- 打完前後光劍離命中方向的角度 (°): 預備 ← 0 → 收勢 (間隔 0.45-0.8s)')
    print('     τ(s) ' + ' '.join(f'{x:+5.2f}' for x in TAUS))
    for nm, d in (('生成', a), ('真人', b)):
        res = []
        for h in 'LR':
            sw = d[h + 'sw']
            for i in range(1, len(sw) - 1):
                if not (0.45 <= sw[i + 1] - sw[i] < 0.8 and 0.45 <= sw[i] - sw[i - 1] < 0.8): continue
                f0 = unit(at(d, h + 'f', np.array([sw[i]])))[0]
                res.append(np.degrees(np.arccos(np.clip(unit(at(d, h + 'f', sw[i] + TAUS)) @ f0, -1, 1))))
        if res: print(f'     {nm} ' + ' '.join(f'{x:5.0f}' for x in np.median(res, 0)) + f'   (n={len(res)})')


def main():
    gen = prep(sys.argv[1]); reals = [prep(p) for p in sys.argv[2:]]
    gi = gen['info']
    print(f"{gi['songName']} [{gi['difficulty']}]  模型 {gen['model']}  身高設定 {gi['height']:.2f}")
    for k, r in enumerate(reals):
        compare(gen, r, f"生成 vs 真人 #{k + 1} (身高設定 {r['info']['height']:.2f}, 分數 {r['info']['score']})")
    for k in range(1, len(reals)):
        compare(reals[0], reals[k], f'基準: 真人 #1 vs 真人 #{k + 1}')
    if reals:
        phase_table(gen, reals[0])
        follow(gen, reals[0])


if __name__ == '__main__':
    main()
