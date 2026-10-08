# Turn-one planning and GPU simulation — 27 September 2026

The AI now searches from the opening turn. The production controller uses the same planner as the headless GPU test host. The frozen generation-19062 network weights are unchanged; this is an inference improvement, not another training run.

No Unity editor, player, or Unity test runner was used for these tests. All strength matches use the actual headless C# rules and batched RTX 5090 policy/value inference. Small native-inference checks verify export parity and the game-facing controller; they are not throughput benchmarks.

## Accepted behavior

For every non-forced gameplay decision, consider the eight highest-probability actions plus the original sampled fallback if it is outside that set. Simulate each in two possible hidden states constructed from public information. Follow the frozen policy's greedy continuation for up to eight wrapper decisions, stopping when the turn changes. Terminal wins/losses receive exact outcomes; unfinished positions use the frozen value head, with its sign corrected to the root player. A 0.04 value margin and a 0.015 log-policy prior limit changes based on small or unlikely critic preferences.

This includes early purchases, resource use, hero activations and card ordering; it is not gated on mastery 25 or proximity to lethal. Hero drafting retains the existing matchup-based draft policy. Forced single-option decisions need no branch search.

The exact finishing planner remains a second layer near a possible finish. Its default budget is 512 expansions for each narrow/wide attempt, with a larger Scry/reorder attempt, depth 12, and a single concrete winning sequence checked in four public worlds. A winning rollout line can be checked directly in four worlds, avoiding redundant tree search. Beam pruning preserves distinct first moves when possible: setup moves should not disappear solely because an alternative produces power sooner. Successful continuations are revalidated.

Settings are exposed through `PolicySearchSettings` and the headless CLI. The inference-profile JSON records provenance; it is not a runtime configuration loader. The game evaluates the network natively on background workers and retains the existing presentation delay. The game does not start Python, a GPU server, or a console process.

## Strength and tactical evidence

- **Independent final comparison versus the previous finish-only lookahead:** 109 wins, 51 losses in 160 games (**68.125%**). Eighty matched seed pairs, treatment in both seats, all 20 ordered distinct-hero pairings equally represented. Paired bootstrap 95% interval **61.25–74.375%**; 34 improved pairs, 5 worse pairs, 41 neutral. Two-sided paired sign-test p = 0.00000243.
- Earlier candidate versus the unchanged sampled network: **282–118 (70.5%)** in 400 games. This is development evidence for the broader approach, not another independent test of the final pruning change.
- Earlier candidate versus the previous lookahead: **116–44 (72.5%)** in a separate 160-game development run. Do not combine it with the final confirmation as though the implementations were identical.
- Greedy-only control versus sampled network: **564–436 (56.4%)** in 1,000 games. Greedy selection also helps, but did not reproduce the development search score. A final direct search-versus-greedy tournament was not run.
- Final regression suite: **4,602 / 4,608 (99.87%)**, all **512 greedy attempts succeed**, and all **32 scenario families** pass the pre-existing 95% gate separately for greedy and sampled play. Tests cover five heroes, 16 variants per family, extra cards, shuffled hands, and eight sampled attempts plus one greedy attempt per position. Six sampled Rez Star Seeker continuations still fail. These scenarios were used during development; they are regression evidence, not an untouched generalization set.
- Two complete synchronous/background-controller parity games match across **622 decisions**, including stale-result rejection. The independently compiled deployable DLL additionally passed **64 decisions** against the existing game dependency DLLs, including **61 opening search preparations**.
- **258 engine tests pass**. Branch tests cover source preservation, hidden-state perturbation invariance and actual transition parity. GPU/native prediction errors in the final strength cohort stayed below 0.000002 for probabilities and 0.0000005 for values, across 129 parity checks including new hero/decision contexts.

Strength cohorts compare different agents. Their hero win rates must not replace self-play balance observations. The previous 50,000-game statistics window is unchanged.

## Speed and rejected optimizations

