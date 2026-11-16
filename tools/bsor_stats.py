"""Minimal .bsor reader + motion statistics, for comparing AutoPlay replays with real ones."""
import struct, sys, math, glob, os
import numpy as np

class R:
    def __init__(s, b): s.b, s.o = b, 0
    def i(s): v = struct.unpack_from('<i', s.b, s.o)[0]; s.o += 4; return v
    def f(s): v = struct.unpack_from('<f', s.b, s.o)[0]; s.o += 4; return v
    def by(s): v = s.b[s.o]; s.o += 1; return v
    def bo(s): return s.by() != 0
    def s(s):
        n = s.i(); v = s.b[s.o:s.o + n].decode('utf8', 'replace'); s.o += n; return v
    def fl(s, n): v = struct.unpack_from('<%df' % n, s.b, s.o); s.o += 4 * n; return v

def load(path):
    r = R(open(path, 'rb').read())
    assert r.i() == 0x442d3d69
    r.by(); assert r.by() == 0
    info = {}
    for k in ['version', 'gameVersion', 'timestamp', 'playerID', 'playerName', 'platform', 'tracking', 'hmd',
              'controller', 'hash', 'songName', 'mapper', 'difficulty']:
        info[k] = r.s()
    info['score'] = r.i()
    for k in ['mode', 'environment', 'modifiers']: info[k] = r.s()
    info['jd'] = r.f(); info['leftHanded'] = r.bo(); info['height'] = r.f()
    info['start'] = r.f(); info['fail'] = r.f(); info['speed'] = r.f()
    assert r.by() == 1
    n = r.i()
    rows = []
    for _ in range(n):
        tt = r.f(); r.i()  # time, fps
        rows.append((tt,) + r.fl(21))
    fr = np.array(rows, dtype=np.float64)
    assert r.by() == 2
    notes = []
    for _ in range(r.i()):
        nid = r.i(); et = r.f(); st = r.f(); typ = r.i()
        cut = None
        if typ in (0, 1):
            so, do, to, early = r.bo(), r.bo(), r.bo(), r.bo()
            speed = r.f(); sdir = r.fl(3); stype = r.i(); tdev = r.f(); ddev = r.f()
            cp = r.fl(3); cn = r.fl(3); cdist = r.f(); cang = r.f(); bcr = r.f(); acr = r.f()
            cut = dict(speed=speed, before=bcr, after=acr, dist=cdist, stype=stype, cp=cp)
        notes.append(dict(id=nid, t=et, type=typ, cut=cut))
    return info, fr, notes

def qrot(q, v):
    x, y, z, w = q[..., 0], q[..., 1], q[..., 2], q[..., 3]
    u = np.stack([x, y, z], -1)
    uv = np.cross(u, v); uuv = np.cross(u, uv)
    return v + 2 * (w[..., None] * uv + uuv)

def stats(path):
    info, fr, notes = load(path)
    t = fr[:, 0]
    # trim to the played part: first..last note
    good = [n['t'] for n in notes if n['type'] == 0]
    if good:
        m = (t >= min(good) - 0.5) & (t <= max(good) + 0.5)
        fr, t = fr[m], t[m]
    head_p, head_q = fr[:, 1:4], fr[:, 4:8]
    out = {'file': os.path.basename(path)[:60], 'n_frames': len(t), 'fps': len(t) / max(t[-1] - t[0], 1e-6)}
    out['head_pos_std_cm'] = (head_p.std(0) * 100).round(1).tolist()
    hf = qrot(head_q, np.array([0, 0, 1.0]))
    out['head_fwd_std_deg'] = float(np.degrees(np.arccos(np.clip((hf * hf.mean(0) / np.linalg.norm(hf.mean(0))).sum(1), -1, 1))).mean().round(1))
    for name, off in (('L', 8), ('R', 15)):
        p, q = fr[:, off:off + 3], fr[:, off + 3:off + 7]
        dt = np.diff(t); dt[dt <= 0] = 1e-3
        f = qrot(q, np.array([0, 0, 1.0]))
        ang = np.degrees(np.arccos(np.clip((f[1:] * f[:-1]).sum(1), -1, 1))) / dt
        pv = np.linalg.norm(np.diff(p, axis=0), axis=1) / dt
        rel = p - head_p  # hand relative to head
        out[name + '_pos_std_cm'] = (p.std(0) * 100).round(1).tolist()
        out[name + '_rel_head_mean'] = rel.mean(0).round(2).tolist()
        out[name + '_angvel_p50/p90/p99'] = np.percentile(ang, [50, 90, 99]).round(0).tolist()
        out[name + '_linvel_p50/p90/p99'] = np.percentile(pv, [50, 90, 99]).round(2).tolist()
        out[name + '_still_frac(<20deg/s)'] = float((ang < 20).mean().round(3))
        # jerk-ish: angular acceleration p99
        acc = np.abs(np.diff(ang)) / dt[1:]
        out[name + '_angacc_p99'] = float(np.percentile(acc, 99).round(0))
    cuts = [n['cut'] for n in notes if n['type'] == 0 and n['cut']]
    if cuts:
        out['saberSpeed_p50'] = float(np.median([c['speed'] for c in cuts]).round(1))
    out['info'] = {k: info[k] for k in ('playerName', 'songName', 'difficulty', 'modifiers', 'height', 'hmd')}
    return out

if __name__ == '__main__':
    for p in sys.argv[1:]:
        for k, v in stats(p).items(): print(f'{k:28s} {v}')
        print()
