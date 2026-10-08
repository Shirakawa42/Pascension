"""Lossless float32 transport: copy occupied table prefixes, expand on CUDA.

Table extents come from the audited encoder catalog, never a learned sparsity
guess. Ordinary dense actor inputs remain the source for owned rollout storage.
Unknown fixed fields and tables without a count column are copied completely.
"""
from __future__ import annotations

import math

import numpy as np
import torch

from model import dimensions

try:
    import triton
    import triton.language as tl
except ImportError:  # Ordinary transport works without this optional backend.
    triton = tl = None


if triton is not None:
    @triton.jit
    def _expand(Packed, Metadata, Observations, Candidates, Mask,
                Segments: tl.constexpr, ObsDim: tl.constexpr,
                Actions: tl.constexpr, ActionDim: tl.constexpr,
                Block: tl.constexpr):
        lane = tl.program_id(0)
        columns = tl.program_id(1) * Block + tl.arange(0, Block)
        packed_width = tl.load(Metadata + Segments * 3)
        retained = tl.full((Block,), False, tl.int1)
        source_column = tl.full((Block,), 0, tl.int32)
        for segment in tl.static_range(Segments):
            start = tl.load(Metadata + segment)
            length = tl.load(Metadata + Segments + segment)
            source = tl.load(Metadata + Segments * 2 + segment)
            belongs = (columns >= start) & (columns < start + length)
            retained |= belongs
            source_column = tl.where(belongs, source + columns - start, source_column)
        total: tl.constexpr = ObsDim + Actions * ActionDim + Actions
        values = tl.load(Packed + lane * packed_width + source_column,
                         retained & (columns < total), other=0.)
        tl.store(Observations + lane * ObsDim + columns, values, columns < ObsDim)
        candidate_column = columns - ObsDim
        tl.store(Candidates + lane * Actions * ActionDim + candidate_column,
                 values, (candidate_column >= 0) & (candidate_column < Actions * ActionDim))
        mask_column = columns - ObsDim - Actions * ActionDim
        tl.store(Mask + lane * Actions + mask_column,
                 values, (mask_column >= 0) & (mask_column < Actions))


def _integer(value, name, *, minimum=0):
    if type(value) is not int or value < minimum:
        raise ValueError(f"{name} must be an integer >= {minimum}")
    return value


