import sys, os, numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bsor_stats import load, qrot
from wrist_fit_lib import roll_of
for p in sys.argv[1:]:
    info, fr, notes = load(p)
    t = fr[:, 0]
    good=[n['t'] for n in notes if n['type']==0]; m=(t>=min(good)-1)&(t<=max(good)+1); fr=fr[m]; t=fr[:,0]
    hits = {s: np.array([n['t'] for n in notes if n['type']==0 and n['cut'] and n['cut']['stype']==s]) for s in (0,1)}
    print('==', info['playerName'][:10], os.path.basename(p)[:50], 'fps %.0f' % (len(t)/(t[-1]-t[0])))
    for name, off, st in (('L', 8, 0), ('R', 15, 1)):
        P, Q = fr[:, off:off+3], fr[:, off+3:off+7]
        f = qrot(Q, np.array([0,0,1.0]))
        dt = np.diff(t)
        w = np.degrees(np.arccos(np.clip((f[1:]*f[:-1]).sum(1), -1, 1))) / dt  # deg/s
        wr = np.degrees(np.abs(np.angle(np.exp(1j*np.diff(roll_of(Q, f)))))) / dt
        v = np.linalg.norm(np.diff(P, axis=0), axis=1) / dt
        nb = np.maximum(np.r_[w[1:], 0], np.r_[0, w[:-1]])
        spike = (w > 1000) & (w > 2.0 * nb)
        rollflip = wr > 4000
        acc = np.abs(np.diff(w)) / dt[1:]
        print(f' {name}: ang vel p99 {np.percentile(w,99):.0f} max {w.max():.0f} deg/s | roll vel p99 {np.percentile(wr,99):.0f} max {wr.max():.0f} | lin vel p99 {np.percentile(v,99):.1f} max {v.max():.1f} m/s | ang acc p99 {np.percentile(acc,99):.0f} | spikes {spike.sum()} rollflips {rollflip.sum()}')
        for i in list(np.where(spike | rollflip)[0][:6]):
            h = hits[st]; k = np.argmin(np.abs(h - t[i])) if len(h) else 0
            print(f'    t={t[i]:7.3f} w={w[i]:5.0f} nb={nb[i]:5.0f} roll={wr[i]:5.0f} v={v[i]:4.1f} hit dt={h[k]-t[i] if len(h) else 9:+.3f} fwd-angle={np.degrees(np.arccos(np.clip(f[i,2],-1,1))):.0f}')
