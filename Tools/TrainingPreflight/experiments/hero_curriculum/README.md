# Isolated hero-curriculum candidate

This candidate implements a **75% random-hero / 25% ordinary-draft** training population without changing the game rules, observation schema, policy network or PPO objective. It is not integrated into a training host and has not performed training or real complete matches. HostV3, HostV4 and their runtimes remain unchanged.

`HeroCurriculum.PlanForSeed(seed)` maps the engine seed through a separate, versioned SplitMix64 stream with rejection sampling over 80 buckets. Each of the 20 ordered distinct `(seat0 hero, seat1 hero)` pairs owns three buckets; the remaining 20 keep natural drafting. Thus the intended population assigns 3.75% to each forced pair and 25% to natural draft. These are sampling probabilities, not an exact quota within every finite batch. The mapping reads no game state and never advances `State.Rng`.

`ApplyAtGameStart(adapter, seed)` accepts only an untouched initial Duel draft. In forced games it finds and submits the two actual advertised choices, in seat1 then seat0 order, using their original candidate slots. Normal engine validation, hero events, relic setup and turn initialization still run. Natural games remain byte-identical at the original draft request. A versioned setup record stores the seed, pair, two slots/option IDs/heroes and setup counters. Midgame/double application and malformed plans fail closed.

The independent probe project references the existing HostV3 engine DLL and links its internal adapter/encoder sources. It does not rebuild either pinned host. Build and prefix checks:

```sh
/home/lva/.dotnet/dotnet build Tools/TrainingPreflight/experiments/hero_curriculum/HeroCurriculumProbe.csproj -c Release
/home/lva/.dotnet/dotnet Tools/TrainingPreflight/experiments/hero_curriculum/bin/Release/net8.0/HeroCurriculumProbe.dll selftest
```

[Recorded prefix result](prefix-selftest.json): all 20 actual ordered pairs accepted both legal draft steps; each then replayed four legal policy-suffix steps exactly. Independent reset instances matched observation/menu bytes and setup records. Engine RNG stayed unchanged. The natural branch preserved all 4,160 observation/menu/mask floats and zero counters. A seed-plan-only probe over 16,000 seeds produced 4,009 natural games and 549–658 assignments per forced pair, versus an expectation of 600. This is a deterministic distribution sanity check, not a strength result. Golden seed vectors pin the mapping version.

## Future HostV5 integration contract

Create an ordinary adapter, call the helper once using that adapter's engine seed, then publish the initial observation. Apply it on every reset as well. Do not infer mode from lane, worker, wall time, current policy, archive choice, or hidden state. Hold/reset behavior must remain unchanged.

The two forced choices happen **before the collector receives an observation**. They have no behavior log-probability, no policy gradient and no stored PPO rows. All later actions use the unchanged sampled policy and verified behavior likelihood. Natural games retain normal draft actions and learning. Existing terminal-credit and censoring rules still apply to all retained policy rows.

Adapter-local `WrapperSteps` and `Submissions` include the two real setup submissions, as they do for a naturally chosen draft. The wire's cumulative policy-step counters and recorded policy suffix should exclude this administrative prefix. Keep separate setup counters and record the setup plan/version in episode metadata; do not add two fabricated learner rows or silently pretend the engine never performed the draft. Capped traces must replay the same source variant and seed setup **before** replaying their stored policy-action suffix. Check mixed cohorts, held reset lanes and counter deltas before any update.

Pin the helper source and curriculum schema in a new explicit runtime identity. A runtime migration may preserve model/Adam/RNG/archive/checkpoint counters because the tensor schema and objective do not change, but its different training population must be explicit. Never load a new host behind the old strict identity. The shared 12-hour ledger remains authoritative across both branches.

Random assignments train play with every hero; they do not directly train the forced draft choices. The natural 25% preserves some draft learning, but the current draft head's almost-zero Rez/Ko Syn Wu probabilities may remain biased. A later balanced payoff matrix can motivate a separately validated draft controller or counterpick strategy. This prototype does not implement one, and a maximin first pick would require a sufficiently reliable payoff matrix and treatment of the second player's response.

