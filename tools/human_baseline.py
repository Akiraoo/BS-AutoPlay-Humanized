"""Natural variability: saber-direction difference between two real replays of the same map (same metric as sim_planner)."""
import sys, os, numpy as np
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from compare_map import prep, fat
A = prep(sys.argv[1]); B = prep(sys.argv[2])
errs = []
for hand, col in (('L', 0), ('R', 1)):
    e = sorted(tt for nid, tt in A[3] if (nid // 10) % 10 == col)
    grid = np.arange(e[0], e[-1], 0.01)
    fa, fb = fat(A[1], A[2][hand], grid), fat(B[1], B[2][hand], grid)
    errs.append(np.degrees(np.arccos(np.clip((fa * fb).sum(1), -1, 1))))
e = np.concatenate(errs)
print(f"{A[0]['songName'][:28]:28s} human vs human: p50 {np.percentile(e,50):5.1f}  p75 {np.percentile(e,75):5.1f}  p90 {np.percentile(e,90):5.1f}  >75deg {np.mean(e>75):5.1%}")
