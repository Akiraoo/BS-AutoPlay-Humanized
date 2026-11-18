"""訓練範圍以外的所有 ranked 譜: 每張譜用遊戲外生成器生成一次, 和同一張譜最近一份真人回放比較, 順便找 bug.
usage: ranked_batch.py <模型 model.json> [--before YYYY-MM-DD] [--jobs N] [--tag 名稱] [--only-analyze] [--args "..."]
輸出: <OUT>/<tag>/*.bsor 和 <OUT>/<tag>/report.json (每張譜一列), 最後印出彙總和最可疑的譜.
檢查項目: 生成失敗 / 漏切 / 戳頭 / 劍柄插眼 / 炸彈 / 牆 / 手速過快 / 一幀內大角度跳動 / 和真人的方向誤差 / 速度比.
"""
import sys, os, json, glob, re, datetime, subprocess, shlex, traceback
from concurrent.futures import ThreadPoolExecutor, ProcessPoolExecutor
import numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bsor_stats import load, qrot
import eval_batch as eb
import head_poke as hp
import hilt_eye as he
import bomb_eval as be
import wall_eval as we

GAME = eb.GAME
JOKE = {'6C9D463C1CF556246CF60BFAEF75FC4F8EA3053F', '875F9F57103004ACA7C4BFAC57601D21402CDB10'}


def pick(before):
    sc = json.load(open(os.path.join(GAME, 'UserData', 'AutoPlay', 'star-cache.json'), encoding='utf-8'))
    hd = json.load(open(os.path.join(GAME, 'UserData', 'SongCore', 'SongHashData.dat'), encoding='utf-8'))
    have = {v['songHash'].upper() for v in hd.values()}
    cut = datetime.datetime.strptime(before, '%Y-%m-%d').timestamp()
    cand = {}
    for f in glob.glob(os.path.join(GAME, 'UserData', 'BeatLeader', 'Replays', '*.bsor')):
        b = os.path.basename(f)
        m = re.match(r'\d+-(.*)-(Easy|Normal|Hard|Expert|ExpertPlus)-Standard-([0-9A-F]{40})-(\d+)\.bsor$', b)
        if not m or 'exit-' in b or int(m.group(4)) >= cut or m.group(3) not in have or m.group(3) in JOKE: continue
        s = sc.get(m.group(3) + '|' + m.group(2))
        if not s or not (s.get('blStatus') == 3 or s.get('ssRanked')): continue
        cand.setdefault((m.group(3), m.group(2)), []).append(f)
    out = []
    for (h, d), fs in sorted(cand.items()):
        s = sc[h + '|' + d]
        out.append(dict(hash=h, diff=d, real=max(fs, key=lambda f: int(re.search(r'-(\d+)\.bsor$', f).group(1))),
                        stars=s.get('blStars') or s.get('ssStars') or 0))
    return out


def gen(job):
    r = subprocess.run([eb.TOOL, 'generate', job['hash'], job['diff'], job['model'], job['out']] + job['args'],
                       capture_output=True, timeout=600)
    return r.returncode, (r.stdout + r.stderr).decode('utf-8', 'replace')[-600:]


def glitch(path):
    """一幀內方向跳很大 (> 25°, 而且前後幀都小得多) 和手速."""
    info, fr, notes = load(path)
    fr = fr[np.r_[True, np.diff(fr[:, 0]) > 0]]; t = fr[:, 0]; dt = np.diff(t)
    res = {}
    for name, off in (('L', 8), ('R', 15)):
        Q = fr[:, off + 3:off + 7] / np.linalg.norm(fr[:, off + 3:off + 7], axis=1, keepdims=True)
        f = qrot(Q, np.array([0, 0, 1.0]))
        a = np.degrees(np.arccos(np.clip((f[1:] * f[:-1]).sum(1), -1, 1)))
        nb = np.maximum(np.r_[a[1:], 0], np.r_[0, a[:-1]])
        spikes = np.where((a > 25) & (a > 2.5 * nb))[0]
        v = np.linalg.norm(np.diff(fr[:, off:off + 3], axis=0), axis=1) / dt
        res[name] = dict(spikes=[float(t[i]) for i in spikes][:10], nspike=int(len(spikes)),
                         vmax=float(v.max()), v99=float(np.percentile(v, 99)), tvmax=float(t[np.argmax(v)]))
    return res


