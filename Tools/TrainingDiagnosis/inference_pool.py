"""One GPU consumer batches requests from independent CPU search streams.

Each producer blocks until its exact output slice is ready, so its shared
observation buffer cannot be overwritten during GPU work. No search budget,
random stream, observation or candidate order is changed.
"""
from concurrent.futures import Future, ThreadPoolExecutor
import queue
import time
import numpy as np


class InferencePool:
    def __init__(self, infer, coalesce_seconds=.00015):
        self.infer = infer
        self.coalesce_seconds = coalesce_seconds
        self.requests = queue.Queue()
        self.groups = self.merged_groups = self.request_count = 0

    def request(self, packet, meta):
        result = Future()
        self.requests.put((packet, result))
        return result.result()

    def collect(self, hosts, seeds, *, heartbeat=None, captures=None):
        def collect_one(i):
            return hosts[i].collect(seeds[i], self.request, retain_rows=False,
                on_root=captures[i] if captures else None)
        error = None
        with ThreadPoolExecutor(max_workers=len(hosts), thread_name_prefix='search-transport') as team:
            pending = [team.submit(collect_one, i) for i in range(len(hosts))]
            while not all(f.done() for f in pending) or not self.requests.empty():
                # Cancel every blocked producer on inference/transport failure;
                # otherwise the executor would wait forever for a GPU reply.
                if error is None:
                    error = next((f.exception() for f in pending if f.done() and f.exception()), None)
                try:
                    first = self.requests.get(timeout=.05)
                except queue.Empty:
                    if error:
                        for host in hosts:host.stop=lambda: True
                    if heartbeat and error is None:
                        try:heartbeat(0, 0)
                        except BaseException as failure:error=failure
                    continue
                group = [first]
                deadline = time.perf_counter()+self.coalesce_seconds
                while True:
                    try:
                        wait = max(0., deadline-time.perf_counter()) if len(hosts)>1 else 0.
                        group.append(self.requests.get(timeout=wait))
                    except queue.Empty:break
                try:
                    if error:raise error
                    packet = group[0][0] if len(group)==1 else np.concatenate([r[0] for r in group])
                    logits, values = self.infer(packet, None)
                    offset = 0
                    for rows, reply in group:
                        end = offset+len(rows)
                        reply.set_result((logits[offset:end], values[offset:end]))
                        offset = end
                    self.groups += 1
                    self.request_count += len(group)
                    self.merged_groups += len(group)>1
                    if heartbeat:heartbeat(0, 0)
                except BaseException as failure:
                    error = failure
                    for _, reply in group:
                        if not reply.done():reply.set_exception(error)
                    for host in hosts:host.stop=lambda: True
            if error:raise error
            return [f.result() for f in pending]

    def diagnostics(self):
        return dict(gpu_groups=self.groups, merged_groups=self.merged_groups,
                    producer_requests=self.request_count)
