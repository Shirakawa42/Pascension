# Latest hybrid: natural-game audit

The experimental AI still makes clear sequencing mistakes. The new visible-menu search is not sufficient to call its play reliable, and has not been promoted to the installed game.

## Evidence

Generated **20 complete games, 6,219 actions, 417 end turns**, with every ordered pair of distinct heroes. Both seats use the latest experimental menu-depth-1 planner, four continuation styles, gain-prefix preferences, tactical guards and mixed resource plans; the generation-19062 weights are unchanged. These reuse the diagnostic seeds, so they are not an independent strength test.

Screened the full sample, read the chronological sequences of games **6 and 15**, and inspected individual turns in games 0, 1, 7, 8, 12, 16 and 17. Also reviewed earlier candidate games 1, 5 and 14 for comparison. Replay verified all 6,219 actions and outcomes, including **15,823 public card-target lookups**. Tested **49 counterfactual paths × 16 public-information worlds** without changing the replay source. Position numbers below are zero-based.

## Confirmed problems in the latest games

| Position | Recorded play | Verified alternative |
|---|---|---|
| Game 1, round 4, Ko, step 65; also games 12/70, 17/84 and 6/111 | Shard Reactor at mastery 4, then Focus | Focus is already affordable. Focus → Reactor gives **one additional gem**, with the same mastery, hand and health. All four cases pass in all 16 worlds. |
| Game 0, round 7, Ko, step 150 | Infinity Shard at mastery 9, then active Datic Secrets | Datic → Infinity reaches mastery 10 first and yields **3 power instead of 2**, with the same remaining gems and cards. |
| Game 15, round 9, Rez, step 226 | Exhaust Evokatus with one champion, then deploy champions already in hand | Deploy Systema A.I., Star Seeker and Testudo Vanguard first, then exhaust Evokatus: **4 power instead of 1**, with the same deployed cards, health, gems and mastery. |
| Game 6, round 15, Decima, step 477 | Buy Primus Pilus while Numeri Drones is ready; leave Numeri unused | Numeri → buy Primus puts Primus **directly into play**. Compared with buy → Numeri, gems are identical. Primus can then exhaust and **draw two cards** immediately. This is an actual lost defensive body and draw opportunity, not just a cosmetic ordering preference. |
| Game 8, round 11, Rez, step 371 | End with Le’shai Knight and Brute still in hand, at 10 power | Play Le’shai, play Brute and decline its optional warp: **20 power**, with no HP or gem cost. The bounded lethal checker did not establish an immediate win, but ten available power was left unused. |
| Game 15, round 10, Rez, step 278 | Star Seeker at mastery 17 before ready Systema A.I. and Slipstream Shard in hand | Systema → Slipstream reaches mastery 20 before Star Seeker. Selecting the same Carnivorous Vine then permits the **second warp selection** in all 16 worlds; the recorded order does not. The new planner still loses this opportunity in a fresh position. |

These are immediate tactical improvements, not measurements of their eventual win-rate effect. Several happen in strongly favorable or unfavorable positions; neither makes the lost opportunity disappear, but it limits what a win-only diagnostic can tell us.

## Search diagnosis

Re-evaluated nine distinct suspect positions with default search, depth 64, zero prior and eight worlds. Full default continuations were also inspected; they are diagnostic argmax continuations, not claims to reproduce every original batched stochastic action.

- **Candidate omission:** at the Numeri/Primus position, the root search considers buying Primus, buying Arach Devotees, exhausting Taur and buying Cryptofist Monk. It does **not** consider Numeri's activation. Its initial candidate limit is four, and the policy assigns effectively all probability to buying Primus. More downstream depth cannot recover that missing root action.
- **Bad search override:** at game 1 / step 65, the policy assigns **93.9% to Focus** and 6.1% to Reactor. Search nevertheless chooses Reactor: its best continuation value is about −0.6800 versus −0.7167 after Focus. Eight worlds correct this example; depth 64 does not.
- **Continuation and value errors:** all four profiles retain Infinity-before-Datic and the unused Le’shai/Brute. The default critic values ending at roughly 0.97486 and Le’shai-first at 0.97353. Its tiny preference is contrary to the verified free power gain.
- **Increasing depth is not a general cure:** depth 64 retains the four Reactor mistakes, Infinity-before-Datic, the Numeri purchase, and the unused power cards. It changes the Evokatus root choice, but a changed first move alone does not prove the eventual activation timing is correct.

Next work should broaden meaningful setup-action coverage and compare coherent reordered sequences, then improve outcome evaluation. Blindly imitating these search choices would teach the model some of the mistakes above.

## False alarms and coverage

Of **31 end-turn alternative actions**, **26 have no active effect** in the reconstructed position. Examples include Strategic Mastermind below 40 HP and unmet Datic Secrets conditions. Oblivion Gatekeeper costs 3 HP for its draw in the checked position; Tetra's draw costs 3 gems. Neither is automatically a mandatory improvement. The remaining three alternatives are Numeri, Le’shai and Brute discussed above.

Banishing a Crystal from hand before playing it is not automatically wrong: the checked banish effects accept hand/discard targets, so playing it first removes it from that target set.

Ko opens and cancels **32** previews; adjacent transcript states confirm **no HP paid** in all 32. The repeated previews remain wasted decisions. Volos uses all four modes: **37 heals, 7 power, 7 draws, 8 mastery**. No bounded alternative-winning-line flag occurred; that is not exhaustive proof that no lethal was missed.

## Provenance and status

Artifact root: `/home/lva/.local/share/shards-training/2026-09-28/ten-hour-improvement`.

- `optimized-menu1-fresh-review/`: frozen runtime/source manifests, exact reviews, `chronological-review.md`, `ordering-screen.json`, position specifications, `followup-evidence.json`, cancellation validation.
- `optimized-menu1-natural-diagnosis/games.json`: 45 paths, four search profiles for analyzed positions, leaf scores and default continuations.
- The separate earlier depth-2 pilot finished **165–155 / 320 (51.56%)**, paired-seed bootstrap 95% interval **46.88–56.25%**. It does not demonstrate a strength increase. Its runtime predates the latest batching/history/prefix changes; its 783.35 seconds cannot be assigned to this latest candidate.
- Latest 20-game recording took **57.50 seconds internally**, excluding startup. It is a small diagnostic batch with passive review work, not a production throughput comparison.

No optimizer update, deployment or statistics publication occurred in this audit. The 4,000-game outcome collector was safely paused for GPU diagnostics and resumed afterward; both processes were verified running within the eight-CPU affinity. The improvement campaign remains active.
