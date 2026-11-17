"""炸彈多的譜批次比較: 每個變體生成一次, 統計撞到 (<0.20 m) / 擦過 (<0.27 m) 的炸彈數, 以及炸彈經過時 (前後 0.4 s 沒有方塊) 劍的角速度.
usage: bomb_batch.py <variants.json> [--maps N]
"""
import sys, os, json, glob, re, random, subprocess
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import bomb_eval as be, eval_batch as eb
from bsor_stats import load, qrot


def bomb_maps(n):
    G = be.GAME
    hd = json.load(open(os.path.join(G, 'UserData', 'SongCore', 'SongHashData.dat'), encoding='utf-8'))
    by = {v['songHash'].upper(): os.path.join(G, k[2:]) for k, v in hd.items()}
    seen = {}
    for f in glob.glob(os.path.join(G, 'UserData', 'BeatLeader', 'Replays', '*.bsor')):
        m = re.match(r'\d+-(?:exit-)?(.*)-(Expert|ExpertPlus)-Standard-([0-9A-F]{40})-\d+\.bsor$', os.path.basename(f))
        if not m or m.group(3) not in by or (m.group(3), m.group(2)) in seen: continue
        try:
            bombs, njs = be.load_bombs(by[m.group(3)], m.group(2))
        except Exception:
            continue
        seen[(m.group(3), m.group(2))] = (len(bombs), m.group(1))
    top = sorted(seen.items(), key=lambda kv: -kv[1][0])[:n]
    return [(h, d, name) for (h, d), (nb, name) in top]


def saber_speed(path, bombs):
    info, fr, notes = load(path); fr = fr[np.r_[True, np.diff(fr[:, 0]) > 0]]; t = fr[:, 0]
    g = np.arange(t[0], t[-1], 1 / 60)
    ct = np.array(sorted(n['t'] for n in notes if n['type'] == 0)); bt = np.array(sorted(b[0] for b in bombs))
    i = np.searchsorted(ct, g); dc = np.minimum(np.abs(g - ct[np.clip(i - 1, 0, len(ct) - 1)]), np.abs(g - ct[np.clip(i, 0, len(ct) - 1)]))
    i = np.searchsorted(bt, g); db = np.minimum(np.abs(g - bt[np.clip(i - 1, 0, len(bt) - 1)]), np.abs(g - bt[np.clip(i, 0, len(bt) - 1)]))
    m = (dc > 0.4) & (db < 0.15)
    out = []
    for off in (8, 15):
        Q = np.stack([np.interp(g, t, fr[:, off + 3 + j]) for j in range(4)], 1); Q /= np.linalg.norm(Q, axis=1, keepdims=True)
        f = qrot(Q, np.array([0, 0, 1.0]))
        w = np.r_[0, np.degrees(np.arccos(np.clip((f[1:] * f[:-1]).sum(1), -1, 1))) * 60]
        out.append(w[m])
    return np.concatenate(out)


def main():
    variants = json.load(open(sys.argv[1], encoding='utf-8'))
    n = int(sys.argv[sys.argv.index('--maps') + 1]) if '--maps' in sys.argv else 10
    maps = bomb_maps(n)
    tot = {v['name']: [0, 0, 0, []] for v in variants}
    for h, diff, name in maps:
        bombs, njs = be.load_bombs(be.find_folder(h), diff)
        line = f'{name[:22]:22s} {diff:10s} 炸彈 {len(bombs):4d}'
        for v in variants:
            out = os.path.join(eb.OUT, f"bomb_{v['name']}_{h[:8]}_{diff}.bsor")
            if subprocess.run([eb.TOOL, 'generate', h, diff, v['model'], out] + v.get('args', []), capture_output=True).returncode: line += f" | {v['name']} FAIL"; continue
            info, res = be.analyze(out, bombs, njs)
            d = np.array([r['d'] for r in res]); hit = int((d < 0.20).sum()); graze = int((d < 0.27).sum())
            w = saber_speed(out, bombs)
            T = tot[v['name']]; T[0] += hit; T[1] += graze; T[2] += len(d); T[3].append(w)
            line += f" | {v['name']} 撞{hit:3d} 擦{graze:3d} ω{np.median(w) if len(w) else 0:4.0f}"
        print(line, flush=True)
    print('\n=== 合計 撞到(<0.20) / 擦過(<0.27) / 炸彈經過時劍角速度 中位 (真人約 54°/s)')
    for k, (hit, graze, nb, ws) in tot.items():
        w = np.concatenate(ws) if ws else np.zeros(1)
        print(f'{k:12s} 撞 {hit:4d}  擦 {graze:4d}  / {nb} 炸彈   ω 中位 {np.median(w):.0f}°/s  p75 {np.percentile(w,75):.0f}')


if __name__ == '__main__':
    main()
