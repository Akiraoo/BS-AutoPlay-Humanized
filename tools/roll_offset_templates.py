"""Backhand / wrist-turn templates: roll offset (human roll - pointing-only roll model) around each cut,
per hand x cut direction (8 arrows + dot). Output: circular-mean offset curve over tau and its consistency R.
usage: roll_offset_templates.py <Replays folder> <N> out.json
"""
import sys, os, glob, json
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bsor_stats import load, qrot
from wrist_fit_lib import roll_of, feats

M = json.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'model.json')))
DIRN = ['U', 'D', 'L', 'R', 'UL', 'UR', 'DL', 'DR', 'dot']
TAU = np.round(np.arange(-0.20, 0.201, 0.02), 3)

def qmul(a, b):
    ax, ay, az, aw = a; bx, by, bz, bw = b[:, 0], b[:, 1], b[:, 2], b[:, 3]
    return np.stack([aw*bx + ax*bw + ay*bz - az*by, aw*by - ax*bz + ay*bw + az*bx,
                     aw*bz + ax*by - ay*bx + az*bw, aw*bw - ax*bx - ay*by - az*bz], 1)

folder, N, out = sys.argv[1], int(sys.argv[2]), sys.argv[3]
files = sorted(glob.glob(os.path.join(folder, '*.bsor')), key=os.path.getmtime, reverse=True)
acc = {}
used = 0
for p in files:
    if used >= N: break
    try: info, fr, notes = load(p)
    except Exception: continue
    if info['leftHanded'] or info['mode'] != 'Standard' or info['difficulty'] not in ('Hard', 'Expert', 'ExpertPlus'): continue
    t = fr[:, 0]; keep = np.r_[True, np.diff(t) > 0]; fr = fr[keep]; t = fr[:, 0]
    hf = qrot(fr[:, 4:8], np.array([0, 0, 1.0])); psi = np.arctan2(hf[:, 0].mean(), hf[:, 2].mean())
    yawq = np.array([0, np.sin(-psi / 2), 0, np.cos(-psi / 2)])
    used += 1
    for hand, off, col in (('L', 8, 0), ('R', 15, 1)):
        Q = qmul(yawq, fr[:, off + 3:off + 7]); Q /= np.linalg.norm(Q, axis=1, keepdims=True)
        f = qrot(Q, np.array([0, 0, 1.0]))
        C = np.array(M[hand]['rollC']).T
        P = feats(f) @ C
        dr = np.angle(np.exp(1j * (roll_of(Q, f) - np.arctan2(P[:, 1], P[:, 0]))))
        z = np.exp(1j * dr)
        for n in notes:
            if n['type'] != 0 or (n['id'] // 10) % 10 != col: continue
            tt = n['t'] + TAU
            if tt[0] < t[0] or tt[-1] > t[-1]: continue
            zr = np.interp(tt, t, z.real); zi = np.interp(tt, t, z.imag)
            acc.setdefault((hand, n['id'] % 10), []).append(zr + 1j * zi)
print('replays', used)
res = {'tau': TAU.tolist(), 'groups': []}
for (hand, cd), v in sorted(acc.items()):
    v = np.array(v); v = v / np.maximum(np.abs(v), 1e-9)
    m = v.mean(0)
    ang = np.degrees(np.angle(m)); R = np.abs(m)
    res['groups'].append(dict(hand=hand, dir=cd, n=len(v), offset=ang.round(1).tolist(), R=R.round(3).tolist()))
    i0 = len(TAU) // 2
    print(f'{hand} {DIRN[cd]:3s} n={len(v):6d} | offset @-0.2 {ang[0]:+6.1f}° @-0.1 {ang[i0-5]:+6.1f}° @0 {ang[i0]:+6.1f}° @+0.1 {ang[i0+5]:+6.1f}° @+0.2 {ang[-1]:+6.1f}° | R@0 {R[i0]:.2f}')
json.dump(res, open(out, 'w'))
