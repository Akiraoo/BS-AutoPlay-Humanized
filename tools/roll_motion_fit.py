"""Does the saber twist (roll) depend on the swing direction (forehand vs backhand)?
Fits two roll models on moving frames:
  A) roll = g(f)            (pointing direction only, current HumanModel)
  B) roll = g(f, m)         (pointing direction + motion direction m = df/dt normalised)
usage: roll_motion_fit.py <Replays folder> <N> [out.json] [--only SUBSTRING]
"""
import sys, os, glob, json
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bsor_stats import load, qrot
from wrist_fit_lib import roll_of, feats

folder, N = sys.argv[1], int(sys.argv[2])
out = sys.argv[3] if len(sys.argv) > 3 and not sys.argv[3].startswith('--') else None
only = sys.argv[sys.argv.index('--only') + 1] if '--only' in sys.argv else None

def featsM(f, m):
    F = feats(f)
    return np.concatenate([F, F * m[:, 0:1], F * m[:, 1:2], F * m[:, 2:3]], 1)

files = sorted(glob.glob(os.path.join(folder, '*.bsor')), key=os.path.getmtime, reverse=True)
data = {'L': [], 'R': []}
used = 0
for p in files:
    if used >= N: break
    if only and only not in p: continue
    try: info, fr, notes = load(p)
    except Exception: continue
    if info['leftHanded'] or info['mode'] != 'Standard': continue
    t = fr[:, 0]; keep = np.r_[True, np.diff(t) > 0]; fr = fr[keep]; t = fr[:, 0]
    hf = qrot(fr[:, 4:8], np.array([0, 0, 1.0])); psi = np.arctan2(hf[:, 0].mean(), hf[:, 2].mean())
    yawq = np.array([0, np.sin(-psi / 2), 0, np.cos(-psi / 2)])
    def qmul(a, b):
        ax, ay, az, aw = a; bx, by, bz, bw = b[:, 0], b[:, 1], b[:, 2], b[:, 3]
        return np.stack([aw*bx + ax*bw + ay*bz - az*by, aw*by - ax*bz + ay*bw + az*bx,
                         aw*bz + ax*by - ay*bx + az*bw, aw*bw - ax*bx - ay*by - az*bz], 1)
    used += 1
    for hand, off in (('L', 8), ('R', 15)):
        Q = qmul(yawq, fr[:, off + 3:off + 7]); Q /= np.linalg.norm(Q, axis=1, keepdims=True)
        f = qrot(Q, np.array([0, 0, 1.0]))
        df = np.gradient(f, t, axis=0)
        sp = np.degrees(np.linalg.norm(df, axis=1))
        m = df / np.maximum(np.linalg.norm(df, axis=1, keepdims=True), 1e-9)
        sel = sp > 300
        data[hand].append(np.concatenate([f[sel], m[sel], roll_of(Q[sel], f[sel])[:, None]], 1))
print('replays', used)
res = {}
for hand in ('L', 'R'):
    D = np.concatenate(data[hand]); f, m, roll = D[:, :3], D[:, 3:6], D[:, 6]
    Y = np.stack([np.cos(roll), np.sin(roll)], 1)
    n = len(D); idx = np.random.default_rng(0).permutation(n); tr, te = idx[: n * 4 // 5], idx[n * 4 // 5:]
    for name, X in (('A f-only', feats(f)), ('B f+motion', featsM(f, m))):
        C, *_ = np.linalg.lstsq(X[tr], Y[tr], rcond=None)
        P = X[te] @ C
        err = np.degrees(np.abs(np.angle(np.exp(1j * (roll[te] - np.arctan2(P[:, 1], P[:, 0]))))))
        print(f'{hand} {name:11s} moving frames n={n}: roll error p50 {np.percentile(err, 50):5.1f}°  p75 {np.percentile(err, 75):5.1f}°  p90 {np.percentile(err, 90):5.1f}°')
        if name.startswith('B'):
            Cfull, *_ = np.linalg.lstsq(X, Y, rcond=None)
            res[hand] = Cfull.T.tolist()
    # horizontal swings: roll at inward vs outward motion, same pointing region (roughly forward)
    fw = (f[:, 2] > 0.7)
    inward = fw & ((m[:, 0] < -0.8) if hand == 'R' else (m[:, 0] > 0.8))
    outward = fw & ((m[:, 0] > 0.8) if hand == 'R' else (m[:, 0] < -0.8))
    cm = lambda r: np.degrees(np.angle(np.mean(np.exp(1j * r))))
    print(f'   {hand} pointing forward, horizontal swing: inward roll {cm(roll[inward]):6.1f}° (n={inward.sum()})  outward roll {cm(roll[outward]):6.1f}° (n={outward.sum()})')
    up = fw & (m[:, 1] > 0.8); dn = fw & (m[:, 1] < -0.8)
    print(f'   {hand} pointing forward, vertical swing:   up roll {cm(roll[up]):6.1f}° (n={up.sum()})  down roll {cm(roll[dn]):6.1f}° (n={dn.sum()})')
if out:
    json.dump(res, open(out, 'w'))
