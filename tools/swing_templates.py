"""Cut-aligned swing templates from real replays.
For every good cut: swing frame at the cut = (f_cut, d = swing direction, n = f_cut x d).
  alpha(tau) = angle along the swing (negative = behind, where the saber came from)
  beta(tau)  = sideways angle out of the swing plane (the figure-8 / loop component)
Grouped by gap to the previous cut of the same hand and by whether that cut went the opposite way.
"""
import sys, os, glob, json
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bsor_stats import load, qrot

REPLAYS, N, OUT = sys.argv[1], int(sys.argv[2]), sys.argv[3]
TAU = np.round(np.arange(-0.70, 0.401, 0.01), 3)
GAP_BINS = [0.10, 0.18, 0.26, 0.36, 0.50, 0.75, 1.20, 3.0]

def unit(v): return v / np.linalg.norm(v, axis=-1, keepdims=True)

files = sorted(glob.glob(os.path.join(REPLAYS, '*.bsor')), key=os.path.getmtime, reverse=True)
rows = []  # (hand, gap_prev, gap_next, alternate_prev, alpha[], beta[], d_screen(2), fwd_angle_at_cut)
used = 0
for p in files:
    if used >= N: break
    try: info, fr, notes = load(p)
    except Exception: continue
    if info['leftHanded'] or info['mode'] != 'Standard' or info['difficulty'] not in ('Expert', 'ExpertPlus'): continue
    if 'SS' in info['modifiers'] or 'FS' in info['modifiers'] or 'SF' in info['modifiers']: continue
    t = fr[:, 0]
    if np.any(np.diff(t) <= 0):
        keep = np.r_[True, np.diff(t) > 0]; fr, t = fr[keep], fr[keep][:, 0]
    # room yaw normalization (same as wrist_fit)
    hf = qrot(fr[:, 4:8], np.array([0, 0, 1.0])); psi = np.arctan2(hf[:, 0].mean(), hf[:, 2].mean())
    c, s = np.cos(-psi), np.sin(-psi)
    used += 1
    for hand, off, st in (('L', 8, 0), ('R', 15, 1)):
        f = qrot(fr[:, off + 3:off + 7], np.array([0, 0, 1.0]))
        f = np.stack([c * f[:, 0] + s * f[:, 2], f[:, 1], -s * f[:, 0] + c * f[:, 2]], 1)
        cuts = np.array(sorted(n['t'] for n in notes if n['type'] == 0 and n['cut'] and n['cut']['stype'] == st))
        if len(cuts) < 20: continue
        # merge near-simultaneous cuts (stacks/sliders/chains) into one swing
        sw = [cuts[0]]
        for x in cuts[1:]:
            if x - sw[-1] > 0.06: sw.append(x)
        sw = np.array(sw)
        fi = lambda tt: unit(np.stack([np.interp(tt, t, f[:, k]) for k in range(3)], -1))
        fc = fi(sw)
        d = unit(fi(sw + 0.008) - fi(sw - 0.008))
        d = unit(d - (d * fc).sum(1, keepdims=True) * fc)
        n = np.cross(fc, d)
        for i in range(1, len(sw) - 1):
            g = sw[i] - sw[i - 1]; gn = sw[i + 1] - sw[i]
            if g > 3.0: continue
            F = fi(sw[i] + TAU)
            a = np.unwrap(np.arctan2((F * d[i]).sum(1), (F * fc[i]).sum(1)))
            i0 = int(np.argmin(np.abs(TAU)))
            a = np.degrees(a - 2 * np.pi * np.round(a[i0] / (2 * np.pi)))
            b = np.degrees(np.arcsin(np.clip((F * n[i]).sum(1), -1, 1)))
            alt = float((d[i] * d[i - 1]).sum()) < -0.3
            ang = np.degrees(np.arccos(np.clip(fc[i, 2], -1, 1)))
            rows.append((hand, g, gn, alt, a, b, d[i, :2], ang))
print('replays', used, 'swings', len(rows))


def dclass(dv):
    return 'down' if dv[1] < -0.5 else ('up' if dv[1] > 0.5 else 'side')
out = {'tau': TAU[::2].tolist(), 'gap_bins': GAP_BINS, 'groups': []}
for alt in (True, False):
    for dc in ('down', 'up', 'side', 'any'):
        for g0, g1 in zip(GAP_BINS[:-1], GAP_BINS[1:]):
            sel = [r for r in rows if r[3] == alt and g0 <= r[1] < g1 and (dc == 'any' or dclass(r[6]) == dc)]
            if len(sel) < 120: continue
            A = np.array([r[4] for r in sel]); B = np.array([r[5] for r in sel]) * np.array([(1 if r[0]=='R' else -1) for r in sel])[:, None]
            am, bm = np.median(A, 0), np.median(B, 0)
            ipre = np.argmin(np.where(TAU < 0, am, 1e9)); ipost = np.argmax(np.where(TAU > 0, am, -1e9))
            out['groups'].append(dict(alt=alt, dir=dc, gap=[g0, g1], n=len(sel), alpha=am[::2].round(1).tolist(), beta=bm[::2].round(1).tolist()))
            if dc != 'any':
                print(f"alt={alt!s:5} {dc:4} gap {g0:.2f}-{g1:.2f} n={len(sel):5d} pre {am[ipre]:6.1f}@{TAU[ipre]:+.2f} post {am[ipost]:6.1f}@{TAU[ipost]:+.2f} | beta at pre {bm[ipre]:+5.1f} mid-pre {bm[ipre//2+np.argmin(np.abs(TAU))//2]:+5.1f} post {bm[ipost]:+5.1f}")
json.dump(out, open(OUT, 'w'))
print('groups', len(out['groups']))