The old slow benchmark used individual C# CPU network evaluations. This host instead batches both policy and value inference using CUDA graphs, fixed legal padding, pinned staging and direct shared-memory encoding. Full contiguous GPU observation/candidate/mask tensors are required by the fused logits kernel. An early strided-input prototype was discarded; native/GPU parity and legal-probability checks were added before accepted runs.

A short pilot sweep on development seeds found 44/80 wins at depth 2, 54/80 at depth 4, and 61/80 with eight candidates and depth 8. A smaller terminal budget of 128 missed 23 of 512 tactical checks and was rejected. Plain policy rollouts missed World Piercer and Volos winning lines, which is why exact finishing search remains. Increasing rollout depth alone did not fix the Rez pruning failure.

A compiled-field copy prototype matched transitions but was not reliably faster than the reference graph copy; it is retained only as an isolated comparison tool. Production and final headless matches use the reference copier. Direct shared-memory encoding was retained. Server-GC and worker-count results are recorded separately in the throughput artifacts below.

Isolated measurements after the other tests stopped:

| Mode | Games | Batch / workers | Seconds | Games/second |
|---|---:|---:|---:|---:|
| Network only, workstation GC | 5,000 | 250 / 8 | 15.048 | 332.27 |
| Network only, server GC | 5,000 | 250 / 8 | 13.821 | **361.78** |
| Search both seats, workstation GC | 40 | 40 / 8 | 100.885 | 0.396 |
| Search both seats, server GC | 40 | 40 / 8 | 68.796 | 0.581 |
| Search both seats, server GC | 40 | 40 / 16 | **63.255** | **0.632** |

GC/worker changes preserve every game's winner, round and decision count. The driver now defaults to server GC, eight workers for network-only modes and sixteen for search modes. The best tested search setting reduces wall time by 37.3% relative to the workstation-GC run. Each configuration has one timing cohort; this is not proof of a global performance optimum.

The fast no-search baseline is restored. Full turn planning remains expensive: in the 16-worker run, exact finishing search and state copying dominate over GPU inference. A 50,000-game run at the measured both-player rate would take roughly **22 hours**, before full balance-telemetry overhead; this is an extrapolation from only 40 games, not a measured large-run duration. The different batch/cohort sizes also prevent treating the network/search throughput ratio as a controlled per-action slowdown factor.

Timing cohorts omit full card-acquisition balance telemetry and CUDA initialization; initialization is recorded separately. The both-player throughput probe intentionally repeats each seed twice for determinism checks, so it is not a 40-independent-game balance cohort. Do not use the concurrent strength-test wall times as isolated hardware throughput measurements.

## Deployment and limitations

The AI component was built directly as a .NET Standard 2.1 DLL against the exact installed Core/Engine/Content dependencies, preserving the existing four-argument constructor ABI. Installation at `E:\Bureau\pascension-windows-v1.0.4` verifies dependency hashes, assembly identity and the new DLL hash; the old DLL is backed up under `ai-backups`. No running game was closed. See `installation.json` for the exact backup path and hash. The Unity UI itself was not exercised. Source changelog and Resources metadata will be incorporated by the next full game build; the component update includes a companion inference-profile JSON.

The planner remains approximate: two rollout worlds, four-world checks for concrete winning lines, a limited candidate set, greedy continuations, an imperfect value head, and a same-turn horizon. It does not establish perfect play, solve every delayed combination, or prove optimal hero balance. General expected-value choices do not carry the four-world winning-line guarantee. Unknown center composition is sampled approximately. Search uses sampled hidden states, never the real hidden order or engine RNG to predict outcomes.

## Reproduction and artifacts

See [the headless host README](../../GpuSearchHost/README.md) via the repository path `Tools/GpuSearchHost/README.md` for commands and modes. Detailed evidence is in [all-turn-gpu-search-2026-09-27](all-turn-gpu-search-2026-09-27/). The independent final opponent comparison retains frozen binaries and source snapshots at `/tmp/shards-gpu-vs-terminal-accepted`; the accepted runtime is also archived outside temporary storage in the run manifest. No tests train the network or publish new balance statistics.