## Frozen evaluation preparation

`balanced_evaluation.py` defaults to printing a plan only. Explicit `--run` is required to execute its CPU evaluator. It delegates model/snapshot validation and normal post-draft policy play to the existing CPU evaluator, forces only the initial two legal choices, labels the intervention, and has a cooperative deadline. Use an outer process-group timeout for real execution. Its current strict loader supports v3/v4 checkpoints; V5 loading/evaluation-host selection must be reviewed separately before using V5 policies.

The primary plan has **4,000 games: 20 ordered policy-hero pairs × 100 independent engine seeds × two policy seat assignments**. Every cell has a disjoint seed block. Both games within a cell/seed remain paired; they are not independent confidence samples. Overall bounds use the 2,000 paired seeds, and per-cell simultaneous bounds use a union bound over 20 cells. Caps remain unknown outcomes. No natural-draft strength claim is attached to this score.

For a small pipeline smoke, `--pairs-per-cell 3` means 120 games. Exactly 100 games cannot give every one of the 20 pairings the same integer number of seat-swapped pairs; do not label such a sample fully balanced. No real smoke has run here. Five fake-host CPU tests passed, covering all pair assignments, original action slots, post-setup normal play, paired seeds, held resets, unchanged weights, censor handling and invalid bounds. CUDA initialization was forbidden.

**Evaluation host selection matters:** a V5 training host that automatically randomizes 75% of starts cannot be used unchanged for either the forced matrix or the natural-draft retention test. Strictly validate V5 checkpoint provenance, then explicitly select a reviewed natural-setup evaluation host with matching rules, catalog and observation semantics. Record that execution choice. Otherwise a supposedly natural evaluation would silently measure the mixed setup population.

## Predeclared bounded continuation experiment

1. After independently adopting/freezing the exact HostV4 baseline, retain one episode-free V4 checkpoint `C0`. Preserve its entire model, Adam, RNG, league and counters in both continuations. Complete V5 setup/collector/replay/likelihood tests first. No training is authorized merely by this document.
2. Run one **300-second baseline** continuation and one **300-second mixed-hero** continuation from `C0`, with identical PPO configuration and the same planned seed/RNG starting state. The population intervention is the sole intended learning difference. All real training and inline work is charged to the same persistent ledger; actual charged time may differ from the nominal windows. Neither branch resets moments, learning rate, guards, archive, or budget.
3. Freeze the two final policies. Primary head-to-head: mixed policy A versus baseline B on the 4,000-game balanced matrix starting at `0x6300000000000000`, action-sampling base seed 98311. Each cell consumes its own next 100 engine seeds. Record both policy/checkpoint hashes, setup mode, rules/schema, per-cell and overall outcomes, censor rate and paired uncertainty. These evaluation games occur outside the charged training allocation.
4. Secondary retention: 4,096 **ordinary-draft** head-to-head games on disjoint paired seeds starting at `0x6400000000000000`, action-sampling seed 98391. Neither initial hero selection is forced. Report ordinary-policy score separately. A 45% mixed-policy head-to-head score is a **5-percentage-point operational regression threshold relative to 50%**, not a demonstrated 95% noninferiority margin.
5. Treat coverage as a separately verified result: actual forced/natural counts, every ordered forced pair, terminal/censor counts by mode, and normal-draft hero probabilities. Claim stronger play only if the primary evidence supports it and the retention result is acceptable. If strength is inconclusive, say so; demonstrated coverage is not demonstrated improvement. Do not repeatedly reuse these selection seeds to claim confirmation. Reserve `0x6500000000000000` for a fresh balanced confirmation and `0x6600000000000000` for a fresh natural-draft confirmation if needed.

The balanced matrix tests conditional play of these particular learned policies. It does not establish optimal hero balance, recover missing Scry/history information, or prove that unexplored heroes are weak. Continue reporting the observation ablations and uncertainty in any balance conclusion.
