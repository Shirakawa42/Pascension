"""Owned rollout bits and bounded temporary memory for real-size CUDA inputs."""
from pathlib import Path
import sys
from types import SimpleNamespace
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import numpy as np
import torch
from train import Rollout


CATALOG = {"obs_dim": 24576, "max_actions": 64, "action_dim": 48}


@unittest.skipUnless(torch.cuda.is_available(), "Rollout ownership requires CUDA")
class RolloutCopyTests(unittest.TestCase):
    def test_prefix_and_reordered_rows_own_all_bits_without_full_input_temporaries(self):
        torch.set_num_threads(1)
        generator = np.random.default_rng(739016)
        rows = 128
        state = SimpleNamespace(obs=generator.normal(size=(rows,24576)).astype(np.float32),
            candidates=generator.normal(size=(rows,64,48)).astype(np.float32),
            mask=(np.arange(64)[None,:] <= np.arange(rows)[:,None]%64).astype(np.float32),
            actors=np.arange(rows,dtype=np.int32)%2)
        state.obs[:,0] = -0.0
        packet = np.zeros((rows,3),np.float32)
        packet[:,1:] = generator.normal(size=(rows,2))
        lane_order = generator.permutation(rows)
        inverse = np.argsort(lane_order)
        actor = SimpleNamespace(**{name:torch.as_tensor(getattr(state,name)[lane_order],device="cuda")
                                   for name in ("obs","candidates","mask")},
                                packet=torch.as_tensor(packet[lane_order],device="cuda"))
        actor.source_rows = lambda lanes: inverse[np.asarray(lanes,np.int64)]
        store = Rollout(256,CATALOG,device="cuda")
        for selection in (lane_order, lane_order[[127,63,0,12]]):
            start = store.rows
            torch.cuda.synchronize()
            allocated = torch.cuda.memory_allocated()
            torch.cuda.reset_peak_memory_stats()
            store.append(state,packet,selection,actor=actor)
            torch.cuda.synchronize()
            transient = torch.cuda.max_memory_allocated()-allocated
            self.assertLess(transient, 65536,
                            "Rollout append allocated a full gathered observation instead of writing owned storage")
            destination = slice(start,store.rows)
            for name in ("obs","candidates","packet"):
                expected = packet[selection] if name=="packet" else getattr(state,name)[selection]
                actual = getattr(store,name)[destination].cpu().numpy()
                np.testing.assert_array_equal(actual.view(np.uint32),expected.view(np.uint32))
            np.testing.assert_array_equal(store.mask[destination].cpu().numpy(),state.mask[selection].astype(bool))
            np.testing.assert_array_equal(store.lanes[destination],selection)
            np.testing.assert_array_equal(store.seats[destination],state.actors[selection])
        saved = store.obs[:store.rows].clone()
        actor.obs.fill_(8912)
        actor.packet.fill_(71)
        torch.testing.assert_close(store.obs[:store.rows],saved,rtol=0,atol=0)


if __name__ == "__main__":
    unittest.main()
