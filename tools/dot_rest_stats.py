"""How humans pick the swing direction for dot (no-arrow) notes, and where the saber rests during long gaps.
usage: dot_rest_stats.py <Replays folder> <N>
"""
import sys, os, glob
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from compare_map import prep, swing2d, fat

files = sorted(glob.glob(os.path.join(sys.argv[1], '*.bsor')), key=os.path.getmtime, reverse=True)
N = int(sys.argv[2])
dots = []   # (hand, gap_prev, prev2d, dot2d, next_gap, next_cd, layer)
rest = {'L': [], 'R': []}
used = 0
for p in files:
    if used >= N: break
    if '-exit-' in p: continue
    try: info, t, F, ev = prep(p)
    except Exception: continue
    if info['leftHanded'] or info['mode'] != 'Standard' or info['difficulty'] not in ('Expert', 'ExpertPlus', 'Hard'): continue
    used += 1
    for hand, col in (('L', 0), ('R', 1)):
        e = sorted([(tt, nid) for nid, tt in ev if (nid // 10) % 10 == col])
        if len(e) < 10: continue
        times = np.array([x[0] for x in e]); ids = [x[1] for x in e]
        sw = swing2d(t, F[hand], times)
        for i in range(1, len(e) - 1):
            if ids[i] % 10 == 8 and times[i] - times[i - 1] > 0.06:
                dots.append((hand, times[i] - times[i - 1], sw[i - 1], sw[i], times[i + 1] - times[i], ids[i + 1] % 10, (ids[i] // 100) % 10))
        # rest: middle of gaps longer than 1.5 s
        for i in range(len(e) - 1):
            g = times[i + 1] - times[i]
            if g > 1.5:
                mid = np.arange(times[i] + 0.6, times[i + 1] - 0.6, 0.05)
                if len(mid): rest[hand].append(fat(t, F[hand], mid).mean(0))
print('replays', used, 'dots', len(dots))
bins = [0.06, 0.2, 0.3, 0.45, 0.6, 0.8, 1.0, 1.5, 3, 99]
print('gap_prev bin | n | opposite-to-prev | swing DOWN | swing UP | (when not opposite: down share)')
for a, b in zip(bins[:-1], bins[1:]):
    sel = [d for d in dots if a <= d[1] < b]
    if len(sel) < 30: continue
    opp = np.mean([np.dot(d[2], d[3]) < -0.3 for d in sel])
    down = np.mean([d[3][1] < -0.5 for d in sel]); up = np.mean([d[3][1] > 0.5 for d in sel])
    no = [d for d in sel if np.dot(d[2], d[3]) >= -0.3]
    dn = np.mean([d[3][1] < -0.5 for d in no]) if no else float('nan')
    print(f'{a:4.2f}-{b:4.2f} | {len(sel):5d} | {opp:5.0%} | {down:5.0%} | {up:5.0%} | {dn:5.0%}')
for hand in ('L', 'R'):
    for nm, cond in (('up', lambda v: v[1] > 0.5), ('down', lambda v: v[1] < -0.5)):
        v = np.array([d[3] for d in dots if d[0] == hand and cond(d[3])])
        if len(v): print(f'{hand} {nm:4} dot swings: median tilt from vertical {np.degrees(np.median(np.arctan2(v[:,0], np.abs(v[:,1])))):+.0f}° (+ = toward right)')
for hand in ('L', 'R'):
    if rest[hand]:
        r = np.array(rest[hand]); m = r.mean(0); m /= np.linalg.norm(m)
        print(f'{hand} rest direction during long gaps (n={len(r)}): {m.round(3)}  pitch {np.degrees(np.arcsin(m[1])):.0f}°  yaw {np.degrees(np.arctan2(m[0], m[2])):.0f}°')
