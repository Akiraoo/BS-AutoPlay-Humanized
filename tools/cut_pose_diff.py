"""At each cut: how does the human's saber/hand differ from BotPlay's "aim from the arm-model wrist at the cube" pose?
Grouped by hand x cut direction. Large systematic differences = a technique BotPlay doesn't model.
usage: cut_pose_diff.py <real.bsor ...>
"""
import sys, os, json
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bsor_stats import load, qrot

DIRN = ['U', 'D', 'L', 'R', 'UL', 'UR', 'DL', 'DR', 'dot']
M = json.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'model.json')))

def note_pos(nid):
    li = (nid // 1000) % 10; la = (nid // 100) % 10
    return np.array([(li - 1.5) * 0.6, 0.72 + la * 0.53, 1.5])

def wrist_for(hand, f, head):
    m = M[hand]
    return head + np.array(m['b']) + np.array(m['A']) @ f - 0.2 * f

def aim(hand, target, head):
    f = target - (head + np.array(M[hand]['b'])); f /= np.linalg.norm(f)
    for _ in range(8):
        f = target - wrist_for(hand, f, head); f /= np.linalg.norm(f)
    return f

acc = {}
for p in sys.argv[1:]:
    info, fr, notes = load(p)
    t = fr[:, 0]; keep = np.r_[True, np.diff(t) > 0]; fr = fr[keep]; t = fr[:, 0]
    hf = qrot(fr[:, 4:8], np.array([0, 0, 1.0])); psi = np.arctan2(hf[:, 0].mean(), hf[:, 2].mean())
    c, s = np.cos(-psi), np.sin(-psi)
    ry = lambda v: np.stack([c * v[..., 0] + s * v[..., 2], v[..., 1], -s * v[..., 0] + c * v[..., 2]], -1)
    head = ry(fr[:, 1:4])
    for n in notes:
        if n['type'] != 0: continue
        nid = n['id']; col = (nid // 10) % 10; cd = nid % 10
        hand, off = ('L', 8) if col == 0 else ('R', 15)
        i = np.searchsorted(t, n['t']); i = min(max(i, 1), len(t) - 1)
        f = ry(qrot(fr[i, off + 3:off + 7], np.array([0, 0, 1.0])))
        hp = ry(fr[i, off:off + 3])
        h0 = head[i] * np.array([1, 0, 1]) + np.array([0, 1.5, 0])   # put the human's head on BotPlay's head height
        tgt = note_pos(nid)
        fa = aim(hand, tgt, h0)
        ang = np.degrees(np.arccos(np.clip(f @ fa, -1, 1)))
        dpos = (hp - head[i]) - (wrist_for(hand, fa, h0) + 0.2 * fa - h0)
        acc.setdefault((hand, DIRN[cd]), []).append((ang, *dpos, (nid // 1000) % 10))
print('hand dir  n   | saber dir vs BotPlay aim: p50  p90 | hand pos diff (human - BotPlay) x  y  z [cm] | lanes')
for k in sorted(acc):
    a = np.array(acc[k])
    if len(a) < 15: continue
    lanes = np.bincount(a[:, 4].astype(int), minlength=4)
    print(f'{k[0]} {k[1]:3s} {len(a):4d} | {np.percentile(a[:,0],50):5.0f}° {np.percentile(a[:,0],90):5.0f}° | {np.median(a[:,1])*100:+5.0f} {np.median(a[:,2])*100:+5.0f} {np.median(a[:,3])*100:+5.0f} | {lanes.tolist()}')
