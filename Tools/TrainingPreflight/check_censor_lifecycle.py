"""Real-engine CPU check: one legal long game, one genuine conceded outcome."""
import json
from pathlib import Path
import tempfile
import numpy as np
import torch

from bench_common import save_json
from learning_rollout import EpisodeStore, LearningHost, collect_episodes


class FixtureActor:
    def act(self, host):
        actions = np.zeros(host.batch, dtype=np.int64)
        for lane in range(host.batch):
            legal = np.flatnonzero(host.mask[lane])
            kinds = host.candidates[lane, legal, :16].argmax(1)
            preferred = legal[kinds == (10 if lane == 0 else 11)]
            finish = legal[kinds == 13]
            actions[lane] = preferred[0] if len(preferred) else finish[0] if len(finish) else legal[0]
        self.obs = torch.from_numpy(host.obs.copy())
        self.candidates = torch.from_numpy(host.candidates.copy())
        self.mask = torch.from_numpy(host.mask.copy())
        packet = np.zeros((host.batch, 3), dtype=np.float32)
        packet[:, 0] = actions
        self.packet = torch.from_numpy(packet)
        return actions, packet.copy()


def main():
    torch.set_num_threads(1)
    store = EpisodeStore(4096, device='cpu')
    host = LearningHost(2, 1, seed=9026, transport='pipe', split_branches=8)
    try:
        with tempfile.TemporaryDirectory() as folder:
            result = collect_episodes(host, FixtureActor(), store, version=0,
                censor_truncated=True, seed_base=9026, debug_dir=folder)
            metrics = result.metrics
            assert result.completed and store.sealed
            assert (metrics['attempted_games'], metrics['completed_games'], metrics['censored_games']) == (2, 1, 1)
            assert metrics['censored_lanes'] == [0]
            assert set(store.episode_ids[:store.rows].tolist()) == {1}
            assert torch.all(store.returns.abs() == 1), 'No invented zero target from the cap'
            assert sum(metrics['action_kind_counts']) == store.rows
            report = json.loads(next(Path(folder).glob('*.json')).read_text())
            assert report['engine_seed'] == 9026 and report['pre_step_round'] == 400
            assert len(report['action_indices']) == 802
            metrics['replay_verified_seed'] = report['engine_seed']
            metrics['real_engine_credit_check_passed'] = True
            # Temp paths are intentionally omitted from the lasting artifact.
            metrics['censored_episodes'] = [{k:v for k,v in x.items() if k!='trace_path'} for x in metrics['censored_episodes']]
            save_json(Path(__file__).with_name('results')/'censor-lifecycle.json', metrics)
            print(json.dumps({k:metrics[k] for k in ('attempted_games','completed_games','censored_games',
                'learning_rows','censored_learning_rows','real_engine_credit_check_passed')}))
    finally:
        host.close()


if __name__ == '__main__':
    main()
