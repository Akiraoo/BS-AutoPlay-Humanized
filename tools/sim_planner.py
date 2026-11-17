"""Offline simulation of NoteSwingPlanner's saber pointing trajectory (Python port, no bombs/smoothing/roll),
compared against real replays of the same map. The map's notes are taken from the real replay's note events,
so any real replay can be used as a test map.

usage: sim_planner.py <templates.json> <mode> <real.bsor ...>
  mode: all_slerp = templates keyed by turn angle + direction slerp for every transition (current C#; use with rel_templates.json)
        rel_lerp  = turn-angle templates + angle lerp in the next stroke's frame
        new       = turn-angle templates + route-via-front for turns (rejected: worse)
        old       = alternate yes/no templates + angle lerp (original; use with old_templates.json)
  env: RESTGAP (default 0.9; C# uses 1.2), SIMFLAGS=--diag/--prof for breakdowns
"""
import sys, os, json, math
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from compare_map import prep, fat
from dot_dp_check import plan as plan_dots, slider_tail, cutdir

HERE = os.path.dirname(os.path.abspath(__file__))
M = json.load(open(os.path.join(HERE, 'model.json')))
ForceChainGap, ChainGap, ChainAlign = 0.02, 0.16, 0.8
RestGap, RestFade, IdleGap = float(os.environ.get('RESTGAP', 0.9)), 0.35, 2.0
_unused1, _unused2, _unused3, MinPreLead, Perception = 0.9, 0.35, 2.0, 0.10, 0.20
KNEE, SLOPE = float(os.environ.get('KNEE', 110.0)), float(os.environ.get('SLOPE', 0.8))

def unit(v): n = np.linalg.norm(v); return v / n if n > 1e-9 else v
def slerp(a, b, w):
    d = np.clip(a @ b, -1, 1); th = math.acos(d)
    if th < 1e-4: return unit(a + (b - a) * w)
    return unit((math.sin((1 - w) * th) * a + math.sin(w * th) * b) / math.sin(th))
def smooth(u): u = min(max(u, 0.0), 1.0); return u * u * (3 - 2 * u)

class Templates:
    def __init__(self, path, keyed_rel):
        T = json.load(open(path)); self.tau = np.array(T['tau']); self.rel = keyed_rel
        self.by = {}
        for g in T['groups']:
            r = int(g['alt']) if keyed_rel else (4 if g['alt'] else 0)
            d = {'down': 0, 'up': 1, 'side': 2, 'any': 3}[g['dir']]
            self.by.setdefault((r, d), []).append(g)
        for v in self.by.values(): v.sort(key=lambda g: g['gap'][0] + g['gap'][1])
    def cands(self, rel, dir_):
        for d in range(5):
            for r in ([rel] if d == 0 else [rel + d, rel - d]):
                if 0 <= r <= 4:
                    for dd in (dir_, 3):
                        if (r, dd) in self.by: return self.by[(r, dd)]
    def eval(self, rel, dir_, gap, tau):
        L = self.cands(rel, dir_)
        cs = [0.5 * (g['gap'][0] + g['gap'][1]) for g in L]
        if gap >= cs[-1]: a = b = L[-1]; w = 0
        elif gap <= cs[0]: a = b = L[0]; w = 0
        else:
            i = max(j for j in range(len(cs)) if cs[j] <= gap); a, b = L[i], L[i + 1]; w = (gap - cs[i]) / (cs[i + 1] - cs[i])
        al = (1 - w) * np.interp(tau, self.tau, a['alpha']) + w * np.interp(tau, self.tau, b['alpha'])
        be = (1 - w) * np.interp(tau, self.tau, a['beta']) + w * np.interp(tau, self.tau, b['beta'])
        m = abs(al)
        if m > KNEE: al = math.copysign(KNEE + (m - KNEE) * SLOPE, al)
        return al, be
    def pre_lead(self, rel, dir_, gap):
        best, bt = 0, 0.1
        for tau in self.tau[self.tau < 0]:
            a, _ = self.eval(rel, dir_, gap, tau)
            if a < best: best, bt = a, -tau
        return bt

def rel_bin(a, b):
    ang = math.degrees(math.acos(np.clip(a @ b, -1, 1)))
    return 0 if ang < 30 else 1 if ang < 67.5 else 2 if ang < 112.5 else 3 if ang < 157.5 else 4
def dir_class(d): return 0 if d[1] < -0.5 else (1 if d[1] > 0.5 else 2)

def wrist_for(hand, f):
    m = M[hand]; return np.array([0, 1.5, 0]) + np.array(m['b']) + np.array(m['A']) @ f - 0.2 * f
def aim(hand, tgt):
    f = unit(tgt - (np.array([0, 1.5, 0]) + np.array(M[hand]['b'])))
    for _ in range(8): f = unit(tgt - wrist_for(hand, f))
    return f
def note_pos(n): return np.array([(n['li'] - 1.5) * 0.6, 0.72 + n['la'] * 0.53, 1.5])

