# Live performance audit — 26 September 2026

The running campaign is optimized in several measured ways, but it is **not a demonstrated optimum and does not saturate the RTX 5090**. This audit leaves its pinned code, configuration, weights, processes and training budget unchanged. No competing GPU benchmark was launched.

## Measurements

At approximately 15:04 Europe/Paris, the latest 180 seconds of persistent resource telemetry contained 56 samples: mean GPU activity **15.2%** (9–52%), power **99.3 W** (86.2–124.4 W), temperature **39.8°C** (39–42°C). Live NVIDIA inspection reported graphics/SM clock **2977 MHz**, memory clock **13801 MHz**, a configured **460 W** ceiling, and inactive thermal/power throttling flags at inspection. These readings do not suggest a thermal bottleneck. Changing the power limit is not indicated by this evidence.

The latest process sample showed Python and the C# host each using roughly **82% of one logical CPU**, with total WSL CPU around **11.2%**. Much of the machine remains underused because successive stages wait for each other; an eight-worker setting does not imply eight continuously busy cores.

NVIDIA defines GPU utilization as time during which at least one kernel executes, rather than a measure of peak arithmetic utilization. A high reading in another engine/monitoring interval would not, by itself, establish tensor-core saturation. [NVIDIA documentation](https://docs.nvidia.com/deploy/nvidia-smi/).

The supplied latest 60 completed generations averaged **30,766 retained learning decisions/s**:

| Stage | Mean seconds/generation | Share of generation |
| --- | ---: | ---: |
| Collection, total | 2.416 | 86.4% |
| Actor service, within collection | 0.954 | 34.1% |
| Engine/host roundtrip, within collection | 0.847 | 30.3% |
| Storage, within collection | 0.427 | 15.3% |
| Behavior verification | 0.064 | 2.3% |
| PPO updates | 0.286 | 10.2% |
| Full generation | 2.797 | 100% |

These are host wall-clock intervals. Synchronization can attribute earlier queued GPU work to a later interval. Collection includes GPU inference; it is not purely CPU simulation. Removing optimizer time entirely would improve overall throughput by only about **11.4%** at this split. The primary target is collection and coordination.

## Remaining opportunities

1. **Contiguous rollout appends.** Adaptive inference already returns contiguous selected rows, yet `EpisodeStore.append` uploads an arange index, gathers four source arrays and copies six destinations with conversions. Direct owned copies can avoid redundant gathers and index transfer. Integrating appends into actor dispatch could further reduce launches. This is untested in the real trainer; eliminating the entire measured storage interval would be an unrealistic 18% upper bound, and halving it would imply about 8.3% overall gain.
2. **Fewer sequential actor waits.** Learner and archived-opponent inference currently each pack, upload, launch, download and synchronize. Separate enqueue/completion operations could combine waits while preserving frozen policy versions and sample ownership. This requires correctness and throughput checks before deployment.
3. **CPU validation/packing overhead.** A [CPU-only probe](live-validation-cpu-probe-2026-09-26.json) found substantially faster NumPy numeric checks on fixed valid inputs. It omits outer schema guards and has not established adversarial-input equivalence or an end-to-end gain. It is a candidate measurement, not a ready replacement or a reason to remove checks.
4. **Host overlap and payload reduction.** Held lanes skip engine work, but each response still publishes/copies the full approximately 4.26 MB batch. Independent host groups or active-row publication may reduce serial waits. Historical quiet frozen/exercise runs measured about 8.3% higher wrapper throughput with two overlapping groups; that result does not prove the same gain with current trained policies and real PPO.

Adaptive actors already skip much inactive neural work, so 52.5% mean lane occupancy does not imply a free 1.9× speedup. Earlier larger resident batches and CPU inference alternatives were not consistent improvements. The larger policy also lost the measured pilot matchup, so increasing arithmetic solely to raise power consumption is unsupported.

## Change criteria

Keep the current run healthy while preparing isolated candidates. A production change requires unchanged legal actions, observations, behavior probabilities, frozen opponent ownership and complete-episode credit; measured retained decisions/s on the same frozen checkpoint; and a reviewed checkpoint/source-identity transition. Do not modify pinned modules underneath a running trainer or silently weaken checkpoint identity checks. Target fresh experience and playing strength per training hour, rather than heat or an activity percentage alone.

Runtime evidence remains in `/home/lva/.local/share/shards-training/2026-09-26/resource.jsonl` and `main/metrics.jsonl`. This audit does not claim that the remaining opportunities have been implemented.
