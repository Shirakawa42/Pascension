"""CUDA transport bit parity across changing real-schema extents and lanes."""
import copy
from pathlib import Path
import sys
import tracemalloc
from types import SimpleNamespace
import unittest
import warnings

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))

import numpy as np
import torch
from packed_transport import PackedTransport, triton


CATALOG = {"obs_dim": 24576, "max_actions": 64, "action_dim": 48,
    "tables": {
        "public_entities": {"offset": 4864, "capacity": 384, "stride": 12, "count_column": 166, "count_scale": 384},
        "all_pending_options": {"offset": 9472, "capacity": 384, "stride": 20, "count_column": 149, "count_scale": 384},
        "known_positions": {"offset": 17152, "capacity": 384, "stride": 8, "count_column": 167, "count_scale": 384},
        "staged_selection": {"offset": 20224, "capacity": 384, "stride": 4, "count_column": 177, "count_scale": 384},
        "played_this_turn": {"offset": 21760, "capacity": 384, "stride": 4, "count_column": 168, "count_scale": 384},
        "public_continuations": {"offset": 23296, "capacity": 64, "stride": 8, "count_column": 174, "count_scale": 64},
        "public_copy_frames": {"offset": 23808, "capacity": 96, "stride": 8, "count_column": 176, "count_scale": 96}}}


def host(batch, mode=0):
    random = np.random.default_rng(891321 + batch + mode)
    observations = np.zeros((batch, 24576), np.float32)
    observations[:, :4864] = random.uniform(-100, 100, (batch, 4864)).astype(np.float32)
    for table in CATALOG["tables"].values():
        if mode == 0:
            counts = np.arange(batch) % 7
        elif mode == 1:
            counts = np.full(batch, table["capacity"])
        elif mode == 2:
            counts = np.zeros(batch, int)
        else:
            counts = random.integers(0, table["capacity"] + 1, batch)
        observations[:, table["count_column"]] = counts / table["count_scale"]
        for lane, count in enumerate(counts):
            start, width = table["offset"], int(count) * table["stride"]
            observations[lane, start:start+width] = random.uniform(-1.e4, 1.e4, width).astype(np.float32)
            if width:
                observations[lane, start] = -0.0
    candidates = np.zeros((batch, 64, 48), np.float32)
    masks = np.zeros((batch, 64), np.float32)
    legal = np.full(batch, 64) if mode == 1 else (np.arange(batch) % 19) + 1
    for lane, count in enumerate(legal):
        candidates[lane, :count] = random.uniform(-1.e4, 1.e4, (count, 48)).astype(np.float32)
        candidates[lane, 0, 5] = -0.0
        masks[lane, :count] = 1
    return SimpleNamespace(obs=observations, candidates=candidates, mask=masks)


