# Conditional activation repair and reply-horizon experiment

The confirmed Unconditional Conscription ordering error is fixed in the experimental search. A separate two-turn rollout improves several recorded tactical choices, but its completed strength pilot does not support promotion. No installed model or published balance cohort has changed.

## Conditional activation

The prior no-effect filter could prove mastery-gated inactivity, but intentionally did not execute arbitrary `If` predicates. Some predicates can inspect the exhausted flag that the real engine changes before resolving an ability; evaluating those on the unexhausted source would be unsound.

`If.VisibleStableOnExhaust` now marks explicitly reviewed, side-effect-free predicates whose answers cannot change merely because the source is exhausted. Sixteen Horizon destiny conditions and six Duel replacements receive this annotation. They inspect public played-card counts, faction counts, controlled champions or health. Ordinary `If.Visible` and opaque predicates remain ineligible for this proof. No card name is hardcoded in the planner, no rule changes, and inactive actions remain legal in the engine.

With `PruneNoEffectPlans` enabled, search defers certified inactive activations and allows them again when their condition is met. Existing terminal-prefix simplification applies the same proof and revalidates any shortened winning line.

Validation:

- The new minimal reproduction failed four cases before the change: both seats could spend Conscription with zero or one qualifying ally.
- **88 focused checks pass**, including an actual two-ally sequence, all 22 registered annotated conditions against engine resolution in both seats, source immutability, opaque/public callback protection, incomplete Ko-menu coverage and terminal-prefix simplification.
- Original and semantic models each pass **512 tactical cases**.
- At natural position **2 / 230**, default search now plays Infinity Shard, kills the monster, takes and plays World Piercer, then activates Conscription. It ends with **4 power instead of 0**.
- At **2 / 393**, the actual continuation now ends with **37 power instead of 33**, again delaying Conscription until useful.
- Replaying the 20-seed cohort produces **6,015 actions / 404 end-turn decisions**, with **zero canceled Ko activations and zero certified inactive destiny activations**.

The 160-game paired pilot scores **78–82 (48.75%)**, paired 95% interval **43.75–53.75%**, p = 0.8036. This comparison enables the combined no-effect cleanup versus disabling it; it is not a condition-only ablation. It is inconclusive. The replay demonstrates repaired ordering, not a proven overall strength gain.

## Reply horizon

`HorizonTurns` is an experimental setting with default 1 and optional value 2 (`--horizon-turns`, with a separate reference setting). The ordinary mode evaluates at the next turn boundary. The new mode continues through the following turn using the policy for that actor, then evaluates the resulting state. Each simulated actor receives only its own observation.

Depth remains a bounded decision budget, renewed at a turn-start event; the existing extra end-resolution budget settles damage and cleanup. A branch can still hit its depth limit before the requested boundary. This is a fixed-policy opponent continuation, not exhaustive or adversarial reply search. Predicted wins occurring after a turn boundary cannot acquire the priority or cached execution plan reserved for validated immediate winning lines. Resource-order repairs inspect only the original turn's prefix.

The minimal reply test initially failed four cases and now passes **eight cases**: both seats and forced-action batching on/off, checking that the reply mode sees an opponent's lethal turn while the one-turn mode stops beforehand. Source states remain unchanged. Hybrid invariants, end-resolution and nested-menu audits also pass.

The default refactor reproduces **every exact action key and terminal record across all 20 games / 6,015 actions** from the frozen pre-refactor host. Both original and semantic models pass their **512-case tactical suites** in reply mode.

Recorded-position results with the default reply profile:

| Position | One-turn choice | Reply-horizon choice |
|---|---|---|
| 19 / 203, Rez | Longshot before Scry | Crystals, Scry, then Longshot |
| 15 / 157, Decima | End with Mining Drones in hand | Play Mining Drones |
| 2 / 294, Decima | End with Arach Devotees in hand | Play Arach Devotees |
| 17 / 138 and 186, Decima | Delay relic | Recruit a relic |
| 17 / 314, Tetra | Leave Gatekeeper unused at mastery 24 | Activate Gatekeeper |

These changes are promising but budget-sensitive: some alternative settings revert Arach or Gatekeeper, and some leaves stop within an unfinished opponent turn. Match results must decide whether the additional work is worthwhile.

## Fresh review and completed strength test

The reply profile's fresh 20 games contain **6,321 actions / 429 end-turn decisions**, zero canceled Ko activations and zero certified inactive destiny activations. All four Volos modes occur. This cohort contains no Rez–Longshot play, so the recorded Scry regression remains necessary.

New questionable decisions include Volos leaving Entropic Talons unplayed at mastery 21 and 15 HP (16 / 340), Rez leaving Ferrata Guard in hand (19 / 299), Tetra skipping Legion Carrier (16 / 319), and relic delays by Ko (14 / 144) and Volos (13 / 106). The follow-up diagnosis retains all five questionable choices at the default budget. Depth 64 changes Ko relic acquisition and Talons; eight worlds changes Legion Carrier and Talons. Paired terminal continuations give Talons **0/32 wins either way**, and Ferrata **27/32 when played versus 28/32 when ending**. These samples do not establish win-losing mistakes, and are not proof the omissions are optimal.

The recorder now distinguishes certified inactive actions from meaningful unused options. The latest main-source screen omits certified inactive end alternatives when that metadata is present; old transcripts remain supported. This is diagnostic filtering, not action-space filtering.

The 160-game paired pilot completed **69–91 (43.125%)**, paired bootstrap 95% interval **35.625–50.625%**, paired sign p = **0.1081**. There were 14 improved pairs, 25 worse pairs and 41 neutral pairs. This is not conclusive evidence of inferiority, but offers no support for adopting the slower mode. **Horizon 2 remains experimental; Horizon 1 remains preferred.** The follow-up game audit completed at 06:56 UTC and resumed the expert-experience collector. Both supervisors serialize GPU work, verify process ownership and resume the collector in `finally`. All jobs use the headless engine and the existing eight-core affinity. Runtime/source snapshots are frozen per experiment; the reply validation host predates the final extra inactive-action metadata in the recorder.

Artifacts are under `/home/lva/.local/share/shards-training/2026-09-28/ten-hour-improvement`: `conditional-*`, `reply-*`, plus `run_reply_horizon_validation.py` and `run_reply_game_audit.py`. The goal remains active; these are intermediate improvements and experiments, not a claim of optimal play.
