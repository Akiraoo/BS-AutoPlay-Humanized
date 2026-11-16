"""Learn hand kinematics from real replays:
 - position model:  hand_pos - head_pos ≈ b + A·f   (f = saber forward)
 - roll model:      controller twist around f as a smooth function of f
 - direction coverage: which pointing directions humans actually use (for wrist limits)
"""
import sys, os, glob, json
import numpy as np
sys.path.insert(0, os.path.dirname(__file__))
from bsor_stats import load, qrot

REPLAYS = sys.argv[1]
N = int(sys.argv[2]) if len(sys.argv) > 2 else 60
OUT = sys.argv[3] if len(sys.argv) > 3 else None

def ref_up(f):
    # parallel transport of +y from +z to f (singular only at f = -z, i.e. pointing straight back)
    z = np.array([0, 0, 1.0])
    ax = np.cross(z, f); s = np.linalg.norm(ax, axis=1, keepdims=True); c = f[:, 2:3]
    ax = ax / np.maximum(s, 1e-9)
    y = np.array([0, 1.0, 0])
    # Rodrigues rotation of y about ax by angle atan2(s,c)
    return y * c + np.cross(ax, y) * s + ax * (ax @ y)[:, None] * (1 - c)

def roll_of(q, f):
    u = qrot(q, np.array([0, 1.0, 0]))
    r = ref_up(f)
    return np.arctan2((np.cross(r, u) * f).sum(1), (r * u).sum(1))

def feats(f):
    x, y, z = f[:, 0], f[:, 1], f[:, 2]
    return np.stack([np.ones_like(x), x, y, z, x * x, y * y, z * z, x * y, y * z, x * z], 1)

files = sorted(glob.glob(os.path.join(REPLAYS, '*.bsor')), key=os.path.getmtime, reverse=True)
data = {'L': [], 'R': []}
used = 0
for p in files:
    if used >= N: break
    try:
        info, fr, notes = load(p)
    except Exception:
        continue
    if info['leftHanded'] or info['mode'] != 'Standard' or info['difficulty'] not in ('Expert', 'ExpertPlus'):
        continue
    good = [n['t'] for n in notes if n['type'] == 0]
    if len(good) < 200: continue
    t = fr[:, 0]
    m = (t >= min(good)) & (t <= max(good))
    fr = fr[m][::2]
    # normalize room yaw: rotate everything so the average head direction is +z
    hf = qrot(fr[:, 4:8], np.array([0, 0, 1.0]))
    psi = np.arctan2(hf[:, 0].mean(), hf[:, 2].mean())
    c, s_ = np.cos(-psi), np.sin(-psi)
    def ry(v):
        return np.stack([c * v[:, 0] + s_ * v[:, 2], v[:, 1], -s_ * v[:, 0] + c * v[:, 2]], 1)
    yawq = np.array([0, np.sin(-psi / 2), 0, np.cos(-psi / 2)])
    def qmul(a, b):
        ax, ay, az, aw = a; bx, by, bz, bw = b[:, 0], b[:, 1], b[:, 2], b[:, 3]
        return np.stack([aw*bx + ax*bw + ay*bz - az*by, aw*by - ax*bz + ay*bw + az*bx,
                         aw*bz + ax*by - ay*bx + az*bw, aw*bw - ax*bx - ay*by - az*bz], 1)
    for off in (1, 8, 15):
        fr[:, off:off + 3] = ry(fr[:, off:off + 3])
        fr[:, off + 3:off + 7] = qmul(yawq, fr[:, off + 3:off + 7])
    head = fr[:, 1:4]
    for name, off in (('L', 8), ('R', 15)):
        P, Q = fr[:, off:off + 3], fr[:, off + 3:off + 7]
        Q = Q / np.linalg.norm(Q, axis=1, keepdims=True)
        f = qrot(Q, np.array([0, 0, 1.0]))
        rel = P - head
        rel[:, 1] = P[:, 1] - head[:, 1]
        data[name].append(np.concatenate([f, rel, roll_of(Q, f)[:, None]], 1))
    used += 1
print('replays used:', used)

model = {}
for name in ('L', 'R'):
    D = np.concatenate(data[name]); f, rel, roll = D[:, :3], D[:, 3:6], D[:, 6]
    print(f'\n=== {name} hand, {len(D)} samples')
    # position: rel = b + A f
    X = np.concatenate([np.ones((len(f), 1)), f], 1)
    W, *_ = np.linalg.lstsq(X, rel, rcond=None)
    pred = X @ W
    r2 = 1 - ((rel - pred) ** 2).sum(0) / ((rel - rel.mean(0)) ** 2).sum(0)
    print('pos intercept b =', W[0].round(3), ' A rows (x,y,z) =\n', W[1:].T.round(3), '\n R2 per axis =', r2.round(2),
          ' resid std cm =', ((rel - pred).std(0) * 100).round(1))
    # roll: [cos, sin] = feats(f) · C
    F = feats(f)
    C, *_ = np.linalg.lstsq(F, np.stack([np.cos(roll), np.sin(roll)], 1), rcond=None)
    pr = np.arctan2(*(F @ C).T[::-1])
    err = np.degrees(np.angle(np.exp(1j * (roll - pr))))
    print('roll resid |err| p50/p90 deg =', np.percentile(np.abs(err), [50, 90]).round(1),
          ' raw roll spread p50/p90 =', np.percentile(np.abs(np.degrees(np.angle(np.exp(1j * (roll - np.angle(np.exp(1j * roll).mean())))))), [50, 90]).round(1))
    # direction coverage
    yaw = np.degrees(np.arctan2(f[:, 0], f[:, 2])); pitch = np.degrees(np.arcsin(np.clip(f[:, 1], -1, 1)))
    back = np.degrees(np.arccos(np.clip(f[:, 2], -1, 1)))  # angle from straight ahead
    print('pitch p1/p5/p50/p95/p99 =', np.percentile(pitch, [1, 5, 50, 95, 99]).round(0))
    print('yaw   p1/p5/p50/p95/p99 =', np.percentile(yaw, [1, 5, 50, 95, 99]).round(0))
    print('angle from forward p50/p90/p99/p99.9 =', np.percentile(back, [50, 90, 99, 99.9]).round(0))
    # per-direction max angle from forward, binned by the direction's azimuth around the z axis
    az = np.degrees(np.arctan2(f[:, 1], f[:, 0]))  # 0=right, 90=up, 180=left, -90=down
    bins = np.arange(-180, 181, 30)
    lim = []
    for a0, a1 in zip(bins[:-1], bins[1:]):
        s = back[(az >= a0) & (az < a1)]
        lim.append(float(np.percentile(s, 99)) if len(s) > 100 else None)
    print('p99 angle-from-forward by azimuth bins', list(zip(((bins[:-1] + bins[1:]) / 2).tolist(), [None if v is None else round(v) for v in lim])))
    model[name] = {'b': W[0].tolist(), 'A': W[1:].T.tolist(), 'rollC': C.T.tolist(),
                   'limit_az_centers': ((bins[:-1] + bins[1:]) / 2).tolist(), 'limit_deg': lim}
if OUT:
    json.dump(model, open(OUT, 'w'), indent=1)