@unittest.skipUnless(torch.cuda.is_available() and triton is not None, "CUDA/Triton transport requires a GPU")
class PackedTransportTests(unittest.TestCase):
    def setUp(self):
        torch.set_num_threads(1)

    def destinations(self, batch):
        return (torch.empty(batch, 24576, device="cuda"),
                torch.empty(batch, 64, 48, device="cuda"),
                torch.empty(batch, 64, device="cuda"))

    def assert_bits(self, expected, actual):
        np.testing.assert_array_equal(expected.view(np.uint32), actual.cpu().numpy().view(np.uint32))

    def test_graph_all_buckets_all_tables_grow_shrink_full_capacity_and_padding(self):
        for batch in (32, 64, 128):
            transport = PackedTransport(CATALOG, batch, "cuda")
            output = self.destinations(batch)
            addresses = (transport.packed_host.data_ptr(), transport.packed.data_ptr(),
                         transport.metadata_host.data_ptr(), transport.metadata.data_ptr())
            for repetition, mode in enumerate((0, 1, 2, 3, 0)):
                state = host(batch+7, mode)
                retained = batch - (repetition % 3) * 7
                lanes = np.arange(retained-1, -1, -1, dtype=np.int32)
                self.assertEqual(transport.stage(state, lanes), retained)
                transport.upload_into(*output)
                torch.cuda.synchronize()
                for value, original in zip(output, (state.obs, state.candidates, state.mask)):
                    self.assert_bits(original[lanes], value[:retained])
                self.assert_bits(np.zeros((batch-retained,24576),np.float32), output[0][retained:])
                self.assert_bits(np.zeros((batch-retained,64,48),np.float32), output[1][retained:])
                padding_mask = np.zeros((batch-retained,64),np.float32)
                padding_mask[:,0] = 1
                self.assert_bits(padding_mask,output[2][retained:])
                self.assertEqual(addresses, (transport.packed_host.data_ptr(), transport.packed.data_ptr(),
                    transport.metadata_host.data_ptr(), transport.metadata.data_ptr()))
                if mode == 0:
                    self.assertLess(transport.telemetry["uploaded_bytes"], transport.telemetry["dense_bytes"] / 3)

    def test_staging_owns_input_before_host_mutation_and_empty_selection_clears_all_outputs(self):
        transport = PackedTransport(CATALOG, 32, "cuda")
        output = self.destinations(32)
        state = host(40)
        selected = np.array([33, 7, 18],np.int64)
        owned = [value[selected].copy() for value in (state.obs,state.candidates,state.mask)]
        transport.stage(state,selected)
        state.obs.fill(123);state.candidates.fill(321);state.mask.fill(1)
        transport.upload_into(*output);torch.cuda.synchronize()
        for original,value in zip(owned,output):self.assert_bits(original,value[:3])
        transport.stage(state,[])
        transport.upload_into(*output);torch.cuda.synchronize()
        self.assert_bits(np.zeros((32,24576),np.float32),output[0])
        self.assert_bits(np.zeros((32,64,48),np.float32),output[1])
        padded=np.zeros((32,64),np.float32);padded[:,0]=1
        self.assert_bits(padded,output[2])

    def test_unknown_fixed_regions_and_tables_without_counts_are_copied_fully(self):
        catalog = {"obs_dim":16,"max_actions":4,"action_dim":3,
            "tables":{"mask":{"offset":4,"capacity":2,"stride":3}}}
        state = SimpleNamespace(obs=np.arange(32,dtype=np.float32).reshape(2,16),
            candidates=np.arange(24,dtype=np.float32).reshape(2,4,3),mask=np.ones((2,4),np.float32))
        transport=PackedTransport(catalog,2,"cuda",graph=False)
        output=(torch.empty(2,16,device="cuda"),torch.empty(2,4,3,device="cuda"),torch.empty(2,4,device="cuda"))
        transport.stage(state,[1,0]);transport.upload_into(*output);torch.cuda.synchronize()
        for original,value in zip((state.obs,state.candidates,state.mask),output):
            self.assert_bits(original[[1,0]],value)

    def test_count_column_cannot_disappear_inside_its_own_or_another_pruned_table(self):
        for column in (4864, 9472, 23808):
            catalog=copy.deepcopy(CATALOG)
            catalog["tables"]["known_positions"]["count_column"]=column
            with self.assertRaisesRegex(ValueError,"count column lies in a prunable table"):
                PackedTransport(catalog,32,"cuda")

    def test_invalid_counts_schema_masks_and_lanes_fail_visibly(self):
        transport=PackedTransport(CATALOG,32,"cuda")
        for invalid in (float("nan"),float("inf"),-1/384,-1.e-8,385/384,1.5/384):
            state=host(32);state.obs[0,166]=invalid
            with self.assertRaisesRegex(ValueError,"table extent"):
                transport.stage(state,[0])
        for lanes in ([32],[-1],[1.5],np.zeros((2,2),np.int32),np.arange(33)):
            with self.assertRaises(ValueError):transport.stage(host(32),lanes)
        state=host(32);state.mask[0]=0
        with self.assertRaisesRegex(ValueError,"candidate prefix"):transport.stage(state,[0])
        state=host(32);state.mask[0]=0;state.mask[0,[0,3]]=1
        with self.assertRaisesRegex(ValueError,"candidate prefix"):transport.stage(state,[0])
        state=host(32);state.mask[0,0]=.5
        with self.assertRaisesRegex(ValueError,"zero/one"):transport.stage(state,[0])
        state=host(32);state.obs=state.obs.astype(np.float16)
        with self.assertRaisesRegex(ValueError,"float32 input schema"):transport.stage(state,[0])

    def test_late_tail_staging_does_not_copy_unselected_host_table_rows(self):
        transport = PackedTransport(CATALOG, 32, "cuda")
        state = host(128)
        lanes = np.array([7, 3], np.int64)
        # NumPy take requires contiguous input/output. A narrow table view
        # previously materialized all 128 host rows even for these 2 survivors.
        # Bound temporary CPU data independently of preallocated pinned/GPU data.
        tracemalloc.start()
        try:
            transport.stage(state, lanes)
            peak = tracemalloc.get_traced_memory()[1]
        finally:
            tracemalloc.stop()
        self.assertLess(peak, 256 * 1024, f"Staging copied unselected host rows: {peak} bytes")
        output = self.destinations(32)
        transport.upload_into(*output)
        torch.cuda.synchronize()
        for original, value in zip((state.obs, state.candidates, state.mask), output):
            self.assert_bits(original[lanes], value[:2])

    def test_all_table_guards_reject_nonfinite_fractional_negative_and_overflow_counts(self):
        transport = PackedTransport(CATALOG, 32, "cuda")
        state = host(40)
        lanes = np.array([33, 7], np.int64)
        for name, table in CATALOG["tables"].items():
            for invalid in (float("nan"), float("inf"), -1.e-8,
                            (table["capacity"] + 1) / table["count_scale"], .25 / table["count_scale"]):
                with self.subTest(table=name, invalid=invalid):
                    previous = state.obs[7, table["count_column"]].copy()
                    state.obs[7, table["count_column"]] = invalid
                    try:
                        with warnings.catch_warnings():
                            warnings.simplefilter("error", RuntimeWarning)
                            with self.assertRaisesRegex(ValueError, "table:" + name):
                                transport.stage(state, lanes)
                    finally:
                        state.obs[7, table["count_column"]] = previous
        # Reporting follows schema region order even when a later table has NaN.
        state.obs[7, 166] = -1.e-8
        state.obs[33, 177] = float("nan")
        with self.assertRaisesRegex(ValueError, "table:public_entities"):
            transport.stage(state, lanes)

    def test_duplicate_reordered_selected_rows_preserve_all_input_bits(self):
        transport = PackedTransport(CATALOG, 32, "cuda")
        output = self.destinations(32)
        for mode in (1, 2, 3):
            state = host(40, mode)
            lanes = np.array([33, 7, 33, 18, 7], np.int64)
            transport.stage(state, lanes)
            transport.upload_into(*output)
            torch.cuda.synchronize()
            for original, value in zip((state.obs, state.candidates, state.mask), output):
                self.assert_bits(original[lanes], value[:len(lanes)])

    def test_invalid_table_metadata_and_graph_destinations_fail_visibly(self):
        for key,value in (("offset",24500),("capacity",True),("stride",0),("count_column",24576),("count_scale",0)):
            catalog=copy.deepcopy(CATALOG)
            catalog["tables"]["public_entities"][key]=value
            with self.assertRaises(ValueError):PackedTransport(catalog,32,"cuda")
        catalog=copy.deepcopy(CATALOG)
        catalog["tables"]["all_pending_options"]["offset"]=4900
        with self.assertRaisesRegex(ValueError,"overlap"):PackedTransport(catalog,32,"cuda")
        transport=PackedTransport(CATALOG,32,"cuda")
        output=self.destinations(32)
        with self.assertRaisesRegex(RuntimeError,"staged before upload"):transport.upload_into(*output)
        transport.stage(host(32),[0]);transport.upload_into(*output);torch.cuda.synchronize()
        with self.assertRaisesRegex(ValueError,"changed addresses"):transport.upload_into(*self.destinations(32))
        wrong=(output[0].half(),output[1],output[2])
        with self.assertRaisesRegex(ValueError,"float32 CUDA"):transport.upload_into(*wrong)


if __name__ == "__main__":
    unittest.main()
