"""Semantic access to exact center-deck facts already in observation schema v2."""
import torch


def center_top_ids(obs, card_count, card_scale=192):
    """Return the first three known identities; uncertainty never becomes a card."""
    facts = obs[:, 17152:20224].reshape(-1, 384, 8)
    codes = (facts[:, :, 0] * card_scale).round().long()
    certain = ((facts[:, :, 1] == 1 / 3) & (facts[:, :, 4] > .5)
               & (facts[:, :, 5] < .5) & (facts[:, :, 6] > .5)
               & (codes > 0) & (codes <= card_count))
    ids = []
    for position in (0, 1, 2):
        matches = (certain & (facts[:, :, 2] == position / 384)
                   & (facts[:, :, 3] == position / 384))
        ids.append(torch.where(matches.sum(-1) == 1, (codes * matches).sum(-1),
                               torch.zeros_like(codes[:, 0])))
    return torch.stack(ids, -1)