def via_front(a, b, w, left):
    if w <= 0: return a
    if w >= 1: return b
    mid = a + b
    behind = np.linalg.norm(mid) < 1e-2 or unit(mid)[2] < 0.25
    if math.degrees(math.acos(np.clip(a @ b, -1, 1))) < 100 and not behind: return slerp(a, b, w)
    via = unit(np.array([-0.55 if left else 0.55, 0.15, 0.82]))
    c = 2 * via - 0.5 * (a + b); p = (1 - w) ** 2 * a + 2 * w * (1 - w) * c + w * w * b
    return unit(p)

def simulate(notes, left, T, mode, rt=0.6):
    hand = 'L' if left else 'R'
    n = len(notes)
    dd = plan_dots(notes, left)
    dirs3 = [np.array([d[0], d[1], 0.0]) for d in dd]
    chained = [False] * n
    for i in range(1, n):
        g = notes[i]['t'] - notes[i - 1]['t']
        p2, d2 = np.array(dd[i - 1]), np.array(dd[i])
        st = notes[i]['cd'] == 8 and p2 @ d2 >= ChainAlign and slider_tail(notes[i - 1], notes[i], p2, g)
        chained[i] = g <= ForceChainGap or st or (g <= ChainGap and (p2 @ d2 >= ChainAlign or slider_tail(notes[i - 1], notes[i], p2, g, d2 if notes[i]['cd'] < 8 else None)))
    v = [aim(hand, note_pos(x)) for x in notes]
    strokes = []; i = 0
    while i < n:
        e = i
        while e + 1 < n and chained[e + 1]: e += 1
        strokes.append((i, e)); i = e + 1
    m = len(strokes)
    sS = [notes[s]['t'] for s, _ in strokes]; sE = [notes[e]['t'] for _, e in strokes]
    gP = [IdleGap] + [sS[k] - sS[k - 1] for k in range(1, m)]
    gN = [sS[k + 1] - sS[k] for k in range(m - 1)] + [IdleGap]
    def rel(a, b):
        if mode == 'old': return 4 if dirs3[a] @ dirs3[b] <= -0.3 else 0
        return rel_bin(dirs3[a], dirs3[b])
    rP = [4] + [rel(strokes[k - 1][1], strokes[k][0]) for k in range(1, m)]
    rN = [rel(strokes[k][1], strokes[k + 1][0]) for k in range(m - 1)] + [4]
    scale = []
    for k in range(m):
        lead = T.pre_lead(rP[k], dir_class(dirs3[strokes[k][0]]), gP[k])
        mx = max(MinPreLead, rt - Perception); scale.append(lead / mx if lead > mx else 1.0)
    side = -1 if left else 1
    def frame(i):
        fc = v[i]; d3 = dirs3[i] - (dirs3[i] @ fc) * fc; d3 = unit(d3); nn = np.cross(fc, d3); return fc, d3, nn
    def fdir(i, a, b):
        fc, d3, nn = frame(i); a, b = math.radians(a), math.radians(b * side)
        return unit(math.cos(b) * (math.cos(a) * fc + math.sin(a) * d3) + math.sin(b) * nn)
    def tofr(i, f):
        fc, d3, nn = frame(i)
        return math.degrees(math.atan2(f @ d3, f @ fc)), math.degrees(math.asin(np.clip(f @ nn, -1, 1))) * side
    def pre(k, t):
        i = strokes[k][0]; a, b = T.eval(rP[k], dir_class(dirs3[i]), gP[k], (t - sS[k]) * scale[k]); return fdir(i, a, b)
    def post(k, t):
        i = strokes[k][1]; a, b = T.eval(rN[k], dir_class(dirs3[i]), gN[k], t - sE[k]); return fdir(i, a, b)
    def prewin(k): return min(0.75, T.pre_lead(rP[k], dir_class(dirs3[strokes[k][0]]), gP[k]) / scale[k] + 0.15)
    def via_rest(kp, kq, t):
        f = post(kp, t) if kp >= 0 else pre(kq, t)
        if kq >= 0:
            w = smooth((t - (sS[kq] - prewin(kq) - RestFade)) / RestFade)
            if w > 0: f = via_front(f, pre(kq, t), w, left) if mode == 'new' else slerp(f, pre(kq, t), w)
        return f
    def dir_at(t):
        if t <= sS[0]: return via_rest(-1, 0, t)
        if t >= sE[-1]: return via_rest(m - 1, -1, t)
        k = int(np.searchsorted(sE, t, side='right') - 1)
        if t < sS[k] or (k + 1 < m and False): pass
        s, e = strokes[k]
        if sS[k] <= t <= sE[k] and e > s:    # inside a multi-note stroke
            j = s + int(np.searchsorted([notes[x]['t'] for x in range(s, e + 1)], t) - 1)
            j = min(max(j, s), e - 1); u = (t - notes[j]['t']) / max(1e-4, notes[j + 1]['t'] - notes[j]['t'])
            return slerp(v[j], v[j + 1], min(max(u, 0), 1))
        if k + 1 >= m: return via_rest(k, -1, t)
        if sS[k + 1] - sE[k] > RestGap: return via_rest(k, k + 1, t)
        fp = post(k, t)
        nx = strokes[k + 1][0]
        qa, qb = T.eval(rP[k + 1], dir_class(dirs3[nx]), gP[k + 1], (t - sS[k + 1]) * scale[k + 1])
        w = smooth((t - sE[k]) / max(1e-4, sS[k + 1] - sE[k]))
        if mode == 'new' and rN[k] < 3: return via_front(fp, fdir(nx, qa, qb), w, left)
        if mode == 'all_slerp': return slerp(fp, fdir(nx, qa, qb), w)
        if rN[k] < 3 and mode.startswith('turn_'):
            fq = fdir(nx, qa, qb)
            if mode == 'turn_pre': return fq
            if mode == 'turn_post': return fp
            if mode == 'turn_slerp': return slerp(fp, fq, w)
        pa, pb = tofr(nx, fp)
        return fdir(nx, (1 - w) * pa + w * qa, (1 - w) * pb + w * qb)
    def seg_info(t):
        k = int(np.searchsorted(sE, t, side='right') - 1)
        if k < 0 or k + 1 >= m: return ('edge', 9.0, 0.0)
        return (rN[k], sS[k + 1] - sE[k], (t - sE[k]) / max(1e-4, sS[k + 1] - sE[k]))
    dir_at.info = seg_info
    return dir_at

