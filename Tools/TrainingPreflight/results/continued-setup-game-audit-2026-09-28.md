# Fresh-game audit after setup continuation changes

Experimental planner only. Installed AI and published 2,500-game statistics are unchanged. No overall strength improvement has been established.

## Evidence

Twenty fresh games cover every ordered distinct-hero pairing, using seeds `9073000000000000000 + index`. The cohort contains **5,564 actions and 392 end turns**. Exact replay reproduces every selected action, winner, round count and action count, and verifies 13,759 public card-target lookups. Forty-four unused play/exhaust alternatives across 20 end-turn positions and 17 further manual alternatives were tested in 16 sampled public worlds each. Live replay states remain unchanged by counterfactuals.

All runs use the headless engine, frozen generation-19062 policy and at most eight CPU cores. No Unity tests. Diagnostic samples are not balance estimates or strength matches.

## Confirmed sequencing loss

**Game 16, round 4, step 64, Tetra:** mastery 4, Anomaly Cleric and Shard Reactor already in hand. The AI plays Reactor before Cleric. Actual engine replay gives:

| Order | Resulting mastery | Crystals |
|---|---:|---:|
| Reactor → Cleric | 5 | 5 |
| Cleric → Reactor | 5 | 6 |

The same HP, power and remaining hand result in all 16 public worlds. No draw or hidden information is needed. The known setup improvement is real, although different played-card ordering is not claimed to produce an identical later seeded shuffle.

The current order-repair implementation accepts Focus and transparent mastery exhausts; it does not accept this mastery-granting card play. Search considers setup alternatives but its approximate continuation score still chooses the inferior resource order. Depth 64 and removal of the prior do not change the first action; eight worlds select Crystal first, which alone does not prove the complete sequence is repaired.

Games 12/74 and 17/91 also admit Crystal → Focus → Reactor for one more crystal than Reactor → Crystal → Focus. The AI eventually Focuses in both turns. Intervening non-resource actions prevent the narrow funded-Focus repair from applying. These are verified local improvements; their full intervening action sequences have not been shown state-equivalent.

## Suspicious Ko banishes and a planning inconsistency

There are **59 Ko previews: 30 banishes and 29 cancellations**. A cancellation pays no HP. Two Infinity Shard banishes occur despite discard Crystals being available:

- Game 14/168, mastery 6, HP 26: the root search caches Infinity Shard as the hero target. Policy probability for that target is float-rounded zero; the five discard Crystals carry essentially all target probability. Independently solving the actual menu at step 169 selects a Crystal under default, depth-64, no-prior and eight-world settings. The cached root plan instead executes Infinity Shard. Default root leaf value for that plan is -0.8122 versus about -0.8297 for relevant alternatives: a small approximate-value difference drives the override. Eight worlds changes the root action; greater depth alone does not.
- Game 2/168, mastery 8, HP 50: Infinity Shard is banished while two discard Crystals are available. Actual replay confirms that either Crystal can be targeted for the same one-HP cost while retaining Infinity Shard.
- Game 2/226: the root caches banishing Reactor Drone from hand while three discard Crystals exist. Independently solving the target menu selects a Crystal in all four configurations and then plays the retained Drone. The root comparison prefers the banish by only about 0.0024 raw value over cancellation.

This is not evidence that targets are accidentally mapped to the wrong card. Keys and exact replay are consistent. Root activation plans enumerate targets, score them with the activation's probability, and use target probability only as a tie-breaker. Standalone target menus have different candidate budgets and prior regularization. Thus the two search contexts can disagree, and the committed root target bypasses menu reconsideration. The critic can override a sensible target policy. The long-term value of every individual nonstarter banish is not proven by this local audit.

Other targets include Shard Reactor, Thornshell Warden, Cloud Oracles and Arach Devotees. Some occur with no starter target available. They remain strategic questions, not automatically classified errors.

## Premature end turns

Two screened omissions occur before a non-winning turn transition:

- **Game 10/267, Rez, mastery 22:** leaves Mainframe Abbot in hand. Playing it draws a card and reaches mastery 23 in every tested world. The policy favors playing it (~91%); default search ends instead. The value estimates are close and saturated: about 0.9726 for End versus 0.9690 for a play-then-stop continuation. Eight worlds selects Abbot; depth 64 does not. Drawing changes hidden-card access, so this is a strong tactical concern rather than a universal formal dominance claim.
- **Game 8/427, Decima, mastery 29:** leaves Legion Carrier in hand with zero crystals. Playing it and declining the champion selection yields two crystals, with unchanged HP/mastery/power, in all 16 worlds. Policy favors playing it (~93%), yet all four search profiles choose End. **Correction after inspecting `RevealTopForChampion`: revealing/milling five cards is mandatory; only taking a revealed champion is optional.** The original scalar-only comparison missed the changed deck/discard. Consequently this is not a confirmed free-action omission, and a mandatory-play guard is unjustified. The turn does not win, but future draw quality can differ materially.

The remaining unused-card cases are either actual immediately winning end turns or inactive condition-only exhausts in the tested worlds. This is not a proof that an apparent lethal succeeds against every possible hidden opponent hand.

## Behavior that is present

Volos uses every mode: **22 heals, 7 power, 7 draws and 9 mastery**. No Doom Gate hero-banish appears in these games. This establishes observed coverage, not optimal decisions.

## Changes preceding this cohort

The preceding implementation work added achievable Dominion setup recognition, a fully refunded planned Focus repair across intervening purchases, and continuation of known setup prefixes after optional menus. These fixed the earlier recorded Dominion/Infinity and Decima purchase-order positions. Validation passes 26 setup checks, 12 nested-menu checks, 40 natural-gain checks, four optional-choice checks and 512 tactical cases. A 40-game A/A control returned 20–20 with identical paired transcripts; it ran before the setup-prefix continuation change. None of these checks establishes superior match strength.

This audit demonstrates that obvious problems still remain. Next priorities are consistent root/menu evaluation and broader verified setup ordering. Draw/mill decisions need full deck-transition evidence rather than scalar-only comparisons. Simply increasing depth is insufficient in these examples.

## Artifacts and ongoing work

Artifacts are under `/home/lva/.local/share/shards-training/2026-09-28/ten-hour-improvement/`:

- `continued-setup-fresh-review/`: transcripts, screening, `manual-evidence.json`, `endturn-evidence.json`.
- `continued-fresh-diagnosis/`: eight positions, four search configurations, complete default-turn continuations and leaf values.
- `continued-setup-diagnosis/`: earlier Dominion/refund regression positions.
- `continued-setup-green.json`, `continued-setup-tactics/`, and the accompanying audit results.
- `planner-aa-control/assessment.json`.

The 4,000-game outcome collector was resumed after the brief GPU diagnosis. Its supervisor still waits for finalized collection before two bounded value-training experiments. No model deployment or statistics replacement was performed during this audit.
