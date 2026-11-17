"""牆: 頭有沒有進到牆裡 (真人 / 生成都能算).
usage: wall_eval.py <譜面資料夾或hash> <難度> <回放.bsor> [更多回放...]
牆的位置 (照遊戲的規則): 每條軌道寬 0.6 m (第 i 軌 x 從 (i-2)*0.6 到 (i-1)*0.6);
底邊 = 層高 (0.25 / 0.85 / 1.45 ...) - 0.15 + 身高偏移 0.5*(設定身高-1.5); 高 = h * 0.6;
牆在 [time, time+duration] 期間經過玩家所在的位置. 頭當成一個點, 再留 HEAD_R 的半徑.
"""
import sys, os, json
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bsor_stats import load
import bomb_eval as be

HEAD_R = 0.08
LAYER_Y = lambda y: 0.25 + 0.6 * y   # 0.25 / 0.85 / 1.45 / ...


def load_walls(folder, diff):
    info = json.load(open(os.path.join(folder, 'Info.dat'), encoding='utf-8-sig')); bpm = info['_beatsPerMinute']
    fn = [b['_beatmapFilename'] for s in info['_difficultyBeatmapSets'] if s['_beatmapCharacteristicName'] == 'Standard'
          for b in s['_difficultyBeatmaps'] if b['_difficulty'] == diff][0]
    d = json.load(open(os.path.join(folder, fn), encoding='utf-8-sig'))
    if 'obstacles' in d and (not d['obstacles'] or 'b' in d['obstacles'][0]):
        ev = [(e.get('b', 0), e.get('m', bpm)) for e in d.get('bpmEvents', [])]
        obs = [(o['b'], o.get('d', 0), o.get('x', 0), o.get('y', 0), o.get('w', 1), o.get('h', 5)) for o in d.get('obstacles', [])]
    else:
        ev = [(e['_time'], e.get('_floatValue', bpm)) for e in d.get('_events', []) if e.get('_type') == 100]
        obs = [(o['_time'], o['_duration'], o['_lineIndex'], 2 if o['_type'] == 1 else 0, o['_width'], 3 if o['_type'] == 1 else 5)
               for o in d.get('_obstacles', [])]
    regions = [(0.0, bpm, 0.0)]
    for b, m in sorted(ev):
        if m <= 0: continue
        lb, lm, lt = regions[-1]
        if b <= lb: regions[-1] = (lb, m, lt); continue
        regions.append((b, m, lt + (b - lb) * 60 / lm))
    def sec(b):
        r = [x for x in regions if x[0] <= b][-1]; return r[2] + (b - r[0]) * 60 / r[1]
    return [(sec(b), sec(b + dd), x, y, w, h) for b, dd, x, y, w, h in obs if w > 0 and h > 0]


def analyze(replay, walls):
    info, fr, notes = load(replay)
    fr = fr[np.r_[True, np.diff(fr[:, 0]) > 0]]; t = fr[:, 0]
    yoff = 0.5 * (info['height'] - 1.5) if info['height'] > 1 else 0.0
    hits = []
    for t0, t1, x, y, w, h in walls:
        if t1 < t[0] or t0 > t[-1]: continue
        m = (t >= t0) & (t <= max(t1, t0 + 1 / 90))
        if not m.any(): continue
        hx, hy = fr[m, 1], fr[m, 2]
        x0, x1 = (x - 2) * 0.6, (x - 2 + w) * 0.6
        y0 = LAYER_Y(y) - 0.15 + yoff; y1 = y0 + h * 0.6
        inside = (hx > x0 - HEAD_R) & (hx < x1 + HEAD_R) & (hy > y0 - HEAD_R) & (hy < y1 + HEAD_R)
        if inside.any():
            # 離開牆需要的最小移動 (越大越"深")
            depth = np.max(np.minimum.reduce([hx - (x0 - HEAD_R), (x1 + HEAD_R) - hx, hy - (y0 - HEAD_R), (y1 + HEAD_R) - hy])[inside])
            hits.append((round(t0, 2), round(t1 - t0, 2), x, y, w, h, round(float(depth), 2), round(float(hx[inside].mean()), 2), round(float(hy[inside].mean()), 2)))
    return info, hits


def main():
    folder = be.find_folder(sys.argv[1]); diff = sys.argv[2]
    walls = load_walls(folder, diff)
    print(f'{os.path.basename(folder)} [{diff}]  牆 {len(walls)}')
    for p in sys.argv[3:]:
        info, hits = analyze(p, walls)
        print(f"  {os.path.basename(p)[-48:]:48s} {info['playerName'][:10]:10s} 頭進牆 {len(hits)} 次" + (f"  例 (時間, 長度, x, y, 寬, 高, 深度, 頭x, 頭y): {hits[:4]}" if hits else ''))


if __name__ == '__main__':
    main()
