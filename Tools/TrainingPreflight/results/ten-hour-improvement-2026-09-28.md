# Ten-hour AI improvement campaign

Status: active. Started 2026-09-27 23:46:50 UTC; deadline 2026-09-28 09:46:50 UTC (11:46:50 Europe/Paris).

Objective: maximize actual playing strength, repeatedly review natural games, repair obvious tactical failures, and repeat. Model training, new search algorithms, and algorithm-only candidates are in scope. Completion requires evidence of strength and fresh-game tactical quality, not merely passing existing prepared puzzles.

## Baseline and evidence

Frozen generation 19062, hybrid four candidate groups / depth 24 / two public-information worlds / 512 finishing nodes. Baseline host and weights preserved in `/home/lva/.local/share/shards-training/2026-09-28/hybrid-statistics-2500/runtime`.

The preceding review reconstructed 14 original-cohort games (3,324 decisions). Confirmed missed Volos healing, Tetra resource collection, and Rez power. Diagnosed policy-prior ranking reversals and weak greedy rollout continuations; more depth alone did not fix them. Six earlier retraining variants on 800 games had failed to demonstrate stronger play.

## Working order

1. Correct narrowly demonstrated dominated end turns in both live choices and simulated continuations. Add mixed resource-card plans. Run natural-position regression checks, tactical/privacy checks, paired-seat strength tests, then fresh complete-game reviews.
2. Broaden continuation search rather than only its maximum length. Compare diversified continuations, useful action sequences, and a broader bounded tree. Keep a frozen baseline and use new confirmation seeds after exploratory selection.
3. Collect stronger search experience and test meaningful policy/value updates. Training targets must come from completed games or honestly labeled search advice; search actions are not on-policy PPO samples. Consider independent value calibration and a model-free heuristic comparator if the current critic remains unreliable.
4. Audit fresh games after every meaningful candidate: premature end turns, paid-effect waste, hero/relic/destiny sequencing, mastery thresholds, champion damage, known-card handling, missed wins, and redundant menus. Add confirmed failures to regression coverage and continue.
5. Promote only supported improvements; verify headless/native compatibility, information boundaries, and final natural-game behavior. Report unresolved limitations rather than claim perfect play.

Eight physical CPU cores maximum; headless simulation and GPU inference; no Unity tests. The existing 2,500-game balance cohort stays isolated from improvement experiments.

## First candidate

Opt-in `TacticalGuards` excludes End Turn when a narrowly recognized free gain remains (neutral starter resource effects, free pure-gain exhaust effects, or Volos healing). It does not force HP-cost draws or arbitrary custom effects. `MixedResourcePlans` adds an explicit sequence collecting gems from different neutral starter definitions. Both settings remain disabled in the shipped/default profile while tested.

The three confirmed natural-game end-turn regressions are corrected across current-depth, depth-64, no-prior, and eight-world diagnostic profiles. Bleak Communion remains optional. The 320-game swapped-seat pilot finished **158–162 (49.375%)**, paired-seed bootstrap 95% interval **45.625–53.125%**. No strength gain demonstrated; no deployment.

## Diversified continuation candidate

Three continuation styles compare the learned policy, resource collection, and draw/mastery preferences. A conservative effect reader also recognizes unconditional gains accompanied by nonnegative conditional bonuses, without executing opaque predicates. Tactical/privacy battery: **1,024/1,024** after a successful build. The earlier `styles3-tactics` artifact is explicitly excluded because its runtime preceded the final reader edit; `styles3-tactics-verified` is the valid result.

The same 320 paired-seat pilot finished **163–157 (50.9375%)**, paired-seed bootstrap 95% interval **46.5625–55.3125%**. Runtime 241.84 seconds, 1.323 games/s, versus 187.26 seconds / 1.709 games/s for the first candidate. These are exploratory tests on common seeds, not independent confirmation. The bootstrap groups rows by seed and verifies both treatment seats; rows are emitted in completion order and must not be paired by array adjacency.

Twenty diagnostic games on seeds outside the strength pilot, covering all 20 ordered distinct hero pairs, produced **5,940 actions and 399 end turns**. All complete transcripts and 29 alternative action paths across 16 public-information worlds were reconstructed with source-state preservation. Further avoidable omissions remain: Advanced Medicine +4 HP, active Datic Secrets +1 mastery/+1 gem, Thornshell Warden +2 power, and useful resource/card-play opportunities. Many inactive conditional abilities were correctly left unused. See `styles3-fresh-review/evidence.json` and the separate natural-game review report.

Seven suspect positions were re-evaluated with current settings, depth 64, no prior, and eight worlds. Deeper greedy continuation did not fix any of these positions. Removing the prior corrected Thornshell Warden only. Several search overrides reject actions strongly favored by the policy, so teaching from the current search uncritically would copy errors.

Terminal-outcome diagnostics completed 1,152 continuations / 148,458 transitions across those seven positions, with no censorship. Several favorable positions produce 64/64 wins under every action; a fixed weak continuation also underrates Datic Secrets because its later choices change. A fourth style that resolves the candidate action then ends corrects four of the seven natural-position choices. It passes 1,024/1,024 tactical/privacy checks. Its 320-game pilot finished **158–162**, paired bootstrap interval **45.0–54.0625%**, 256.85 seconds. No demonstrated strength gain and no promotion. Details: [natural-game review](natural-game-review-2026-09-28.md).

## Visible conditional gains and broader strategic audit

