"""批次評估: 在訓練資料以外的真人回放上, 用遊戲外生成器比較不同模型 / 引數.
usage: eval_batch.py <variants.json> [--maps N] [--before YYYY-MM-DD] [--seed S]
variants.json: [{"name": "...", "model": "<模型路徑>", "args": ["--no-arm", ...]}, ...]
每張譜: 從玩家回放挑一份 (日期早於 --before, 也就是不在訓練範圍), 每個變體生成一次, 和那份真人回放比較.
指標: 方向誤差中位 / >75° 比例 / 手的位置誤差 (相對頭) / 角速度和手速 相對真人的比例 / 收勢與預揮最遠角度
"""
import sys, os, json, glob, subprocess, random, datetime, re
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bsor_stats import load, qrot

GAME = r'D:\Steam\steamapps\common\Beat Saber'
TOOL = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'ApModelTool', 'bin', 'Release', 'net10.0', 'ApModelTool.exe')
OUT = os.environ.get('EVAL_OUT', os.path.join(os.environ.get('TEMP', '.'), 'ap_eval'))


def unit(v): return v / np.linalg.norm(v, axis=-1, keepdims=True)


def prep(p):
    info, fr, notes = load(p)
    fr = fr[np.r_[True, np.diff(fr[:, 0]) > 0]]; t = fr[:, 0]
    hf = qrot(fr[:, 4:8], np.array([0, 0, 1.0])); psi = np.arctan2(hf[:, 0].mean(), hf[:, 2].mean())
    c, s = np.cos(-psi), np.sin(-psi)
    R = lambda v: np.stack([c * v[:, 0] + s * v[:, 2], v[:, 1], -s * v[:, 0] + c * v[:, 2]], 1)
    d = dict(info=info, notes=notes, t=t, head=R(fr[:, 1:4]))
    for h, off, st in (('L', 8, 0), ('R', 15, 1)):
        d[h + 'pos'] = R(fr[:, off:off + 3]); d[h + 'f'] = R(qrot(fr[:, off + 3:off + 7], np.array([0, 0, 1.0])))
        cuts = np.array(sorted(n['t'] for n in notes if n['type'] == 0 and n['cut'] and n['cut']['stype'] == st))
        sw = [cuts[0]] if len(cuts) else []
        for x in cuts[1:]:
            if x - sw[-1] > 0.06: sw.append(x)
        d[h + 'sw'] = np.array(sw)
    return d


def at(d, k, g): return np.stack([np.interp(g, d['t'], d[k][:, j]) for j in range(3)], 1)


def metrics(gen, real):
    ts = [n['t'] for n in real['notes'] if n['type'] == 0]
    g = np.arange(min(ts), max(ts), 1 / 90)
    out = dict(err=[], pos=[], wg=[], wr=[], vg=[], vr=[], post_g=[], post_r=[], pre_g=[], pre_r=[])
    for h in 'LR':
        fg, fr_ = unit(at(gen, h + 'f', g)), unit(at(real, h + 'f', g))
        out['err'].append(np.degrees(np.arccos(np.clip((fg * fr_).sum(1), -1, 1))))
        pg = at(gen, h + 'pos', g) - at(gen, 'head', g); pr = at(real, h + 'pos', g) - at(real, 'head', g)
        out['pos'].append(np.linalg.norm(pg - pr, axis=1))
        out['wg'].append(np.degrees(np.arccos(np.clip((fg[1:] * fg[:-1]).sum(1), -1, 1))) * 90)
        out['wr'].append(np.degrees(np.arccos(np.clip((fr_[1:] * fr_[:-1]).sum(1), -1, 1))) * 90)
        out['vg'].append(np.linalg.norm(np.diff(pg, axis=0), axis=1) * 90)
        out['vr'].append(np.linalg.norm(np.diff(pr, axis=0), axis=1) * 90)
        for d, kpost, kpre in ((gen, 'post_g', 'pre_g'), (real, 'post_r', 'pre_r')):
            sw = d[h + 'sw']
            for i in range(1, len(sw) - 1):
                if not (0.3 <= sw[i + 1] - sw[i] < 1.0 and 0.3 <= sw[i] - sw[i - 1] < 1.0): continue
                f0 = unit(at(d, h + 'f', np.array([sw[i]])))[0]
                a = np.degrees(np.arccos(np.clip(unit(at(d, h + 'f', sw[i] + np.arange(0.02, 0.4, 0.02))) @ f0, -1, 1)))
                b = np.degrees(np.arccos(np.clip(unit(at(d, h + 'f', sw[i] - np.arange(0.02, 0.5, 0.02))) @ f0, -1, 1)))
                out[kpost].append(a.max()); out[kpre].append(b.max())
    return {k: (np.concatenate(v) if v and isinstance(v[0], np.ndarray) else np.array(v)) for k, v in out.items()}


