# Second September 28 balance review

Completed baseline snapshot: `f0bf9a2471f2cdf1`. All 1,500 games and detailed traces validated; 600 appearances per hero, equally split between seats.

| Hero | Wins | Win rate |
|---|---:|---:|
| Decima | 317/600 | 52.8% |
| Tetra | 349/600 | 58.2% |
| Volos | 277/600 | 46.2% |
| Ko Syn Wu | 277/600 | 46.2% |
| Rez | 280/600 | 46.7% |

Seat 0: 789/1,500 = 52.6%. Current seat compensation is retained. Hero and seat differences from the previous snapshot combine the effects of all nine earlier changes plus sampling variability. They are not isolated causal estimates.

## All 15 relics

Use the owning hero’s primary relic selection, excluding bonus acquisitions from these figures. Selection is not randomized: decks, mastery timing and position influence the choice. Small samples are explicitly inconclusive.

| Hero | Relic | Main picks | Wins | Win rate | Review |
|---|---|---:|---:|---:|---|
| decima | Praetorian-01 | 267 | 126 | 47.2% | User declined base-power buff; unchanged. |
| decima | Praetorian-02 | 8 | 8 | 100.0% | Only 8 primary choices, all wins. No buff from low usage alone. |
| decima | Praetorian-03 | 266 | 163 | 61.3% | Performed well; no change. |
| tetra | Datic Robes | 13 | 5 | 38.5% | Approved: discard shield unlocks at M15 instead of M20; shield amount unchanged. |
| tetra | Multitask Brain | 253 | 136 | 53.8% | Performed well; no change. |
| tetra | Terminal Crescents | 278 | 191 | 68.7% | Approved: M20 power = mastery minus 5; retain 1 mastery gain and half-mastery lower tier. |
| volos | Entropic Talons | 510 | 235 | 46.1% | User declined extra healing; unchanged. |
| volos | Panconscious Crown | 36 | 22 | 61.1% | Performed well; no change. |
| volos | Unknown God | 7 | 4 | 57.1% | Only 7 primary choices, 3 games with a play; low usage does not establish weak effect. No buff. |
| kosynwu | Doom Gate | 134 | 41 | 30.6% | Approved: flood 25 → 35 Ingeminex, once per game; defense stays 7. |
| kosynwu | The Heart of Nothing | 347 | 202 | 58.2% | Performed well; no change. |
| kosynwu | The World Piercer | 37 | 27 | 73.0% | Performed well; no change. |
| rez | Slipstream Shard | 445 | 219 | 49.2% | Near 50% on primary choices. No buff. |
| rez | Star Seeker | 77 | 45 | 58.4% | Performed well; no change. |
| rez | Warpquartz | 25 | 7 | 28.0% | Approved: draw 1 before choosing banish targets; other effects unchanged. |

## Approved follow-up patch

- terminal_crescents_duel: Approved: M20 power = mastery minus 5; retain 1 mastery gain and half-mastery lower tier.
- power_struggle: Approved: sacrifice reward 5 → 6 power.
- doom_gate: Approved: flood 25 → 35 Ingeminex, once per game; defense stays 7.
- warpquartz_duel: Approved: draw 1 before choosing banish targets; other effects unchanged.
- datic_robes_duel: Approved: discard shield unlocks at M15 instead of M20; shield amount unchanged.

Rejected alternatives: Terminal Crescents M25 threshold; Nil Assassin +1 power; Doom Gate defense 9; Entropic Talons healing 2 before M20; Praetorian-01 base power 9. Do not apply these.

Warpquartz and Datic Robes buffs are exploratory: the sample does not prove them weak. Many acquisitions occurred shortly before a game ended. Kiln Drone’s low overall purchase score was concentrated in later acquisitions, so no generic buff was proposed from that headline score.

## Verification and next cohort

277 headless engine tests passed. Five affected effect-descriptor rows refreshed; all 26 other model arrays remain identical. Doom Gate’s audited monster count and Power Struggle’s explicit conditional-power quantity are included. No retraining or AI search changes.

Next evaluation: 1,500 fresh games, 75 per ordered distinct-hero pair, GPU inference and at most eight CPU cores. Preserve the old cohort and display only the new cohort after launch. Complete-game detail records flush after each game.

Artifacts: `/home/lva/.local/share/shards-training/2026-09-28/approved-relic-pass-snapshot-1500`.

20 smoke games completed with full verified statistics. The follow-up 1,500-game snapshot was launched; see `snapshot-command.json` and its runtime manifest for provenance.
