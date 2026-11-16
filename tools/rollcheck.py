import sys, os, json, numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from bsor_stats import load, qrot
from wrist_fit_lib import roll_of, feats
M = json.load(open(sys.argv[1]))
for p in sys.argv[2:]:
    info, fr, notes = load(p)
    good=[n['t'] for n in notes if n['type']==0]; t=fr[:,0]; fr=fr[(t>=min(good))&(t<=max(good))]
    for name, off in (('L', 8), ('R', 15)):
        Q = fr[:, off+3:off+7]; Q = Q/np.linalg.norm(Q,axis=1,keepdims=True)
        f = qrot(Q, np.array([0,0,1.0])); roll = roll_of(Q, f)
        C = np.array(M[name]['rollC']).T
        pr = np.arctan2(*(feats(f) @ C).T[::-1])
        err = np.degrees(np.angle(np.exp(1j*(roll-pr))))
        ang = np.degrees(np.arccos(np.clip(f[:,2],-1,1)))
        print(os.path.basename(p)[:40], name, 'roll err vs human model |p50|/p90:', np.percentile(np.abs(err),[50,90]).round(0), ' angle-from-fwd p50/p99:', np.percentile(ang,[50,99]).round(0))
