"""Learn hand kinematics from real replays:
 - position model:  hand_pos - head_pos ≈ b + A·f   (f = saber forward)
 - roll model:      controller twist around f as a smooth function of f
 - direction coverage: which pointing directions humans actually use (for wrist limits)
"""
import sys, os, glob, json
import numpy as np
sys.path.insert(0, os.path.dirname(__file__))
from bsor_stats import load, qrot


def ref_up(f):
    # parallel transport of +y from +z to f (singular only at f = -z, i.e. pointing straight back)
    z = np.array([0, 0, 1.0])
    ax = np.cross(z, f); s = np.linalg.norm(ax, axis=1, keepdims=True); c = f[:, 2:3]
    ax = ax / np.maximum(s, 1e-9)
    y = np.array([0, 1.0, 0])
    # Rodrigues rotation of y about ax by angle atan2(s,c)
    return y * c + np.cross(ax, y) * s + ax * (ax @ y)[:, None] * (1 - c)

def roll_of(q, f):
    u = qrot(q, np.array([0, 1.0, 0]))
    r = ref_up(f)
    return np.arctan2((np.cross(r, u) * f).sum(1), (r * u).sum(1))

def feats(f):
    x, y, z = f[:, 0], f[:, 1], f[:, 2]
    return np.stack([np.ones_like(x), x, y, z, x * x, y * y, z * z, x * y, y * z, x * z], 1)

