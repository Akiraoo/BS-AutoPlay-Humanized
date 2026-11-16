"""Offline check of NoteSwingPlanner.PlanDotDirections (Python port) against human dot choices.
Notes are read from the AutoPlay replay's note events; human swing directions from real replays of the same map.
usage: dot_dp_check.py <autoplay.bsor> <real.bsor ...>
"""
import sys, os, math
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from compare_map import prep, swing2d

ANG = {0: 0, 1: 180, 2: 270, 3: 90, 4: 315, 5: 45, 6: 225, 7: 135}
ForceChainGap, ChainGap, ChainAlign, SliderAlign, SliderDirAlign, AlternateDot = 0.02, 0.16, 0.8, 0.65, 0.5, -0.3
ResetScale, ResetTau, ResetBase = 3.0, 0.45, 0.4
StackGap = 0.06

def cutdir(cd):
    a = math.radians(ANG[cd]); return np.array([math.sin(a), math.cos(a)])
def pos(n): return np.array([(n['li'] - 1.5) * 0.6, 0.72 + n['la'] * 0.53])
def slider_tail(prev, note, pdir, g, tdir=None):
    if g > ChainGap: return False
    d = pos(note) - pos(prev); L = np.linalg.norm(d)
    if L < 0.05 or pdir @ (d / L) < SliderAlign: return False
    if tdir is not None and pdir @ tdir < SliderDirAlign: return False
    return True
def tcost(prev, note, a, b, g):
    dt = a @ b
    FK = float(os.environ.get('FLIPK', 0.0))
    flip = FK * (1 - (-a) @ b) / 2 if note['cd'] == 8 else 0.0
    if g <= StackGap: return 0 if dt >= ChainAlign else (6 if g <= ForceChainGap else 3)
    if dt <= AlternateDot: return flip
    if g <= ChainGap and dt >= SliderDirAlign and slider_tail(prev, note, a, g, b): return 0
    r = ResetScale * math.exp(-g / ResetTau) + ResetBase
    TURN = float(os.environ.get('TURNMUL', 1.0))
    if dt < ChainAlign: r *= TURN * (0.4 + 0.6 * np.clip((dt - AlternateDot) / (ChainAlign - AlternateDot), 0, 1))
    return r + flip
def plan(notes, left):
    ut = math.radians(-10 if left else 5); dt_ = math.radians(6 if left else 2); g2 = 0.7071
    C, P = [], []
    for i, n in enumerate(notes):
        if n['cd'] < 8: C.append([cutdir(n['cd'])]); P.append([0]); continue
        c = [np.array([math.sin(dt_), -math.cos(dt_)]), np.array([math.sin(ut), math.cos(ut)]),
             np.array([-1., 0]), np.array([1., 0]), np.array([-g2, g2]), np.array([g2, g2]), np.array([-g2, -g2]), np.array([g2, -g2])]
        HP = float(os.environ.get('HPRIOR', 0.35)); DP_ = float(os.environ.get('DPRIOR', 0.25))
        C.append(c); P.append([0, 0.2 if i == 0 else 0, HP, HP, DP_, DP_, DP_, DP_])
    cost = [np.array(P[0], float)]; back = [None]
    for i in range(1, len(notes)):
        g = notes[i]['t'] - notes[i - 1]['t']
        cc = np.zeros(len(C[i])); bb = np.zeros(len(C[i]), int)
        for b, vb in enumerate(C[i]):
            vals = [cost[-1][a] + tcost(notes[i - 1], notes[i], va, vb, g) for a, va in enumerate(C[i - 1])]
            bb[b] = int(np.argmin(vals)); cc[b] = min(vals) + P[i][b]
        cost.append(cc); back.append(bb)
    cur = int(np.argmin(cost[-1])); res = [None] * len(notes)
    for i in range(len(notes) - 1, -1, -1):
        res[i] = C[i][cur]
        if i: cur = back[i][cur]
    return res
def old_rule(notes):
    res = []
    for i, n in enumerate(notes):
        if n['cd'] < 8: res.append(cutdir(n['cd'])); continue
        if i == 0: res.append(np.array([0, -1.])); continue
        g = n['t'] - notes[i - 1]['t']
        if g <= 0.02 or slider_tail(notes[i - 1], n, res[-1], g): res.append(res[-1])
        else: res.append(-res[-1])
    return res

def main():
    args=[a for a in sys.argv[1:] if a!='--v']
    auto = prep(args[0]); reals = [prep(p) for p in args[1:]]
    for left, col, hand in ((True, 0, 'L'), (False, 1, 'R')):
        ev = sorted((t, nid) for nid, t in auto[3] if (nid // 10) % 10 == col)
        notes = [dict(t=t, li=(nid // 1000) % 10, la=(nid // 100) % 10, cd=nid % 10, id=nid) for t, nid in ev]
        new, old = plan(notes, left), old_rule(notes)
        stats = {'new': [], 'old': []}
        for i, n in enumerate(notes):
            if n['cd'] != 8: continue
            hs = []
            for _, tr, Fr, evr in reals:
                m = [x for x in evr if x[0] == n['id'] and abs(x[1] - n['t']) < 0.03]
                if m: hs.append(swing2d(tr, Fr[hand], np.array(m[0][1])))
            if not hs: continue
            h = np.mean(hs, 0); h /= np.linalg.norm(h)
            stats['new'].append(new[i] @ h > 0.5); stats['old'].append(old[i] @ h > 0.5)
            if '--v' in sys.argv and new[i] @ h <= 0.5:
                ctx = ' '.join(f"{'UDLR'[x['cd']] if x['cd']<4 else ['UL','UR','DL','DR','.'][x['cd']-4]}{'*' if j==i else ''}@{x['t']-n['t']:+.2f}(L{x['li']}{x['la']})" for j, x in enumerate(notes) if abs(x['t'] - n['t']) < 1.0)
                print(f"   {hand} t={n['t']:.2f} plan={np.round(new[i],1)} human={np.round(h,1)} n={len(hs)} | {ctx}")
        print(f"{hand}: dots {len(stats['new'])} | matches human: old rule {np.mean(stats['old']):.0%}  ->  new plan {np.mean(stats['new']):.0%}")


if __name__ == '__main__':
    main()