class PackedTransport:
    """Preallocated live-lane staging with stable captured GPU expansion.

``stage`` owns the selected inputs immediately. ``upload_into`` must finish on
the caller's CUDA stream before staging is reused; the normal actor's final
stream synchronization provides that boundary. The destination tensors are
ordinary dense float32 buffers, so learning does not depend on a packed schema.
    """
    def __init__(self, catalog, batch, device, *, graph=True):
        if triton is None:
            raise RuntimeError("Packed CUDA transport requires the optional Triton backend")
        self.device = torch.device(device)
        if self.device.type != "cuda":
            raise ValueError("Packed transport requires a CUDA device")
        self.batch = _integer(batch, "batch", minimum=1)
        self.obs_dim, self.actions, self.action_dim = dimensions(catalog)
        if min(self.obs_dim, self.actions, self.action_dim) < 1:
            raise ValueError("Invalid packed observation/candidate dimensions")
        tables = catalog.get("tables", {})
        if not isinstance(tables, dict):
            raise ValueError("Observation table metadata must be a mapping")
        declarations = []
        for name, table in tables.items():
            if not isinstance(table, dict):
                raise ValueError(f"Invalid table descriptor: {name}")
            offset = _integer(table.get("offset"), f"{name} offset")
            capacity = _integer(table.get("capacity"), f"{name} capacity", minimum=1)
            stride = _integer(table.get("stride"), f"{name} stride", minimum=1)
            column = table.get("count_column")
            scale = table.get("count_scale")
            if column is not None:
                _integer(column, f"{name} count_column")
                if column >= self.obs_dim or type(scale) not in (int, float) or not math.isfinite(scale) or scale <= 0:
                    raise ValueError(f"Invalid table count metadata: {name}")
            elif scale is not None:
                raise ValueError(f"Table count_scale requires count_column: {name}")
            end = offset + capacity * stride
            if end > self.obs_dim:
                raise ValueError(f"Observation table exceeds declared dimension: {name}")
            declarations.append((offset, end, capacity, stride, column, scale, name))
        for _start, _end, _capacity, _stride, column, _scale, name in declarations:
            if column is not None and any(other_column is not None and start <= column < end
                                          for start, end, _cap, _row, other_column, _factor, _name in declarations):
                raise ValueError(f"Table count column lies in a prunable table: {name}")
        self.regions = []
        cursor = 0
        for start, end, capacity, stride, column, scale, name in sorted(declarations):
            if start < cursor:
                raise ValueError(f"Observation tables overlap: {name}")
            if start > cursor:
                self.regions.append((cursor, start - cursor, None, None, None, "fixed"))
            self.regions.append((start, end - start, capacity, stride,
                                 (column, scale) if column is not None else None, "table:" + name))
            cursor = end
        if cursor < self.obs_dim:
            self.regions.append((cursor, self.obs_dim - cursor, None, None, None, "fixed"))
        self.regions.append((self.obs_dim, self.actions * self.action_dim, None, None, None, "candidates"))
        self.regions.append((self.obs_dim + self.actions * self.action_dim, self.actions, None, None, None, "mask"))
        self.dense_width = self.obs_dim + self.actions * self.action_dim + self.actions
        self.segment_count = len(self.regions)
        self.packed_host = torch.empty(self.batch * self.dense_width, dtype=torch.float32, pin_memory=True)
        self.packed = torch.empty_like(self.packed_host, device=self.device)
        self.metadata_host = torch.zeros(self.segment_count * 3 + 1, dtype=torch.int32, pin_memory=True)
        self.metadata_host.numpy()[:self.segment_count] = [region[0] for region in self.regions]
        # The initial zero-length plan is safe even before a first stage.
        self.metadata_host[-1] = 1
        self.metadata = self.metadata_host.to(self.device)
        self.used_elements = 0
        self.retained_count = 0
        self.packed_width = 0
        self.graph_enabled = bool(graph)
        self.graph = None
        self._destinations = None
        # Compilation/self-check runs once before actor captures and RNG
        # restoration; unavailable backends keep the exact NumPy path.
        from native_packing import get_backend
        self._native_backend = get_backend()

    @property
    def telemetry(self):
        return {"dense_bytes": self.batch * self.dense_width * 4,
                "uploaded_bytes": self.used_elements * 4 + self.metadata_host.numel() * 4,
                "packed_float_bytes": self.used_elements * 4,
                "metadata_bytes": self.metadata_host.numel() * 4,
                "active_rows": self.retained_count, "bucket_rows": self.batch}

    def stage(self, host, lanes):
        backend = getattr(self, "_native_backend", None)
        if backend is not None:
            retained = backend.stage(self, host, lanes)
            if retained is not None:
                return retained
        observations, candidates, masks = [np.asarray(getattr(host, name)) for name in ("obs", "candidates", "mask")]
        if observations.ndim != 2:
            raise ValueError("Host observation must be a matrix")
        host_batch = observations.shape[0]
        expected = ((host_batch, self.obs_dim), (host_batch, self.actions, self.action_dim), (host_batch, self.actions))
        if any(value.shape != shape or value.dtype != np.dtype(np.float32)
               for value, shape in zip((observations, candidates, masks), expected)):
            raise ValueError("Host float32 input schema differs from packed transport")
        lanes = np.asarray(lanes)
        if lanes.ndim != 1 or lanes.size and lanes.dtype.kind not in "iu":
            raise ValueError("Selected lanes must be a one-dimensional integer vector")
        if lanes.size > self.batch or np.any(lanes < 0) or np.any(lanes >= host_batch):
            raise ValueError("Selected lane outside transport/host batch")
        lanes = lanes.astype(np.intp, copy=False)
        selected_masks = np.take(masks, lanes, axis=0, mode="clip")
        if not np.isin(selected_masks, (0., 1.)).all():
            raise ValueError("Action masks must contain only zero/one")
        counts = selected_masks.sum(axis=1).astype(np.int32)
        if lanes.size and (np.any(counts == 0) or not np.array_equal(
                selected_masks.astype(bool), np.arange(self.actions)[None, :] < counts[:, None])):
            raise ValueError("Packed transport requires a nonempty contiguous legal candidate prefix")
        candidate_rows = int(counts.max()) if lanes.size else 0
        counted = [(index, region[4][0], region[4][1], region[2])
                   for index, region in enumerate(self.regions) if region[4] is not None]
        table_lengths = {}
        if counted and lanes.size:
            # Validate all extents together while retaining float64 rounding and
            # the first failing region's diagnostic. Every source field remains
            # raw float32 in the packed payload.
            columns = np.asarray([item[1] for item in counted], dtype=np.intp)
            scales = np.asarray([item[2] for item in counted], dtype=np.float64)
            capacities = np.asarray([item[3] for item in counted], dtype=np.float64)
            raw = observations[lanes[:, None], columns[None, :]].astype(np.float64) * scales
            rounded = np.rint(raw)
            finite = np.isfinite(raw)
            difference = np.subtract(raw, rounded, out=np.zeros_like(raw), where=finite)
            invalid = ((~finite) | (raw < 0) | (rounded > capacities)
                       | (np.abs(difference) > np.maximum(1.e-4, np.abs(rounded) * 1.e-6)))
            invalid_columns = invalid.any(axis=0)
            if invalid_columns.any():
                region_index = counted[int(np.flatnonzero(invalid_columns)[0])][0]
                raise ValueError(f"Invalid encoded table extent: {self.regions[region_index][5]}")
            table_lengths = {item[0]: int(value) * self.regions[item[0]][3]
                             for item, value in zip(counted, rounded.max(axis=0))}
        lengths = []
        for index, (_start, maximum, _capacity, _stride, count_meta, name) in enumerate(self.regions):
            lengths.append(candidate_rows * self.action_dim if name == "candidates"
                           else table_lengths.get(index, 0) if count_meta is not None else maximum)
        sources = np.cumsum([0, *lengths[:-1]], dtype=np.int32)
        width = sum(lengths)
        if not 0 < width <= self.dense_width:
            raise RuntimeError("Packed extent exceeded preallocated schema")
        destination = self.packed_host.numpy()[:self.batch * width].reshape(self.batch, width)
        for (start, _maximum, _capacity, _stride, _count, name), length, target in zip(self.regions, lengths, sources):
            if not length:
                continue
            if name == "candidates":
                source = candidates.reshape(host_batch, -1)[:, :length]
            elif name == "mask":
                source = masks
            else:
                source = observations[:, start:start + length]
            output = destination[:, target:target + length]
            if lanes.size:
                # take requires contiguous arrays, materializing these strided
                # host/output views. Index only the selected rows before copy.
                output[:lanes.size] = source[lanes]
            output[lanes.size:].fill(0)
            if name == "mask":
                output[lanes.size:, 0] = 1
        metadata = self.metadata_host.numpy()
        metadata[self.segment_count:self.segment_count * 2] = lengths
        metadata[self.segment_count * 2:self.segment_count * 3] = sources
        metadata[-1] = width
        self.used_elements = self.batch * width
        self.packed_width = width
        self.retained_count = int(lanes.size)
        return self.retained_count

    def _expand_into(self, obs, candidates, mask):
        _expand[(self.batch, triton.cdiv(self.dense_width, 1024))](
            self.packed, self.metadata, obs, candidates, mask, self.segment_count,
            self.obs_dim, self.actions, self.action_dim, 1024)

    @torch.inference_mode()
    def upload_into(self, obs, candidates, mask):
        if self.used_elements == 0:
            raise RuntimeError("Packed input must be staged before upload")
        tensors = (obs, candidates, mask)
        expected = ((self.batch, self.obs_dim), (self.batch, self.actions, self.action_dim), (self.batch, self.actions))
        if any(value.shape != shape or value.dtype != torch.float32 or value.device != self.packed.device
               or not value.is_contiguous() for value, shape in zip(tensors, expected)):
            raise ValueError("Packed destinations must match contiguous float32 CUDA input buffers")
        addresses = tuple(value.data_ptr() for value in tensors)
        if self._destinations is not None and self._destinations != addresses:
            raise ValueError("Captured packed destinations changed addresses")
        self._destinations = addresses
        self.packed[:self.used_elements].copy_(self.packed_host[:self.used_elements], non_blocking=True)
        self.metadata.copy_(self.metadata_host, non_blocking=True)
        if not self.graph_enabled:
            self._expand_into(*tensors)
        else:
            if self.graph is None:
                stream = torch.cuda.Stream(device=self.device)
                stream.wait_stream(torch.cuda.current_stream(self.device))
                with torch.cuda.stream(stream):
                    for _ in range(3):
                        self._expand_into(*tensors)
                torch.cuda.current_stream(self.device).wait_stream(stream)
                torch.cuda.synchronize(self.device)
                self.graph = torch.cuda.CUDAGraph()
                with torch.cuda.graph(self.graph):
                    self._expand_into(*tensors)
            self.graph.replay()