def summary(m):
    return dict(err=np.median(m['err']), over75=np.mean(m['err'] > 75) * 100, pos=np.median(m['pos']) * 100,
                wratio=np.median(m['wg']) / np.median(m['wr']), vratio=np.median(m['vg']) / np.median(m['vr']),
                post=np.median(m['post_g']), post_r=np.median(m['post_r']), pre=np.median(m['pre_g']), pre_r=np.median(m['pre_r']))


def pick_maps(n, before, seed):
    hd = json.load(open(os.path.join(GAME, 'UserData', 'SongCore', 'SongHashData.dat'), encoding='utf-8'))
    have = {v['songHash'].upper() for v in hd.values()}
    cut = datetime.datetime.strptime(before, '%Y-%m-%d').timestamp()
    files = glob.glob(os.path.join(GAME, 'UserData', 'BeatLeader', 'Replays', '*.bsor'))
    cand = {}
    for f in files:
        m = re.match(r'\d+-(?:exit-)?(.*)-(Expert|ExpertPlus)-Standard-([0-9A-F]{40})-(\d+)\.bsor$', os.path.basename(f))
        if not m or 'exit-' in os.path.basename(f) or int(m.group(4)) >= cut or m.group(3) not in have: continue
        cand.setdefault((m.group(3), m.group(2)), []).append(f)
    keys = sorted(cand); random.Random(seed).shuffle(keys)
    picked = []
    for k in keys:
        f = max(cand[k], key=os.path.getmtime)
        try:
            info, fr, notes = load(f)
        except Exception:
            continue
        if info['leftHanded'] or any(x in info['modifiers'] for x in ('FS', 'SF', 'SS')) or len(notes) < 200: continue
        picked.append((k[0], k[1], f, info['songName']))
        if len(picked) >= n: break
    return picked


def main():
    variants = json.load(open(sys.argv[1], encoding='utf-8'))
    opt = lambda k, d: sys.argv[sys.argv.index(k) + 1] if k in sys.argv else d
    maps = pick_maps(int(opt('--maps', '10')), opt('--before', '2026-10-18'), int(opt('--seed', '1')))
    os.makedirs(OUT, exist_ok=True)
    allm = {v['name']: [] for v in variants}
    for h, diff, real_path, song in maps:
        real = prep(real_path)
        line = f'{song[:26]:26s} {diff:10s}'
        for v in variants:
            out = os.path.join(OUT, f"{v['name']}_{h[:8]}_{diff}.bsor")
            r = subprocess.run([TOOL, 'generate', h, diff, v['model'], out] + v.get('args', []), capture_output=True)
            if r.returncode != 0: line += f" | {v['name']}: FAIL"; continue
            m = metrics(prep(out), real); allm[v['name']].append(m); s = summary(m)
            line += f" | {v['name']}: {s['err']:4.1f}° {s['over75']:4.1f}% {s['pos']:4.1f}cm"
        print(line, flush=True)
    print('\n=== 全部 (方向誤差中位 / >75° / 位置誤差中位 / 角速度比 / 手速比 / 收勢最遠 (真人) / 預備最遠 (真人))')
    for v in variants:
        ms = allm[v['name']]
        if not ms: continue
        cat = {k: np.concatenate([m[k] for m in ms]) for k in ms[0]}
        s = summary(cat)
        print(f"{v['name']:18s} {s['err']:5.1f}°  {s['over75']:5.2f}%  {s['pos']:5.1f} cm  ω×{s['wratio']:.2f}  v×{s['vratio']:.2f}  "
              f"收勢 {s['post']:5.1f}° ({s['post_r']:5.1f})  預備 {s['pre']:5.1f}° ({s['pre_r']:5.1f})")


if __name__ == '__main__':
    main()
