"""閒置時手的微動: 離最近一次切擊 (同一隻手) 超過 GAP 秒的片段, 劍指向的角速度 / 手 (相對頭) 的速度, 以及各頻段的幅度.
usage: idle_stats.py <回放.bsor> [...]   多份時合併
"""
import sys, numpy as np
from bsor_stats import load, qrot
GAP = 0.6
BANDS = ((0.0, 1.0), (1.0, 2.0), (2.0, 4.0), (4.0, 10.0))


def segs(path):
    info, fr, notes = load(path)
    fr = fr[np.r_[True, np.diff(fr[:, 0]) > 0]]; t = fr[:, 0]
    g = np.arange(t[0], t[-1], 1 / 60)
    out = []
    for c, off in ((0, 8), (1, 15)):
        cuts = np.array(sorted(n['t'] for n in notes if n['type'] in (0, 3) and ((n['id'] // 10) % 10 == c or n['type'] == 3)))
        if not len(cuts): continue
        Q = np.stack([np.interp(g, t, fr[:, off + 3 + j]) for j in range(4)], 1); Q /= np.linalg.norm(Q, axis=1, keepdims=True)
        f = qrot(Q, np.array([0, 0, 1.0]))
        p = np.stack([np.interp(g, t, fr[:, off + j] - fr[:, 1 + j]) for j in range(3)], 1)
        idx = np.searchsorted(cuts, g); dn = np.minimum(np.abs(g - cuts[np.clip(idx - 1, 0, len(cuts) - 1)]), np.abs(g - cuts[np.clip(idx, 0, len(cuts) - 1)]))
        idle = dn > GAP
        # 連續片段 >= 1 s
        k = 0
        while k < len(g):
            if not idle[k]: k += 1; continue
            e = k
            while e < len(g) and idle[e]: e += 1
            if e - k >= 60: out.append((f[k:e], p[k:e]))
            k = e
    return out


def main():
    S = []
    for p in sys.argv[1:]: S += segs(p)
    w = np.concatenate([np.degrees(np.arccos(np.clip((f[1:] * f[:-1]).sum(1), -1, 1))) * 60 for f, _ in S])
    v = np.concatenate([np.linalg.norm(np.diff(p, axis=0), axis=1) * 60 for _, p in S])
    band = np.zeros((len(BANDS), 2)); n = 0
    for f, p in S:
        L = len(f); fq = np.fft.rfftfreq(L, 1 / 60)
        ang = np.degrees(np.arcsin(np.clip(f[:, :2] - f[:, :2].mean(0), -1, 1)))   # 約略: 左右 / 上下 的角度
        for j, (lo, hi) in enumerate(BANDS):
            m = (fq >= lo) & (fq < hi) & (fq > 0)
            A = np.abs(np.fft.rfft(ang - ang.mean(0), axis=0))[m]; P = np.abs(np.fft.rfft(p - p.mean(0), axis=0))[m]
            band[j, 0] += np.sqrt((A ** 2).sum() * 2) / L; band[j, 1] += np.sqrt((P ** 2).sum() * 2) / L * 100
        n += 1
    print(f'片段 {len(S)}  閒置總長 {sum(len(f) for f,_ in S)/60:.0f}s   角速度 中位 {np.median(w):.0f}°/s P90 {np.percentile(w,90):.0f}   手速 中位 {np.median(v)*100:.1f} cm/s P90 {np.percentile(v,90)*100:.1f}')
    print('   頻段 RMS (角度°, 位置cm): ' + '  '.join(f'{lo:.0f}-{hi:.0f}Hz {band[j,0]/n:.2f}° {band[j,1]/n:.2f}cm' for j, (lo, hi) in enumerate(BANDS)))


if __name__ == '__main__':
    main()
