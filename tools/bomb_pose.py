"""炸彈經過時兩隻手的姿勢: 按炸彈所在的層 (0 下 / 1 中 / 2 上) 統計劍的指向 (往上 f.y)、劍尖高度、兩手的 x (往內為負).
usage: bomb_pose.py <譜面hash> <難度> <回放.bsor> [...]
"劍插地": 劍尖低於 0.1 m;  "內八": 兩隻手的劍都指向下且劍尖往中間交叉 (左劍尖 x > 右劍尖 x 或兩劍尖都在中線 ±0.15 m)
"""
import sys, os, numpy as np
from bsor_stats import load, qrot
import bomb_eval as be


def pose(replay, bombs):
    info, fr, notes = load(replay)
    fr = fr[np.r_[True, np.diff(fr[:, 0]) > 0]]; t = fr[:, 0]
    cut_t = np.array(sorted(n['t'] for n in notes if n['type'] == 0))
    res = {0: [], 1: [], 2: []}
    seen = set()
    for tb, x, y in bombs:
        k = round(tb, 2)
        if k in seen or tb < t[0] or tb > t[-1]: continue
        seen.add(k)
        if len(cut_t) and np.min(np.abs(cut_t - tb)) < 0.12: continue      # 同時有方塊要切的不算
        i = np.searchsorted(t, tb)
        row = []
        for off in (8, 15):
            q = fr[i, off + 3:off + 7]; q = q / np.linalg.norm(q)
            f = qrot(q[None], np.array([0, 0, 1.0]))[0]; w = fr[i, off:off + 3] - f * be.POMMEL
            tip = w + f * be.S1
            row.append((f[1], tip[1], tip[0], w[0]))
        res[min(int(y), 2)].append(row)
    return res


def main():
    h, diff = sys.argv[1], sys.argv[2]
    bombs, _ = be.load_bombs(be.find_folder(h), diff)
    for rp in sys.argv[3:]:
        r = pose(rp, bombs)
        print(os.path.basename(rp))
        for y in (0, 1, 2):
            a = np.array(r[y])
            if not len(a): continue
            fy = a[:, :, 0]; tipy = a[:, :, 1]; tipx = a[:, :, 2]
            ground = np.mean(tipy < 0.1) * 100
            down = np.mean(fy < -0.5) * 100
            inner8 = np.mean((fy[:, 0] < -0.3) & (fy[:, 1] < -0.3) & ((tipx[:, 0] > tipx[:, 1]) | ((np.abs(tipx[:, 0]) < 0.15) & (np.abs(tipx[:, 1]) < 0.15)))) * 100
            print(f'  第{y}層炸彈 n={len(a):4d}  劍朝下(f.y<-0.5) {down:5.1f}%  劍尖<0.1m {ground:5.1f}%  內八 {inner8:5.1f}%  劍指向 f.y 中位 {np.median(fy):+.2f}  劍尖高 中位 {np.median(tipy):.2f} m')


if __name__ == '__main__':
    main()
