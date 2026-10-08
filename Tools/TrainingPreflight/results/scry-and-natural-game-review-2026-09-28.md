# Scry planning and fresh natural-game review

The experimental AI still makes suspicious plays. A correction for one recorded Rez sequence passes direct tests but exposes a new Scry-before-Longshot regression in fresh games. Neither the installed model nor the published balance statistics have been replaced.

Artifacts: `/home/lva/.local/share/shards-training/2026-09-28/ten-hour-improvement`.

## Review coverage

Generated 20 new headless GPU-inference games, one for every ordered distinct-hero pairing, with seed bank 9086000000000000000. The experimental semantic model uses guards/mixed/menu/setup/optional planning, four rollout styles, candidates 4, depth 24, worlds 2, target confidence 2, end-resolution extension 64, complete Scry plans, and ordinary-action prior cap 6. All work is restricted to eight CPU cores; no Unity tests or training were run for this audit.

The cohort contains **5,800 actions and 399 End Turn decisions**. All 20 complete transcripts replay correctly. Tested **26 recorded End Turn alternatives in 16 public-information worlds each**. Automatic screening covers every action; manual reading focused on games 6, 8, 12 and 18 and the flagged positions, not every decision in every game. These games are diagnostic, not an unbiased strength comparison or published balance sample.

## Confirmed recorded-sequence correction

In the preceding cohort `end-resolution-fresh-review`, game 5 / step 175, Rez sees Cache Warden, The Rotten and J Chord through Scry, with Longshot in hand and seven mastery. The older planner bottoms all three. Enumerating complete Scry selections now keeps Cache Warden and The Rotten and bottoms J Chord.

However, fixing Scry alone still buys Ghostwillow Avenger before Longshot and consumes one kept reveal. Search already evaluates Longshot first higher (approximately -0.641 versus -0.778), but its nearly zero policy likelihood imposes an excessive penalty. Experimental `ActionPriorCap=6` bounds this ordinary-action penalty. The exact full-turn GPU replay now performs:

`bottom J Chord → finish Scry → Longshot → The Rotten + Cache Warden → buy Shardwood Guardian → Focus → recruit relic`

It reaches mastery 10 this turn. This verifies that sequence, not optimality of the rest of the turn or stronger overall play. Evidence: `scry-action-rez-diagnosis/games.json`.

Complete Scry planning is restricted to the actor's already-revealed private Scry menu. It does not inspect an unknown future reveal. The three-card menu has 16 ordered subsets, including immediate finish, and plans preserve actual engine-submission counts across partial UI selections. **10 Scry checks pass**, including both seats, hidden-state perturbations, source immutability, and cached choices. The ordinary-prior fixture uses persistent public mastery; **two expected failures before scoring implementation, four checks passing after**. An earlier event-log fixture did not survive simulation cleanup and was replaced; use the `*-verified.json` files. Both original and semantic model **512-case tactical suites pass**.

`ScryPlans` and `ActionPriorCap` remain opt-in, disabled by default. No installed artifact was promoted.

## New suspicious plays and counterexamples

| Position in fresh cohort | Observation | Evidence / status |
|---|---|---|
| Game 18, step 143, Rez | At mastery 6 with Longshot in hand and unused legal Scry, plays Longshot first, then Scry after the reveal. | **Confirmed experimental regression.** The network puts 99.99994% probability on Scry. With prior cap disabled the same root uses Scry; cap 6 overrides it. Depth 64 does not repair the choice; eight sampled worlds does. |
| Game 18, steps 232–237, Rez | Scry bottoms Mining Drones, retains Shadow Apostle, then fastplays Grim Tutor and tutors a Crystal before Longshot. | Suspicious sequence. Both capped and uncapped action priors choose this; all tested depth/world variants retain the Grim Tutor opening. It is not explained solely by the cap. Future draw, banish and market effects mean immediate resources alone do not prove dominance. |
| Game 6, step 132, Decima | Ends at mastery 11 with Stolen Futures still available instead of taking two destinies. | Exact replay opens a legal destiny menu with six options. This is a real missed opportunity to investigate, not an unmet-condition false alarm. Choosing and evaluating complete alternatives remains outstanding. |
| Game 12, steps 290–292, Ko Syn Wu | Buys Kiln Drone for one crystal, then pays one HP to banish that same card. | Suspicious acquisition/banish cycle, not a no-target HP payment. It refills the market and may constitute market cycling; subsequent Nature Dominance restores the HP. Net strategic value is not established. |

Exact new Rez comparisons: `scry-action-fresh-regression-cap{0,6}/games.json`, replay positions in `scry-action-fresh-review/rez-regression-positions.json`.

The step-143 cap-6 rollout estimates Longshot at about -0.602 versus Scry at -0.665 with two worlds. This small estimated advantage is enough to override the weakened prior; eight-world sampling changes the selected action. This is evidence of sensitivity to sampled hidden states and/or continuation quality, not proof that the hidden-state distribution itself is wrong. Root Scry-plan enumeration does not automatically give every internal rollout the same quality of future Scry choices.

The new cap is therefore **not accepted for deployment** on the strength of the first repaired example or the tactical suite. Next work should compare uncertainty-aware overrides, more worlds only for fragile decisions, and better evaluation of information-gathering continuations against both natural reproductions.

## False alarms and useful coverage

Five unused Soul Syphon activations and two unused Datic Secrets activations produce no benefit in exact replay: their faction conditions are unmet. Four skipped Bound for Life activations cost the actor four HP as well as hurting the opponent; unused activation alone is not an error. At one of those positions the actor has only eight HP.

Ko's selected banishes in this cohort are 32 Crystals, six Blasters and one Kiln Drone; there are also 29 canceled menus. Canceled menus do not themselves show HP payment. No Infinity or Doom Gate banish occurs in this small sample. Volos uses all four modes: heal 35, power 6, draw 9, mastery 5. These counts establish observed coverage, not good timing or optimal use.

There is no fresh-cohort nonwinning End Turn flag with a normal card left in hand, unlike the preceding cohort's Portal Monk/Pall Shades cases. Different seeds and decks prevent treating this as a controlled reduction in error rate.

## Strength evidence remains separate

The preceding combined profile, before Scry/cap changes, won an exploratory paired pilot **179–141 / 320 (55.94%)**, paired-bootstrap 95% interval **51.25–60.63%**. An independent 1,600-game confirmation is running in its frozen runtime. Its comparison shares the current engine/public-world sampler with the baseline; it is not a binary-versus-binary application test.

End-extension-only ablation was **158–162 / 320**, inconclusive. The earlier semantic-model-only comparison was **808–792 / 1,600**, also inconclusive. These results do not justify claiming the new Scry/cap configuration is stronger.