DIAG = {}
SPD = []
PROF = {}
def main():
    tpath, mode = sys.argv[1], sys.argv[2]
    T = Templates(tpath, keyed_rel=(mode != 'old'))
    allerr = []
    for p in sys.argv[3:]:
        info, t, F, ev = prep(p)
        errs = []
        for left, col, hand in ((True, 0, 'L'), (False, 1, 'R')):
            evs = sorted((tt, nid) for nid, tt in ev if (nid // 10) % 10 == col)
            notes = [dict(t=tt, li=(nid // 1000) % 10, la=(nid // 100) % 10, cd=nid % 10, id=nid) for tt, nid in evs]
            if len(notes) < 10: continue
            D = simulate(notes, left, T, mode)
            grid = np.arange(notes[0]['t'], notes[-1]['t'], 0.01)
            sim = np.array([D(x) for x in grid])
            real = fat(t, F[hand], grid)
            er = np.degrees(np.arccos(np.clip((sim * real).sum(1), -1, 1)))
            errs.append(er)
            SPD.append((np.degrees(np.arccos(np.clip((sim[1:]*sim[:-1]).sum(1),-1,1)))/0.01, np.degrees(np.arccos(np.clip((real[1:]*real[:-1]).sum(1),-1,1)))/0.01))
            for x, ee in zip(grid, er):
                r, g, u = D.info(x)
                if r == 2 and g < 0.6: PROF.setdefault(min(int(u * 5), 4), []).append(ee)
                gb = 'short' if g < 0.3 else 'mid' if g < 0.6 else 'long' if g < 0.9 else 'rest'
                DIAG.setdefault((r, gb), []).append(ee)
        e = np.concatenate(errs); allerr.append(e)
        print(f"{info['songName'][:28]:28s} {info['difficulty']:10s} angle err p50 {np.percentile(e,50):5.1f}  p75 {np.percentile(e,75):5.1f}  p90 {np.percentile(e,90):5.1f}  >75deg {np.mean(e>75):5.1%}")
    e = np.concatenate(allerr)
    if PROF and '--prof' in os.environ.get('SIMFLAGS', ''):
        print('90-degree turns (gap<0.6s): error by position inside the gap (0=just after previous hit, 4=just before next hit)')
        for b in sorted(PROF): v = np.array(PROF[b]); print(f'   u-bin {b}: p50 {np.percentile(v,50):5.1f}  >75deg {np.mean(v>75):5.1%}')
    if '--diag' in os.environ.get('SIMFLAGS', ''):
        tot = sum(len(v) for v in DIAG.values()); bad = sum(int(np.sum(np.array(v) > 75)) for v in DIAG.values())
        print('segment (turn, gap)        share of time   >75deg rate   share of all >75deg')
        for k in sorted(DIAG, key=lambda k: (str(k[0]), k[1])):
            v = np.array(DIAG[k])
            print(f'  turn={str(k[0]*45 if k[0]!="edge" else "edge"):>4} gap={k[1]:5s}   {len(v)/tot:6.1%}        {np.mean(v>75):6.1%}       {np.sum(v>75)/max(bad,1):6.1%}')
    sg = np.concatenate([a for a, b in SPD]); sr = np.concatenate([b for a, b in SPD])
    print(f"speed p50 sim {np.median(sg):.0f} real {np.median(sr):.0f} | p25 {np.percentile(sg,25):.0f}/{np.percentile(sr,25):.0f}")
    print(f"{'ALL':39s} angle err p50 {np.percentile(e,50):5.1f}  p75 {np.percentile(e,75):5.1f}  p90 {np.percentile(e,90):5.1f}  >75deg {np.mean(e>75):5.1%}")

if __name__ == '__main__':
    main()
