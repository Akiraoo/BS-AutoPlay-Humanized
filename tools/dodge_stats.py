"""左右闪墙: 真人 (或生成) 遇到只盖住中间一条轨道的全高墙时, 头怎么动.
usage: dodge_stats.py [--maps N] [--seed S] [--replay 生成.bsor 谱面hash 难度]...
只取 "孤立" 的墙 (前后 1.5 秒内没有别的会影响头的墙), 以墙到达为 0, 统计:
  头的横向位移 (往墙的反方向为正)、歪头 (roll, 往墙的反方向为正)、开始 / 最远 / 回来的时间、每次之间的差异
"""
import sys, os, glob, json, random, re
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bsor_stats import load, qrot
import wall_eval as we, bomb_eval as be

TAU = np.arange(-0.6, 1.01, 0.1)


def head_roll(q):
    # 头的 roll: 头的"右"向量的 y 分量 (往右歪 = 右边低)
    r = qrot(q, np.array([1.0, 0, 0]))
    return np.degrees(np.arcsin(np.clip(-r[:, 1], -1, 1)))


def events(replay, walls, mirror=False):
    info, fr, notes = load(replay)
    fr = fr[np.r_[True, np.diff(fr[:, 0]) > 0]]; t = fr[:, 0]
    hx, roll = fr[:, 1], head_roll(fr[:, 4:8])
    bx = np.median(hx); br = np.median(roll)
    yoff = 0.5 * (info['height'] - 1.5) if info['height'] > 1 else 0.0
    head_h = np.median(fr[:, 2])
    rel = []
    for (t0, t1, x, y, w, h) in walls:
        bottom = 0.25 + 0.6 * y - 0.15 + yoff; top = bottom + 0.6 * h
        if top < head_h - 0.15 or bottom > head_h + 0.1: continue          # 不挡头
        c0, c1 = max(x, 1), min(x + w - 1, 2)
        if c0 > c1: continue
        rel.append((t0, t1, c0, c1, bottom))
    out = []
    for i, (t0, t1, c0, c1, bottom) in enumerate(rel):
        if c0 != c1: continue                       # 只要盖住一条中间轨道的
        if bottom > 1.0: continue                   # 要闪不能蹲 (从低处盖过头)
        if any(abs(o[0] - t0) < 1.5 and o is not rel[i] for o in rel): continue   # 孤立
        if t0 - 0.7 < t[0] or t1 + 1.1 > t[-1]: continue
        side = 1.0 if c0 == 1 else -1.0             # 墙在左中 (第2轨) -> 往右闪 (+)
        dx = (np.interp(t0 + TAU, t, hx) - bx) * side
        dr = (np.interp(t0 + TAU, t, roll) - br) * side
        out.append((dx, dr, t1 - t0))
    return out


def summarize(name, ev):
    if not ev: print(name, '沒有符合的牆'); return
    dx = np.array([e[0] for e in ev]); dr = np.array([e[1] for e in ev]); dur = np.array([e[2] for e in ev])
    print(f'{name}: {len(ev)} 次 (牆長 中位 {np.median(dur):.2f}s)')
    print('   τ       ' + ' '.join(f'{x:+.1f} ' for x in TAU))
    print('   橫移 m  ' + ' '.join(f'{v:+.2f}' for v in np.median(dx, 0)))
    print('   歪頭 °  ' + ' '.join(f'{v:+5.1f}' for v in np.median(dr, 0)))
    peak = dx.max(1); rpk = np.abs(dr).max(1)
    print(f'   每次最遠橫移: 中位 {np.median(peak):.2f} m  (P10 {np.percentile(peak,10):.2f} ~ P90 {np.percentile(peak,90):.2f})'
          f'   最大歪頭: 中位 {np.median(rpk):.1f}° (P10 {np.percentile(rpk,10):.1f} ~ P90 {np.percentile(rpk,90):.1f})')
    tp = TAU[np.argmax(dx, 1)]
    print(f'   最遠點時間: 中位 {np.median(tp):+.2f}s (P10 {np.percentile(tp,10):+.2f} ~ P90 {np.percentile(tp,90):+.2f})')


def main():
    args = sys.argv[1:]
    if '--replay' in args:
        i = args.index('--replay'); rp, h, diff = args[i + 1:i + 4]
        summarize(os.path.basename(rp), events(rp, we.load_walls(be.find_folder(h), diff))); return
    n_max = int(args[args.index('--maps') + 1]) if '--maps' in args else 40
    seed = int(args[args.index('--seed') + 1]) if '--seed' in args else 5
    G = be.GAME
    hd = json.load(open(os.path.join(G, 'UserData', 'SongCore', 'SongHashData.dat'), encoding='utf-8'))
    byhash = {v['songHash'].upper(): os.path.join(G, k[2:]) for k, v in hd.items()}
    files = glob.glob(os.path.join(G, 'UserData', 'BeatLeader', 'Replays', '*.bsor')); random.Random(seed).shuffle(files)
    ev, n = [], 0
    for f in files:
        m = re.match(r'\d+-(?:exit-)?(.*)-(Hard|Expert|ExpertPlus)-Standard-([0-9A-F]{40})-\d+\.bsor$', os.path.basename(f))
        if not m or m.group(3) not in byhash: continue
        try:
            walls = we.load_walls(byhash[m.group(3)], m.group(2))
            if len(walls) < 5: continue
            e = events(f, walls)
        except Exception:
            continue
        if not e: continue
        ev += e; n += 1
        if n >= n_max: break
    summarize(f'真人 ({n} 份回放)', ev)


if __name__ == '__main__':
    main()