The effect reader now recognizes explicitly reviewed controller-visible conditions and counters through `If.Visible` / `PerCount.Visible`. All 46 direct content predicates/counters, plus the standard Inspire/Echo/Character/FullHealth helpers, were reviewed before annotation. They inspect visible resources, public piles/boards and played-card counts, or opponent mastery. Unknown delegates are never evaluated by the reader. Existing constructors and engine resolution remain unchanged. Non-champion pure-gain card plays are also covered. HP-cost effects, paid activations, draws and opaque custom effects remain outside the mandatory end-turn guard.

A focused natural-gain audit passes **21/21** checks, including both-seat hidden-zone swap invariance, source-state preservation, opaque predicate/counter rejection, and a corrected nested mastery-threshold calculation. The first red run contained nine genuine failures plus two Datic fixture failures caused by missing faction counters; the fixture was corrected before the passing run. The full engine suite passes **258/258**, and the GPU tactical/privacy battery passes **1,024/1,024**.

The next complete-game review contains **20 games / 6,086 actions**. All transcripts and **30 counterfactual paths × 16 worlds** were verified. Previous free healing, active destiny gains, and Thornshell power omissions did not recur. Almost all remaining flags are inactive conditions or legitimate costs. Order Initiate at game 11 / step 200 can gain two gems by declining its optional removal, but that leaves **only End Turn and Concede legal**; the gems are unusable, so leaving it unplayed is not established as a mistake. Star Seeker at game 8 / step 324 remains worth investigating: two legal warps produce **1 → 9 power** in all 16 worlds, while the planner's continuation warps Aetherbreaker then Cloud Oracles and evaluates its result below ending. The second refill is uncertain, so the gain alone is not proof of superior strategy. A safe first warp followed by declining the second, and deeper branching through visible effect menus, are next candidates.

A broader terminal-rollout screen examined **40 relic/destiny positions**, **14,560 continuations / 3,952,840 transitions**, with 32 paired worlds per action. Five destiny alternatives appeared at least 12.5 points better. Independent confirmation on **128 new worlds per action** largely erased the apparent advantages:

| Position | Current choice → screened challenger | Confirmation wins / 128 |
|---|---|---|
| Tetra, game 16 / step 50 | Soul Syphon → Deadly Recruits | 112 → 107 |
| Ko, game 0 / step 65 | Datic Secrets → Unconditional Conscription | 14 → 25 |
| Ko, game 14 / step 51 | The Last City → Paradigm Shift | 104 → 104 |
| Ko, game 1 / step 66 | Advanced Medicine → Project Yggdrasil | 54 → 44 |
| Volos, game 13 / step 66 | Synthesis → Datic Secrets | 100 → 107 |

These are outcomes of a fixed cheap continuation policy, not optimal-value estimates. The strongest remaining lead is Ko's Unconditional Conscription; its nominal paired bootstrap interval for improvement is about 1.5–16.4 points, without a multiple-comparison adjustment. The other four intervals cross zero. This argues against training on the raw winners of 32-sample searches. Whole-game strength tests remain the promotion gate.

The visible-gain candidate's 320-game paired pilot finished **157–163 (49.06%; paired bootstrap 95% interval 44.375–53.75%)**, with no demonstrated strength improvement. The driver now rejects a local DLL older than its compiled C# sources and archives all compiled source paths/hashes, including engine/content metadata, to prevent stale-build evidence. A prior strategic diagnostic archive had one later, unused audit-only source edit restored to the exact built version; the correction is documented in that run's `source-archive-note.txt`. Executed diagnostic/AI/engine/content code was unaffected.

Campaign state and large artifacts: `/home/lva/.local/share/shards-training/2026-09-28/ten-hour-improvement`.

## Menu planning and the next natural-game defects

Explicit activation-plus-menu plans pass six focused cache/privacy cases and 1,024 tactical checks, but finish **157–163 / 320** against the baseline (paired bootstrap 44.69–53.44%). Another 20-game review (6,175 actions) exposes a more general continuation asymmetry: Rez activates Star Seeker at mastery 18 before playing an already-held Slipstream Shard. Reversing that order preserves a second warp in all 16 sampled worlds. The search considers Slipstream, but its continuation declines both warps; increasing depth, removing the prior, and increasing worlds do not fix the choice. Primus Pilus's active free draw is also left unused in one checked end turn.

This pass fixes two other demonstrated defects: a stale card index omitted newly revealed destinies from live tactical lookup, and the experimental guarded planner could spend a gem on Focus at the mastery cap. Both-seat index regressions reproduce the former; 260 engine tests pass after repair. The latter now chooses Infinity Shard in all four natural-position diagnostic profiles; 23 gain/privacy checks and 1,024 final tactical/privacy checks pass. The model encoder was already using a separate authorized visible-card lookup, so this is not evidence of missing destiny identity in model inputs. Full transcripts validate 15,824 public action-target lookups after the index fix.

No model or candidate deployment; final statistics remain snapshot `39c5f2e31bab5d13`, 2,500 games. The pilot predates the last two fixes and is frozen separately. Details and artifacts: [natural-game review](natural-game-review-2026-09-28.md). Next: branch through downstream visible menus so mastery/draw setup actions are not evaluated with systematically worse follow-ups, then broaden learning experiments using trustworthy completed-game targets.


## Latest audit follow-up

The latest 20-game review confirms additional sequencing failures, including a missing Numeri setup action before a Primus purchase, and an incorrect search override of a strongly preferred Focus action. See [the latest game audit](latest-game-audit-2026-09-28.md) for 49 counterfactual paths, source-preserving replay evidence, and the distinction between confirmed errors and false alarms. No candidate has been promoted. The separate nested-menu depth-2 pilot finished 165–155/320; its paired confidence interval includes 50%.
