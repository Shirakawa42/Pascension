# Winning-line cleanup

An opt-in `SimplifyWins` profile corrects the unnecessary Brute sacrifice and stops after a validated immediate win. The current installed model and published statistics are unchanged. The completed paired pilot is inconclusive for overall strength.

## Cause and implementation

The hybrid search prioritizes validated wins, but previously broke ties with critic/prior scores. It could favor a paid or long sequence over an equally validated cheaper one. A cached or rollout-derived winning line could also keep playing cards after End Turn itself already won.

With the new setting enabled:

1. Before ordinary planning or cached macro execution, a cheap public power-versus-health precondition permits testing immediate End Turn. The precondition alone never chooses an action. The complete End Turn must win in the ordinary verification worlds and the defensive world with maximum public-possible shields.
2. Among already validated winning candidates, prefer lower worst-sampled health expenditure, then shorter worst-sampled action length. Nonwinning estimates retain their previous ranking.
3. For an accepted winning line beginning with a health-cost hero activation, try deleting that activation and its immediate answers. Reconstruct the remainder on a public sample, preserve menu option identity rather than a stale ordinal, and explicitly close an extra optional hand-reveal menu if retaining the card leaves one open. Any unknown or incompatible continuation aborts the rewrite. The same complete rewritten sequence must then win in all validation worlds, including the defensive shield world, before replacing the original plan.

The third step is a bounded sequence repair, not a global ban on sacrificing particular cards. It reads the hero's declared cost and does not name Brute, Doom Gate, Infinity Shard, or any other card. It never uses the true hidden arrangement. A preserved winning line can still depend on sampled future reveals, so this is not an exhaustive mathematical forced-win proof.

`--simplify-wins` and `--reference-simplify-wins` are independently tunable, default off. Runtime configuration and checkpoint shapes remain compatible. No training labels or reward rules change.

## Reproductions and validation

- The first focused audit failed four cases: both seats wasted a paid hero activation despite an immediate win, or preferred a paid winning prefix to a free one.
- A second focused audit failed two cases for removing a real banish from a saved winning sequence.
- All **14 focused checks now pass**, including hidden shields preventing a false immediate finish and source-state preservation. Flag-off cases exercise the existing profile but do not establish full default equivalence.
- Public-hand, defensive-world, no-effect, horizon, hybrid-invariant and nested-menu audits passed after the first change. Original and semantic models each pass **512 tactical cases** on the final paid-prefix host.
- At recorded game **1 / step 252**, all four diagnostic budgets/settings now avoid opening with Ko's sacrifice. The actual default continuation is Shard Reactor → Shard Abstractor → Focus → Infinity Shard → End Turn. It wins with **34 HP**, retaining Brute, in five actions.
- At **1 / 260** and **14 / 315**, all four settings choose immediate End Turn; the actual default continuations finish in one action. Separate 16-world replays test these immediate finishes.
- The recorded **Doom Gate banish at 14 / 298 remains** under the default setting: the rewrite cannot validate the same remaining sequence without it. Eight-world search instead starts Aegis Archivist. Do not claim this case fixed or certified wrong.
- Volos's relic delay at **6 / 146** remains unchanged across settings.

The first cleanup-only profile and the final paid-prefix profile both produce **5,850 actions across the same 20 seeds**, down from 5,982, with all winners and round counts unchanged. Brute is no longer among the cohort's banished nonstarter cards; Doom Gate remains. They are separate frozen runs, and the final paid-prefix repair additionally fixes the forced original Brute position even though that position no longer arises naturally under the first cleanup change.

The final cohort has 414 end turns, 42 Ko activations, zero canceled Ko activations and zero certified inactive destiny activations. All four Volos modes appear. The 21 nonwinning end alternatives remain principally paid Ko activations or effects without a valid target; the relic delay remains open. The full 20 transcripts and **79 alternative paths × 16 public worlds** replay with the strict public-hand truth checks intact. Many newly visible unused options are simply cards retained when ending a won game.

## Running work

`run_winning_paid_validation.py` completed its **160-game paired-seat comparison** at 07:29:36 UTC, same weights and profile except `SimplifyWins`: **78–82 (48.75%)**, paired bootstrap 95% interval **44.375–53.125%**, p = **0.7744**; 5 improved, 7 worse and 68 neutral pairs. This does not demonstrate overall strength improvement or a conclusive regression. It resumed the verified expert collector, which reached 1,888 / 2,000 at the subsequent check. Specific tactical repairs do not substitute for this whole-game strength result. The six focused CPU regression modes also pass on the final paid-prefix source.

The queued `run_fresh_hybrid_values.py` checks the live ownership of the existing expert-iteration supervisor while waiting. After that pipeline finishes its actor fits and paired tests, it will fit value-head-only and semantic-value candidates on the completed 2,000 hybrid self-play games, then run tactical gates and 320 paired games per passing candidate. This tests a critic trained on the hybrid distribution rather than relying only on the older flat-policy outcome dataset. No candidate is automatically deployed.

Artifacts are under `/home/lva/.local/share/shards-training/2026-09-28/ten-hour-improvement`: `winning-*`, `winning-paid-*`, and later `fresh-hybrid-value-*`. Goal remains active; deadline 09:46:50 UTC.

## Relic-delay follow-up

The Volos delay at hand-memory cohort game 6 / step 146 does **not lose a draw opportunity**. There are six cards left in the deck; the impending cleanup draws five. Recruitment puts the relic in discard, so recruiting now cannot put it in that next hand. On round 7 / step 171, with one card still in the deck, Volos recruits Panconscious Crown before recycling. This is consistent with preserving the choice until needed, and is no longer classified as an apparent tactical error. It does not prove optimality against all opponent responses or public information effects.
