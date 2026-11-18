"""Compare an BotPlay replay with real replays of the same map.
  - per-note swing direction at the cut (BotPlay vs. what the human did), dots (no arrow) listed separately
  - stretches where BotPlay's saber points somewhere very different from every human replay
usage: compare_map.py <botplay.bsor> <real1.bsor> [real2.bsor ...]
"""
import sys, os
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bsor_stats import load, qrot

DIRNAME = ['U', 'D', 'L', 'R', 'UL', 'UR', 'DL', 'DR', 'dot']

def unit(v): return v / np.linalg.norm(v, axis=-1, keepdims=True)

def prep(path):
    info, fr, notes = load(path)
    t = fr[:, 0]
    keep = np.r_[True, np.diff(t) > 0]; fr = fr[keep]; t = fr[:, 0]
    hf = qrot(fr[:, 4:8], np.array([0, 0, 1.0])); psi = np.arctan2(hf[:, 0].mean(), hf[:, 2].mean())
    c, s = np.cos(-psi), np.sin(-psi)
    F = {}
    for hand, off in (('L', 8), ('R', 15)):
        f = qrot(fr[:, off + 3:off + 7], np.array([0, 0, 1.0]))
        F[hand] = np.stack([c * f[:, 0] + s * f[:, 2], f[:, 1], -s * f[:, 0] + c * f[:, 2]], 1)
    ev = [(n['id'], n['t']) for n in notes if n['type'] == 0]
    return info, t, F, ev

def fat(t, f, tt):
    return unit(np.stack([np.interp(tt, t, f[:, k]) for k in range(3)], -1))

def swing2d(t, f, tc):
    d = fat(t, f, tc + 0.012) - fat(t, f, tc - 0.012)
    d2 = d[..., :2]
    return d2 / np.maximum(np.linalg.norm(d2, axis=-1, keepdims=True), 1e-9)

def label(v):
    ang = np.degrees(np.arctan2(v[0], v[1])) % 360   # 0=up 90=right
    names = ['U', 'UR', 'R', 'DR', 'D', 'DL', 'L', 'UL']
    return names[int(((ang + 22.5) % 360) // 45)]

def main():
    auto = prep(sys.argv[1]); reals = [prep(p) for p in sys.argv[2:]]
    _, ta, Fa, eva = auto
    print('BotPlay notes', len(eva), '| real replays', len(reals))

    rows = []
    for nid, te in eva:
        hand = 'L' if (nid // 10) % 10 == 0 else 'R'
        cd = nid % 10
        da = swing2d(ta, Fa[hand], np.array(te))
        hs = []
        for _, tr, Fr, evr in reals:
            m = [x for x in evr if x[0] == nid and abs(x[1] - te) < 0.03]
            if m: hs.append(swing2d(tr, Fr[hand], np.array(m[0][1])))
        if not hs: continue
        hm = np.mean(hs, 0); hm /= max(np.linalg.norm(hm), 1e-9)
        agree = float(np.mean([np.dot(h, hm) for h in hs]))
        rows.append((te, hand, cd, da, hm, agree, len(hs)))

    def report(sel, title):
        if not sel: return
        dots = np.array([np.dot(r[3], r[4]) for r in sel])
        print(f'\n== {title}: {len(sel)} notes | BotPlay vs human swing direction: same(<45°) {np.mean(dots > 0.707):.0%}  opposite(>135°) {np.mean(dots < -0.707):.0%}')
        return dots

    report([r for r in rows if r[2] != 8], 'arrow notes')
    dsel = [r for r in rows if r[2] == 8]
    dd = report(dsel, 'dot notes')
    if dsel:
        print('   dot notes where BotPlay disagrees with a consistent human choice (first 25):')
        k = 0
        for r, d in zip(dsel, dd):
            if d < 0.3 and r[5] > 0.7:
                prev = [x for x in rows if x[1] == r[1] and x[0] < r[0]][-1:]
                nxt = [x for x in rows if x[1] == r[1] and x[0] > r[0]][:1]
                ctx = ''
                if prev: ctx += f' prev {DIRNAME[prev[0][2]]}@{r[0]-prev[0][0]:.2f}s'
                if nxt: ctx += f' next {DIRNAME[nxt[0][2]]}@{nxt[0][0]-r[0]:.2f}s'
                print(f'     t={r[0]:7.2f} {r[1]} auto={label(r[3]):2} human={label(r[4]):2} (agree {r[5]:.2f}, n={r[6]}){ctx}')
                k += 1
                if k >= 25: break

    # ---- divergence of saber pointing direction over time ----
    print('\n== stretches where BotPlay points >75° away from every human replay for >0.25s')
    grid = np.arange(max(ta[0], max(r[1][0] for r in reals)), min(ta[-1], min(r[1][-1] for r in reals)), 0.01)
    for hand in ('L', 'R'):
        fa = fat(ta, Fa[hand], grid)
        mins = np.full(len(grid), 180.0)
        for _, tr, Fr, _ in reals:
            fr_ = fat(tr, Fr[hand], grid)
            ang = np.degrees(np.arccos(np.clip((fa * fr_).sum(1), -1, 1)))
            mins = np.minimum(mins, ang)
        bad = mins > 75
        i = 0; segs = []
        while i < len(grid):
            if bad[i]:
                j = i
                while j < len(grid) and bad[j]: j += 1
                if grid[j - 1] - grid[i] >= 0.25: segs.append((grid[i], grid[j - 1], mins[i:j].max()))
                i = j
            else: i += 1
        print(f' {hand}: {len(segs)} stretches, total {sum(b - a for a, b, _ in segs):.1f}s of {grid[-1] - grid[0]:.0f}s')
        for a, b, mx in segs[:15]:
            fa_mid = fat(ta, Fa[hand], np.array((a + b) / 2))
            near = [r for r in rows if r[1] == hand and a - 1.0 < r[0] < b + 1.0]
            ctx = ' '.join(f'{DIRNAME[r[2]]}@{r[0]:.2f}' for r in near[:8])
            print(f'   {a:7.2f}-{b:7.2f}s max {mx:4.0f}°  auto fwd={np.round(fa_mid, 2)}  notes: {ctx}')


if __name__ == '__main__':
    main()
