"""炸弹: 每颗炸弹飞过时, 光剑离它最近的距离 (真人 / 生成都能算).
usage: bomb_eval.py <谱面资料夹或hash> <难度> <回放.bsor> [更多回放...]
几何和 NoteSwingPlanner.BombAvoider 一样: 炸弹在 note.time 时 z=0.72, 以 NJS 往玩家飞;
光剑 = 手腕 (控制器往回 0.20) 往指向 0.30~1.60 m 的线段; 距离 < 0.20 (炸弹 0.18 + 剑 0.02) 算撞到.
另外记录最近点在炸弹的哪一侧 (上/下/左/右), 用来看真人怎么绕开.
"""
import sys, os, json, math
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bsor_stats import load, qrot

GAME = r'D:\Steam\steamapps\common\Beat Saber'
HIT_Z, RADIUS, S0, S1, POMMEL = 0.72, 0.20, 0.30, 1.60, 0.20


def find_folder(h):
    if os.path.isdir(h): return h
    hd = json.load(open(os.path.join(GAME, 'UserData', 'SongCore', 'SongHashData.dat'), encoding='utf-8'))
    for k, v in hd.items():
        if v['songHash'].upper() == h.upper():
            return os.path.join(GAME, k[2:] if k.startswith('.\\') else k)
    raise SystemExit('map not found')


def load_bombs(folder, diff):
    info = json.load(open(os.path.join(folder, 'Info.dat'), encoding='utf-8-sig'))
    bpm = info['_beatsPerMinute']; njs = 0; fn = None; bpmev = []
    for s in info['_difficultyBeatmapSets']:
        if s['_beatmapCharacteristicName'] != 'Standard': continue
        for b in s['_difficultyBeatmaps']:
            if b['_difficulty'] == diff: fn, njs = b['_beatmapFilename'], b['_noteJumpMovementSpeed']
    d = json.load(open(os.path.join(folder, fn), encoding='utf-8-sig'))
    if 'colorNotes' in d:
        bombs = [(x.get('b', 0), x.get('x', 0), x.get('y', 0)) for x in d.get('bombNotes', [])]
        bpmev = [(e.get('b', 0), e.get('m', bpm)) for e in d.get('bpmEvents', [])]
    else:
        bombs = [(n['_time'], n['_lineIndex'], n['_lineLayer']) for n in d['_notes'] if n['_type'] == 3]
        bpmev = [(e['_time'], e.get('_floatValue', bpm)) for e in d.get('_events', []) if e.get('_type') == 100]
    regions = [(0.0, bpm, 0.0)]
    for b, m in sorted(bpmev):
        if m <= 0: continue
        lb, lm, lt = regions[-1]
        if b <= lb: regions[-1] = (lb, m, lt); continue
        regions.append((b, m, lt + (b - lb) * 60 / lm))
    def sec(beat):
        r = [x for x in regions if x[0] <= beat][-1]
        return r[2] + (beat - r[0]) * 60 / r[1]
    return [(sec(b), x, y) for b, x, y in bombs], njs or 16


def seg_dist(p, a, b):
    ab = b - a; t = np.clip(((p - a) * ab).sum(-1) / (ab * ab).sum(-1), 0, 1)
    q = a + ab * t[..., None]
    return np.linalg.norm(p - q, axis=-1), q


def analyze(replay, bombs, njs):
    info, fr, notes = load(replay)
    fr = fr[np.r_[True, np.diff(fr[:, 0]) > 0]]; t = fr[:, 0]
    yoff = 0.5 * (info['height'] - 1.5) if info['height'] > 1 else 0.0
    hit_bombs = sorted(n['t'] for n in notes if n['type'] == 3)
    res = []
    for tb, x, y in bombs:
        bp0 = np.array([(x - 1.5) * 0.6, 0.72 + y * 0.53 + yoff])
        g = np.arange(tb - 0.25, tb + 0.12, 1 / 240)
        if g[0] < t[0] or g[-1] > t[-1]: continue
        bz = HIT_Z + njs * (tb - g)
        bomb = np.stack([np.full_like(g, bp0[0]), np.full_like(g, bp0[1]), bz], 1)
        best = (9.0, None, None)
        for off in (8, 15):
            P = np.stack([np.interp(g, t, fr[:, off + j]) for j in range(3)], 1)
            Q = np.stack([np.interp(g, t, fr[:, off + 3 + j]) for j in range(4)], 1)
            f = qrot(Q / np.linalg.norm(Q, axis=1, keepdims=True), np.array([0, 0, 1.0]))
            wrist = P - f * POMMEL
            dist, q = seg_dist(bomb, wrist + f * S0, wrist + f * S1)
            i = int(np.argmin(dist))
            if dist[i] < best[0]: best = (dist[i], q[i] - bomb[i], 'L' if off == 8 else 'R')
        d, rel, hand = best
        side = None
        if rel is not None:
            side = 'above' if abs(rel[1]) >= abs(rel[0]) and rel[1] > 0 else 'below' if abs(rel[1]) >= abs(rel[0]) else ('right' if rel[0] > 0 else 'left')
        hit = any(abs(h - tb) < 0.05 for h in hit_bombs)
        res.append(dict(t=tb, x=x, y=y, d=d, side=side, hand=hand, hit=hit))
    return info, res


def main():
    folder = find_folder(sys.argv[1]); diff = sys.argv[2]
    bombs, njs = load_bombs(folder, diff)
    print(f'{os.path.basename(folder)} [{diff}]  炸彈 {len(bombs)}  NJS {njs}')
    for p in sys.argv[3:]:
        info, r = analyze(p, bombs, njs)
        d = np.array([x['d'] for x in r])
        sides = {}
        for x in r:
            if x['d'] < 0.45: sides[x['side']] = sides.get(x['side'], 0) + 1
        print(f"  {os.path.basename(p)[:60]:60s} 玩家 {info['playerName'][:10]:10s}"
              f"  距離 p10 {np.percentile(d,10):.2f} p50 {np.percentile(d,50):.2f} m | <0.20 {np.sum(d<0.20):3d}  <0.27 {np.sum(d<0.27):3d}"
              f" | 記錄的炸彈命中 {sum(x['hit'] for x in r)} | 近距離時在炸彈的 {sides}")


if __name__ == '__main__':
    main()
