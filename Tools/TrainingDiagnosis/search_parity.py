"""Numerical equivalence in quantities consumed by policy search."""
import numpy as np


def metrics(expected, actual, mask):
    legal = mask != 0
    if not legal.any(axis=1).all():
        raise ValueError('Parity input has no legal action')
    if not np.isfinite(expected).all() or not np.isfinite(actual).all():
        raise ValueError('Nonfinite inference output')
    a = np.where(legal, expected[:, :-1].astype(np.float64), -np.inf)
    b = np.where(legal, actual[:, :-1].astype(np.float64), -np.inf)
    ca = a-a.max(-1, keepdims=True);cb = b-b.max(-1, keepdims=True)
    pa=np.exp(ca);pa/=pa.sum(-1,keepdims=True)
    pb=np.exp(cb);pb/=pb.sum(-1,keepdims=True)
    different=a.argmax(-1)!=b.argmax(-1)
    top=np.sort(a,axis=-1)[:,-2:]
    return dict(rows=len(a),raw_logit_error=float(np.abs(a[legal]-b[legal]).max()),
        centered_logit_error=float(np.abs(ca[legal]-cb[legal]).max()),
        probability_error=float(np.abs(pa-pb).max()),total_variation=float(np.abs(pa-pb).sum(-1).max()/2),
        value_error=float(np.abs(expected[:,-1]-actual[:,-1]).max()),argmax_changes=int(different.sum()),
        changed_argmax_max_margin=float((top[:,1]-top[:,0])[different].max()) if different.any() else 0.)


def acceptable(report):
    # Search uses relative logits with prior <= 1 (normally .015), and values.
    # This bounds probability drift to .01 percentage points and prior-score
    # drift to .001, well below the normal .02 action replacement margin.
    return (report['centered_logit_error']<=.001 and report['total_variation']<=.0001
        and report['value_error']<=.00002 and report['changed_argmax_max_margin']<=.0001)