def analyze(row):
    p = row['out']; r = dict(row)
    try:
        info, fr, notes = load(p)
        good = sum(1 for n in notes if n['type'] == 0)
        r['cut'] = good; r['bad'] = sum(1 for n in notes if n['type'] == 1); r['miss'] = sum(1 for n in notes if n['type'] == 2)
        r['bombhit'] = sum(1 for n in notes if n['type'] == 3)
        res, best, t, _ = hp.poke(p); r['poke10'] = res[0.10]; r['headmin'] = float(best.min()); r['t_headmin'] = float(t[np.argmin(best)])
        ev, bh, _ = he.hilt(p); r['eye15'] = ev[0.15]; r['eyemin'] = float(bh.min())
        g = glitch(p); r['glitch'] = g
        r['nspike'] = g['L']['nspike'] + g['R']['nspike']; r['vmax'] = max(g['L']['vmax'], g['R']['vmax'])
        folder = be.find_folder(row['hash'])
        try:
            bombs, njs = be.load_bombs(folder, row['diff'])
            if bombs:
                _, br = be.analyze(p, bombs, njs)
                d = np.array([x['d'] for x in br]) if br else np.array([9.0])
                r['nbombs'] = len(bombs); r['bombmin'] = float(d.min()); r['bomb20'] = int(np.sum(d < 0.20))
                r['t_bombmin'] = float(br[int(np.argmin(d))]['t']) if br else None
        except Exception as e:
            r['bomb_err'] = repr(e)[:120]
        try:
            walls = we.load_walls(folder, row['diff'])
            if walls:
                _, hits = we.analyze(p, walls)
                r['nwalls'] = len(walls); r['walltouch'] = len(hits)
                r['wallmax'] = float(max([h[6] for h in hits], default=0)); r['t_walls'] = [float(h[0]) for h in hits[:8]]
        except Exception as e:
            r['wall_err'] = repr(e)[:120]
        try:
            m = eb.metrics(eb.prep(p), eb.prep(row['real'])); s = eb.summary(m)
            r.update({k: float(v) for k, v in s.items()})
        except Exception as e:
            r['cmp_err'] = repr(e)[:120]
    except Exception:
        r['analyze_err'] = traceback.format_exc()[-400:]
    return r


def main():
    model = sys.argv[1]
    opt = lambda k, d: sys.argv[sys.argv.index(k) + 1] if k in sys.argv else d
    before = opt('--before', '2026-10-18'); jobs = int(opt('--jobs', '8')); tag = opt('--tag', 'v4')
    extra = shlex.split(opt('--args', ''))
    outdir = os.path.join(eb.OUT, 'ranked_' + tag); os.makedirs(outdir, exist_ok=True)
    rows = pick(before)
    for r in rows:
        r['model'] = model; r['args'] = extra; r['out'] = os.path.join(outdir, f"{r['hash'][:8]}_{r['diff']}.bsor")
    print(f'{len(rows)} 張 ranked 譜 (早於 {before}) -> {outdir}', flush=True)
    if '--only-analyze' not in sys.argv:
        with ThreadPoolExecutor(jobs) as ex:
            for r, (rc, log) in zip(rows, ex.map(gen, rows)):
                r['rc'] = rc
                if rc != 0: r['genlog'] = log; print('生成失敗', r['hash'][:8], r['diff'], log[-200:].replace('\n', ' '), flush=True)
    ok = [r for r in rows if os.path.exists(r['out']) and r.get('rc', 0) == 0]
    with ProcessPoolExecutor(jobs) as ex:
        res = list(ex.map(analyze, ok))
    res += [r for r in rows if r not in ok]
    json.dump(res, open(os.path.join(outdir, 'report.json'), 'w', encoding='utf-8'), ensure_ascii=False, indent=1, default=str)
    A = [r for r in res if 'cut' in r]
    f = lambda k: np.array([r[k] for r in A if r.get(k) is not None], float)
    print(f'\n=== {len(A)} 張分析成功, 失敗 {len(res) - len(A)}')
    print(f"漏切/壞切 總計 {f('miss').sum():.0f} / {f('bad').sum():.0f}, 炸彈命中 {f('bombhit').sum():.0f}")
    print(f"戳頭 (<10cm) 有的譜 {np.sum(f('poke10') > 0)}  劍柄插眼 (<15cm) {np.sum(f('eye15') > 0)}  炸彈 <20cm {np.sum(f('bomb20') > 0)}  碰牆 {np.sum(f('walltouch') > 0)}")
    print(f"一幀跳動 總計 {f('nspike').sum():.0f}  手速最大 中位 {np.median(f('vmax')):.1f} 最大 {f('vmax').max():.1f} m/s")
    if len(f('err')):
        print(f"方向誤差中位 {np.median(f('err')):.1f}°  >75° {np.median(f('over75')):.1f}%  角速度比 {np.median(f('wratio')):.2f}  手速比 {np.median(f('vratio')):.2f}")
    print('\n=== 可疑的譜')
    for r in A:
        why = []
        if r['miss'] or r['bad']: why.append(f"漏 {r['miss']} 壞 {r['bad']}")
        if r['bombhit']: why.append(f"炸彈 {r['bombhit']}")
        if r['poke10'] > 0: why.append(f"戳頭 {r['headmin']*100:.0f}cm@{r['t_headmin']:.1f}s")
        if r['eye15'] > 0: why.append(f"插眼 {r['eyemin']*100:.0f}cm")
        if r.get('bomb20'): why.append(f"炸彈近 {r['bombmin']*100:.0f}cm@{r['t_bombmin']:.1f}s")
        if r.get('walltouch'): why.append(f"牆 {r['walltouch']} @{r['t_walls'][:3]}")
        if r['nspike']: why.append(f"跳動 {r['nspike']} @{(r['glitch']['L']['spikes'] + r['glitch']['R']['spikes'])[:3]}")
        if r['vmax'] > 15: why.append(f"手速 {r['vmax']:.0f}")
        if r.get('over75', 0) > 10: why.append(f">75° {r['over75']:.0f}%")
        if why: print(f"{r['hash'][:8]} {r['diff']:10s} ★{r['stars']:.1f} | " + ' | '.join(why))
    for r in res:
        if 'cut' not in r: print('失敗', r['hash'][:8], r['diff'], (r.get('analyze_err') or r.get('genlog') or '')[-200:].replace('\n', ' '))


if __name__ == '__main__':
    main()
