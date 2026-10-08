# Follow-up review of natural hybrid games

Reviewed the latest experimental SetupPlans candidate, using unchanged generation-19062 weights. This is **not** the installed release. Replayed all 20 recorded games (6,151 actions, 16,019 public card-target lookups), read the complete Ko–Decima game 1 and Rez–Volos game 10, and inspected the late Rez combo turns in game 15. Thirteen explicit alternative sequences were replayed in 16 sampled public worlds each. No Unity or real hidden allocation was used for the alternatives.

## Confirmed ordering and omission defects

| Position | Recorded play | Verified alternative |
|---|---|---|
| Game 1, step 74 | At mastery 4 and zero gems: Reactor → Crystal → Crystal → Focus | Crystal → Focus → Reactor → Crystal ends with 4 gems instead of 3, with the same mastery, power and hand size, in all 16 worlds. The network's fallback was already Crystal; search chose the inferior resource macro. |
| Game 15, step 277 | Star Seeker at mastery 18; Slipstream already in hand, played much later | Slipstream first reaches mastery 20 and draws two cards. Star then offers its second warp, verified by declining the first warp and observing that another warp decision remains. The original ordering only gets one. Both orders can obtain Slipstream's extra turn; the issue is the lost warp. |
| Game 15, step 295 | End turn with Korvus Legionnaire in hand and an empty discard | Korvus adds 3 power without a cost, in all 16 worlds. The network preferred Korvus; search chose End Turn. **Fixed in the experimental source and verified on the recorded position.** |

Korvus failed the existing free-gain safeguard because `ReturnFromDiscard` was an unrecognized effect tail. The safeguard now recognizes its cost-free movement of public own cards to hand without invoking the opaque card-definition filter or pretending to value the returned card. The effect itself and card rules are unchanged. Five new checks failed before the change; all **40 gain checks** pass after it, including both-seat hidden-allocation invariance, no filter execution and real-engine return resolution. All **128 tactical cases** pass. The recorded position now selects Korvus with default, depth-64, no-prior and eight-world settings; the default continuation ends with 7 power instead of 4.

## Banish decisions expose separate search problems

**Ko acquires Doom Gate and soon banishes it in two games:** game 1 / steps 154–156 and game 0 / steps 299–303. Each banish costs 1 HP; cancelling preserves the HP and relic. These are highly suspicious acquisition/removal decisions, though a short local resource comparison alone does not prove the optimal long-term deck choice.

The cause of the search's preference is directly reproducible. In game 1 / step 156, its best mean continuation values are approximately −0.99378 for banishing Doom Gate and −0.98191 for cancelling: **its own evaluator prefers keeping the relic**. Nevertheless, the model assigns probability 1 to banishing and a float-rounded zero to cancellation. The menu scoring term `0.015 * log(max(1e-12, prior))` penalizes cancellation by about 0.414, overwhelming the value difference of about 0.012. Removing that prior term chooses cancellation in both Doom Gate positions. Increasing depth from 24 to 64 or worlds from 2 to 8 does not fix them. This is a scoring defect, not evidence that deeper simulation is needed.

**Cancellation can also be omitted completely.** Game 13 / step 266 has five legal banish choices, including decline. With a four-candidate budget, the standalone menu search evaluates four card targets and never evaluates decline. Increasing depth or worlds cannot recover a missing root action. Removing the prior merely changes which card is banished. Optional decline needs an explicit coverage guarantee.

**Not every error is a bad network preference.** Game 12 / step 263 banishes Infinity Shard through a previously committed activation/choice plan, even though the network at the resulting menu prefers cancellation. Re-solving that menu with the default planner cancels and plays Infinity Shard. Exact replay of both alternatives followed by the two destiny activations gives 19 power rather than 17, with both reaching 50 HP; those destiny bonuses are independent of banishing. The banish does thin the deck, so this is a questionable strategic trade rather than a claim of strict whole-game dominance. Removing the prior at this menu makes it worse again: the critic slightly prefers the banish continuation. A blanket removal of all menu priors is therefore not established as a good fix.

These findings separate three mechanisms: poor action ordering from resource macros, overconfident policy priors overriding useful search evidence, and inaccurate continuation values/committed plans. More training games alone do not address the first two.

## Corrected and non-suspicious observations

- The earlier screen's `ko_cancel: 32` counted all optional banish cancellations while playing Ko. Exactly **28** followed Ko's hero activation; four followed Shadow Apostle or Cinder Scars. All 28 hero-preview cancellations preserve HP. There are **40 actual Ko hero banishes**, for 68 hero previews total in this cohort.
- Volos uses all four modes: 32 heals, 9 power, 9 draws, 6 mastery. This establishes coverage in these games, not optimal mode selection.
- Several unactivated destinies have unmet conditions. Ending without activating them is not itself a blunder.
- The model sometimes concentrates nearly all probability on one action. That alone is not proof of bad play; the Doom Gate replay specifically demonstrates where the confidence conflicts with its evaluated alternatives.

## Evidence and limits

Artifacts are under `/home/lva/.local/share/shards-training/2026-09-28/ten-hour-improvement`:

- `setup-complete-fresh-review/manual-followup-{positions,evidence}.json`: 13 alternatives × 16 public worlds, complete replay checks.
- `setup-complete-fresh-review/manual-policy-summary.json`: actual hero-preview attribution and model-distribution counts.
- `manual-banish-diagnosis/`: four positions × four search configurations, candidate lists, leaf values and default full-turn continuations.
- `korvus-return-{red,green}.json`, `korvus-return-verification/`, `korvus-return-tactics/`: failing/passing checks and the original-position verification.

These are selected diagnostic positions, not an independent strength estimate or an exhaustive error-rate estimate. No candidate has yet demonstrated a strength increase in the campaign. The installed policy and published 2,500-game statistics remain unchanged. The outcome collector resumed after isolated GPU diagnostics. Next priorities are optional-decline coverage and bounded menu scoring, the funded-Focus resource sequence, then critic training and independent paired strength validation.
