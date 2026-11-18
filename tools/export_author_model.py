"""Combine the Python training outputs into one BotPlay model.json (Core/BotPlayModel format).
usage: export_author_model.py <hand model.json> <rel_templates.json> <roll_offsets.json> <out model.json> --name N --author A --replays K
"""
import sys, json, datetime
hand, tmpl, roll, out = (json.load(open(sys.argv[1])), json.load(open(sys.argv[2])), json.load(open(sys.argv[3])), sys.argv[4])
opt = lambda k, d='': sys.argv[sys.argv.index(k) + 1] if k in sys.argv else d
def hp(h):
    m = hand[h]
    lim = [float(x) if x is not None else 150.0 for x in m['limit_deg']]
    return dict(b=m['b'], A=[v for row in m['A'] for v in row], rollCos=m['rollC'][0], rollSin=m['rollC'][1],
                limitFirstCenter=m['limit_az_centers'][0], limitStep=m['limit_az_centers'][1] - m['limit_az_centers'][0], limitDeg=lim)
dm = {'down': 0, 'up': 1, 'side': 2, 'any': 3}
tau = tmpl['tau']
groups = [dict(rel=int(g['alt']), dir=dm[g['dir']], gap0=g['gap'][0], gap1=g['gap'][1], n=g['n'], alpha=g['alpha'], beta=g['beta']) for g in tmpl['groups']]
rt = roll['tau']
data = [[[0.0] * len(rt) for _ in range(9)] for _ in range(2)]
for g in roll['groups']:
    data[0 if g['hand'] == 'L' else 1][g['dir']] = [round(o * r, 1) for o, r in zip(g['offset'], g['R'])]
model = dict(format=1,
             meta=dict(name=opt('--name', 'author'), author=opt('--author'), createdUtc=datetime.datetime.utcnow().strftime('%Y-%m-%dT%H:%M:%SZ'),
                       trainer='python-tools', replayCount=int(opt('--replays', '0')), swingCount=sum(g['n'] for g in groups if g['dir'] != 3),
                       notes=opt('--notes')),
             left=hp('L'), right=hp('R'),
             templates=dict(tauStart=tau[0], tauStep=round(tau[1] - tau[0], 4), groups=groups),
             rollOffsets=dict(tauStart=rt[0], tauStep=round(rt[1] - rt[0], 4), data=data))
json.dump(model, open(out, 'w', encoding='utf-8'), ensure_ascii=False, indent=1)
print('wrote', out, len(groups), 'template groups')
