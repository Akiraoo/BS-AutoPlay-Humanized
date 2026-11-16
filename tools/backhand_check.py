"""Twist at the cut by hand x cut direction: human vs the f-only twist model (to see if outward cuts are rotated)."""
import sys, os, json, numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bsor_stats import load, qrot
from wrist_fit_lib import roll_of, feats
M = json.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), 'model.json')))
DIRN = ['U','D','L','R','UL','UR','DL','DR','dot']
acc = {}
for p in sys.argv[1:]:
    info, fr, notes = load(p)
    t = fr[:,0]; keep = np.r_[True, np.diff(t)>0]; fr = fr[keep]; t = fr[:,0]
    for n in notes:
        if n['type']!=0: continue
        nid=n['id']; col=(nid//10)%10; hand, off = ('L',8) if col==0 else ('R',15)
        i=min(max(np.searchsorted(t,n['t']),1),len(t)-1)
        for dt_ in (-0.06, 0.0):
            j=min(max(np.searchsorted(t,n['t']+dt_),1),len(t)-1)
            Q=fr[j:j+1,off+3:off+7]; Q=Q/np.linalg.norm(Q); f=qrot(Q,np.array([0,0,1.0]))
            r=roll_of(Q,f)[0]; C=np.array(M[hand]['rollC']).T; P=feats(f)@C; pr=np.arctan2(P[0,1],P[0,0])
            acc.setdefault((hand,DIRN[nid%10],dt_),[]).append((np.degrees(r), np.degrees(np.angle(np.exp(1j*(r-pr))))))
print('hand dir  when   n  | human roll (circ mean, spread) | human - model roll: median  |err|p50')
for k in sorted(acc, key=lambda k:(k[0],k[1],k[2])):
    a=np.array(acc[k]);
    if len(a)<20: continue
    z=np.exp(1j*np.radians(a[:,0])); cm=np.degrees(np.angle(z.mean())); R=abs(z.mean())
    print(f'{k[0]} {k[1]:3s} {k[2]:+.2f}s {len(a):4d} | {cm:7.1f}° R={R:.2f} | {np.median(a[:,1]):+6.1f}°  {np.percentile(np.abs(a[:,1]),50):5.1f}°')
