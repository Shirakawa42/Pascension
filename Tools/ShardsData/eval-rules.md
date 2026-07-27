# SoI end-of-turn evaluator — rules for review

> ## 🚨 CORRECTION 2026-07-27 — §4's "[measured]" table was played with Duel of Doom OFF
>
> §4 says its 30,000-game table *"outranks opinion where they conflict."* It does not.
> `balance-report.md:5-8` records **greedy-V2, DLC mask 7** (Relics|Shadow|Horizon), dated
> 2026-07-21 — four days before Duel was enabled in `SimConfig.AllDlc`. No hero drafts, no
> hero abilities, no rerolls, no Allegiance, and the *base* Dominion rule. The 24 positions
> in `positions/` were generated **with Duel ON**, so the document silently mixes two
> rulesets. Three of its bullets also blend three different reports with three different bots.
>
> Re-measured 2026-07-27 with **Duel ON** (mask 15), 30,000 games each, two policies:
> `balance-report-duel.md` (bench:greedy-v5) and `balance-report-duel-heuristic.md`
> (bench:heuristic). What changed:
>
> | Claim in §4 | Duel-OFF (greedy-V2) | Duel-ON greedy-V5 | Duel-ON heuristic |
> |---|---|---|---|
> | Overwhelm (Infinity Shard) wins | 7.0% | **51.1%** | **5.7%** |
> | early aggression | OR **1.67** (41.6→62.2%) | OR 1.29 (50.4→53.4%) | — |
> | total acquisitions | OR 1.65 | OR **2.67** (28.7→76.4%) | — |
> | mastery at round 8 | OR 1.32 | OR **1.84** (40.8→67.2%) | — |
> | faction concentration | OR 1.10 **inverted** | OR 1.07, **still inverted** (57.8→38.0%) | — |
> | champion share | p=0.440 n.s. | p=0.405 **n.s.** | — |
> | shields prevent | 3.2% | **0.5%** | 3.1% |
> | rounds p10/p50/p90 | 10/13/18 | 11/14/17 | 10/12/15 |
>
> **Four consequences for this document:**
>
> 1. **The ascend clock is not a side-line.** A *tuned* policy wins **half** its games through
>    M30 + Infinity Shard; the hand-written one wins 5.7% the same way under identical rules.
>    So 51% is a property of good play, not of the ruleset — which makes R6's `ascendClock`
>    co-equal with `killClock`, not a special case. The two-clock structure is **validated**.
> 2. **⚔D2 resolves to Expert D, empirically.** He called the 2–3% shield figure *"a
>    denominator artifact"* dominated by the 9999-power Shard turn. Confirmed: V5's 30k games
>    carry **175M** total incoming damage against the heuristic's **22M**, because ~15,000
>    overwhelm turns each dump ~9999 into the sum. Same rules, same shields, 0.5% vs 3.1%.
>    Price shields into the TTK denominator as expected turns of survival, never as a share
>    of damage.
> 3. **⚔D1 resolves to Expert A — but only now is the evidence admissible.** Faction
>    concentration stays *anti*-correlated with Duel ON and Allegiance present (57.8% → 38.0%,
>    steeper than before). Compute the named conditions directly; no generic concentration term.
> 4. **R4's premise is ~4× overstated.** Of the eight cards named as multipliers to
>    special-case, exactly **two** actually multiply: Fao Cu'tul (M20 ×2) and Unknown God
>    (M20 doubles all exhausts) — and Unknown God is not on the list. Axia is a flat 7;
>    Multitask Brain and Scion of Nothingness are per-count adds. Detect multipliers from the
>    effect tree, not from a hardcoded list.
>
> **§6's 13 win-probability anchors are unsourced** — they exist only in this file, the
> position renderer emits no evaluation, and the document itself calls them *"judgement, not
> sim output."* At least two are wrong: pos-020's justification claims the opponent holds a
> 22-power Terminal Crescents (the file shows them at M14, Crystals in hand, that card 7th in
> the draw pile), and pos-013's "forced win" does not reproduce (best line is 7 power into
> 8 HP). pos-007's forced loss does check out. Replace them with **playout-measured** win
> rates before using them as a gate.
>
> Everything structural below — clock race over weighted sum, ratios a summed-bag MLP cannot
> compute, board-per-turn vs deck-per-cycle, health only through the kill clock — is
> uncontradicted by code or data. Use the structure; re-derive the evidence.


> **⛔ THIS FILE IS THE REVIEW GATE. Nothing gets implemented until you have edited it.**
>
> Add, delete, reweight, or contradict anything. Where the experts disagreed I have kept
> **both** readings rather than picking one — those are marked ⚔ and are the places your
> judgement is worth the most.
>
> **What this is for.** The search is being changed to look only one turn ahead and then
> score the resulting end-of-turn position. That means this evaluator carries *all* the
> strategic judgement — if it is not better than simply trusting the greedy policy, the
> search will actively make the bot worse (measured: 300-iteration search scores 21.2%
> against instant greedy).
>
> **Where this came from.** Four independent expert agents each analysed 6 real positions
> (rounds 3–12, both seats) with complete omniscient information, decided every turn, and
> then specified what an evaluator must capture. Their position analyses are in
> `Tools/ShardsData/positions/`. Claims marked **[measured]** come instead from the 30,000-game
> balance report and outrank opinion where they conflict.

---

## 0. The big structural claim — all four experts, independently

**Do not build a weighted sum of health + mastery + deck value. Build a race between clocks.**

Every position they analysed resolved to the same question: *whose clock is shorter?* Each
player runs two clocks, and the evaluator estimates turns-to-win on each:

```
powerPT(p)    = D·Σpower(c)/N  + champion exhausts + destiny exhausts + gems→row conversion
masteryPT(p)  = D·Σmastery(c)/N + champion/destiny exhausts + 1 (Focus, if a gem is available)

killClock(p)   = health(opp) / max(0.5, powerPT(p) − mitigation(opp))
ascendClock(p) = ownsInfinityShard ? (30 − mastery)/max(0.2, masteryPT) + N/D : ∞

TTK(p)  = min(killClock(p), ascendClock(p))
edge    = TTK(opp) − TTK(me) − 0.5          // −0.5: the opponent moves next
eval    = sigmoid(k · edge)                  // k ≈ 1.1, so a clean 1-turn lead reads ~0.75
```

where, for each player:

```
N  = |deck| + |hand| + |discard| + |playZone|     // champions/destinies/relics NOT in N
D  = 5 / max(0.30, 1 − Σdraws(c)/N)               // cards actually seen per turn, cap ~16
```

⚔ **D3 — how to handle the ~40% comeback rate.** Two proposals, and they disagree:
- **A variance damper:** `eval = sigmoid(k · edge / sqrt(1 + min(TTK_me, TTK_opp)))` — *a
  one-turn clock edge is decisive when both clocks are 2 and is noise when both are 12*.
  Compresses early positions toward 0.5.
- **Or just a flatter `k`:** the fourth expert argues the comeback rate is a statement about
  **clock variance, not about health**. TTK estimates have σ ≈ 1.2–1.5 turns because a single
  card can compress a 3-turn clock to 1 (Fao Cu'tul's M20 "double your power", Terminal
  Crescents' "power equal to your full mastery", Slipstream's extra turn). Absorb it in
  **k ≈ 0.62** and leave the shape alone; flattening health instead will misprice the
  positions where a health lead genuinely *is* the game.

### ⚠ Correction to the skeleton itself (fourth expert, and I think he's right)
**The two mastery terms are not additive.** You need M30 **and** the Shard in hand
*simultaneously*. If mastery lands first you wait for the next Shard draw; if the Shard
cycles past before M30 you wait a **full further cycle**:
```
t_m       = (30 − M)/masteryPT
t_shard0  = turnsUntilShardDrawn()        // exact at a leaf — see below
cycle     = N / D
ascendClock = t_shard0 + ceil(max(0, t_m − t_shard0)/cycle) · cycle
```
**And use the KNOWN DRAW ORDER, not `N/D`.** At an end-of-turn leaf the draw pile is known:
`shard in hand → 0` · `shard at index i → ceil((i+1)/D)` · `shard in discard → drawPile/D +
(discard/2)/D`. In one position this flips "mastery clock is slow, race with damage" into
"the Shard is a live 2-turn win" — which was the truth.

### Why this matters more than it looks
This structure *derives* the economy-vs-aggression tradeoff instead of hand-tuning it, and
it is the reason the previous neural evaluator failed. One expert's diagnosis: the target
function is built from **ratios, minima and thresholds** (`5/N × Σ`, `min(killClock,
ascendClock)`, `health/damage`), and a small MLP over *summed bags of card vectors* cannot
compute `Σ/N` at all — it never sees `N` multiplicatively against the sum. It was being
asked to learn division from features that do not contain it. That fits the measurement
exactly: the net lost to having no evaluator at all (40.6% at equal budget).

---

## 1. Rules the experts agreed on unanimously

### R1. Health enters ONLY through the kill clock. Never as a linear term.
`TTK = health / netDamagePerTurn`. Health above ~2.5–3× the opponent's damage-per-turn is
nearly free; below ~1.5× it is everything. A linear-health evaluator rates "gain 10 health"
equal to "deal 10 damage", which is wrong in both directions.
[measured] ~40% comeback rate. The current baseline evaluator's largest coefficient is
linear health — all four experts call this its biggest error.

### R2. Board effects are per-turn; deck effects are per-cycle. Ratio ≈ N/5 ≈ 3–4×.
A champion that exhausts for 3 power fires **every turn** and occupies no deck slot. The
same 3 power printed on an ally fires `D/N` ≈ 0.3 times per turn. So a champion in play is
worth roughly **3–4× the same effect on a card**, and this is the single biggest thing the
current evaluator misses (it counts champion *defense* only).

### R3. Gems and power are exactly ZERO at an end-of-turn leaf. Never use them as features.
`ResetTurn()` zeroes them before the leaf. The old neural encoder fed `Gems/10` and
`Power/15` — constant-zero noise dimensions. Feed **rates**, not pools. Same for
`CharacterExhausted` and `FocusedThisTurn`.
> ⚠ Implementation note: this also means the search must capture unspent gems at the moment
> it submits END TURN if we want to punish waste, because the leaf cannot see it.

### R4. Lethal detection dominates everything. Two separate misses, both fatal.
**Miss 1 — burst is not "power in hand"; gems buy power out of the shop.**
```
rowRate = max over affordable row mercenaries of (power / cost)   // compute from the ACTUAL row
```
**Miss 2 — burst is NOT additive, because this game has multipliers.** Fao Cu'tul at M20 is
"gain 2 power, **then double your power**"; Terminal Crescents at M20 is "power equal to your
full mastery". In one position an additive estimate gives ~26 power and concludes "two turns
to kill"; simulating in order gives **~53** — one turn with 40 to spare. **A 100% error on
the most important quantity in the position.**
```
burst(p):
  g  = gems + gemExhausts(readyChampions) + gemsFromPlayableHand
  pw = power + powerFromHand + powerExhausts(ready, EXCLUDING multipliers)
  pw += g · rowRate
  for each ready multiplier, in play order: pw = applyMultiplier(pw)
```
Multipliers to special-case: **Fao Cu'tul (M20 ×2), Terminal Crescents (M20 = full mastery
as power), Praetorian-01 (8/12), Aetherbreaker (M10 → 8), Axia, Multitask Brain, Scion of
Nothingness, Infinity Shard (2/3/5/9999).**
Clamp hard: `burst(opp) ≥ effectiveHP(me) → eval ≤ 0.05` (they move first);
`burst(me) ≥ effectiveHP(opp) → eval ≈ 0.75` only, because they still get a turn.
`effectiveHP = health + Σ shields in hand`.
Two positions turn entirely on this: one is a **forced win misread as "comfortably ahead"**
because 8 gems buy 9 power out of the shop; another is a **forced loss misread as ~0.25**
because the opponent has a deterministic 5-card mastery kill in hand.

### R5. Mastery is a step function, never linear. Compute it by re-pricing the deck.
> **marginal value of +1 mastery = (deck value re-scored at M+1) − (deck value at M)**

This captures every threshold automatically *and* personalises it — M20 is enormous if you
own Unknown God or Terminal Crescents and near-zero otherwise. `ShardsValueModel.CardValue`
already buckets at 0/5/10/15/20/25/30, so this is nearly free to implement.
Then add explicitly:
- **M5** — destiny pick + hero ability come online
- **M10** — free relic recruit (an entire extra card at zero gem cost)
- **M20** — the biggest card cliff (Infinity Shard 3→5, Unknown God doubles all exhausts,
  Slipstream extra turn, World Piercer return-all, Praetorian-01 8→12)
- **Penalise being one short.** M19 ≉ M20; thresholds snapshot at play time and never
  retro-activate.
- **Unclaimed gates are stored value**: `M≥10 && !RelicRecruited` is a free card sitting on
  the table, not a neutral flag.

### R6. The Shard clock — `ascendClock` needs the draw wait, and everyone omits it.
```
ascendClock = (30 − mastery)/masteryPT + N/D
```
Reaching M30 does **not** win. You must then draw and play the Infinity Shard — exactly 1
copy in a deck of N. At M30 with 15 cards you win in ~3 turns; with 30 cards, ~6.
Consequences:
- **Deck thinning is a win-condition accelerator**, not a nicety.
- **If the Infinity Shard has been banished this line is dead — `∞`, discontinuously.**
  Several cards banish from your own discard and a bot will happily delete its own win
  condition. Check this explicitly.
- A tutor/topdeck effect (Grim Tutor, Dash at M10+, World Piercer) collapses the wait to ~1.
- Do not value the Shard at all below M30 — everyone has exactly one.
- ⚔ One expert: make the eval **steeply nonlinear in *opponent* mastery above ~22** as cheap
  insurance against exactly the forced-loss position they found.

### R7. Deck **density**, not deck size. Size is a divisor, not a feature.
`density = Σ value(c)/N`; per-turn output is `D × density`.
- Adding a card is good **iff** its value exceeds your current average. A mediocre card
  actively makes a lean deck worse. **Never reward "cards bought" as a raw count.**
- Banishing is worth `(avg − v(banished)) × D/N` **forever**, and thinning is quadratically
  more valuable in a small deck (`∂(5/N)/∂N = −5/N²`) — 4× as valuable at N=14 as at N=28.
  Priority: **Blaster > Crystal >> Shard Reactor >>> Infinity Shard (never)**.
- **A cantrip is never dilution** — if it draws ≥1 it replaces itself.
- **Fast-play and Warp are dilution-free** — the card returns to the center deck. Keep
  owned-card throughput and row-conversion throughput as separate terms.
- Card draw compounds twice: it raises `D` *and* shortens the Shard wait.

### R8. Champions: defense and exhaust output MULTIPLY, they don't add.
Three experts said "defense without Taunt is nearly worthless". The fourth sharpened it and
I find his version more useful: **defense is not a damage sponge, it is a price tag on the
opponent's option to remove your engine.** So:
```
championValue(c) = exhaustEPT(c) · survival(c)          // per TURN
survival(c)      = clamp(effDef/(effDef + 0.6·oppPowerPerTurn), 0.25, 1.0)
cardInDeckValue  = cardValue · D/N                      // ≈ 0.25× at N=20 — hence the 3-5× gap
```
A def-9 vanilla body is worth ~0; a def-2 body with a real ability is worth its ability minus
its fragility. **Do not add champion defense to effective HP.** In one position, 33 points of
champion defense (including a +2 aura across five champions) absorbed **exactly zero** damage
all game, because the opponent had no incentive to attack the board.

- **Removal is a rental**, and its value swings with the *owner's discard depth* — a detail
  no evaluator looks at: `turnsOffBoard ≈ 1 + (oppDiscard + oppDrawPile/2)/D`. Same champion,
  same defense, opposite correct answers in two of the positions purely because of it.
- **Taunt as a per-turn tax, not a wall:** `damageThroughPerTurn = max(0, oppPPT − effDef)`.
  This is a **hard wall exactly when `effDef ≥ oppPPT`** (TTK → ∞) and a rounding error when
  `effDef ≪ oppPPT` — the binary feel falls out with no special case.
  ⚠ **The wall trap:** taunt buys *turns*, and turns are only worth `myClockProgressPerTurn`.
  A wall with no clock behind it is worth nothing — an evaluator scoring defense standalone
  will happily buy one while having no way to win.
- **Rank the exhausts** (1) power/gems, (2) mastery/cards, (3) defense — and scale
  defense-aura effects by `oppPowerPerTurn` so they collapse to ~0 against a passive opponent.
- Champion **count** is convex (G-48, Primus Pilus, War Bound, Unknown God, Ferrata Guard,
  Evokatus, Axia) — but gate it on whether the payoff is *offensive*.
- Champion value must be computed **against its owner's deck**: Testudo Vanguard is nearly
  vanilla for an owner with one shield card.

### R8b. The end-of-turn split — a candidate generator, not a strict rule
"Assign exactly `defense` or 0" is *nearly* dominant, and it is a good **candidate
generator** (≤2^k subsets, k ≤ 5 → ≤32), but it is **not strictly true**:
- **Testudo Vanguard** lets champion options be over-assigned, and shields are revealed
  *after* the split is declared — so the optimum is `effDef + E[their shields]`, a decision
  under uncertainty.
- **Taunt × Testudo:** if the taunt survives post-shields, every other champion hit **and all
  face damage** resolve as zero. Under-assigning the taunt zeroes your whole turn.
- **Ingeminex:** here it *is* strict, and it's a strong prune — 9 power against defense 10 is
  worth **zero**, not 90%.
Prune with: `P ≥ lethal` → face, dump remainder into the largest free kill;
`P < lethal` → only consider killing `c` if `killValue(c) > effDef(c) · marginalFaceValue`.

### R9. Conditional cards must be checked for liveness, not counted.
`destinies.Count` is a bad feature; `Σ P(condition satisfiable given my deck) × value` is a
good one. The conditions are named and cheaply checkable:
- **Echo** — a Wraethe card in your *discard* (usually trivially true)
- **Inspire** — ≥1 champion in play
- **Unify** — another Undergrowth **ally** played this turn (champions never satisfy it)
- **Allegiance X 4** — static count over your whole card pool, **the card itself counts**
- **Duel Dominion** — ≥3 *other* cards spanning ≥3 distinct factions. **Much rarer than it
  reads**, because Crystal / Blaster / Shard Reactor / Infinity Shard are faction `None` and
  count for nothing.
Real examples from the positions: a destiny worth 0 because its owner has zero Order cards;
a heal destiny worth 0 at 50/50 health; a champion ability worth 0 because it needs 3
champions and its owner has 1.

### R10. Health cap and overkill are hard zeros.
`healValue = min(heal, 50 − health)`. Overkill is wasted: 20 power into an 8-health opponent
is worth 8. Cap gem value at what is actually spendable.

---

## 2. ⚔ Where the experts disagreed — your call

### ⚔ D1. Faction concentration
- **Expert A:** "close to noise, and where it isn't noise it points the wrong way half the
  time." Faction membership is consumed by a handful of named checkable conditions and
  nothing else; **Duel Dominion actively rewards diversity**. Drop any generic synergy term.
- **Expert B:** "Default to rewarding concentration" — Unify, Echo, Allegiance, True Leader
  reward it; only Dominion and a few others reward spread.
- **[measured] sides with A, strongly:** faction concentration is *anti*-correlated with
  winning — Q1 54.4% → Q4 46.3%, OR 1.10 **inverted**, p=0.000, n=30,000.
- **My reading:** compute the named conditions directly, no generic concentration term.

### ⚔ D2. Shields — three positions, and the third says the other two are both wrong
- **Expert A:** a shield point ≈ a health point, *forever*, because revealed shields are not
  discarded — a repeating asset with zero opportunity cost.
- **Expert B:** `min(Σshield, attacker's expected power)`, net of the hand slot it costs:
  `shield − 0.6 × deckAverage`.
- **Expert D (contradicts both):** *"The 2–3% figure is a denominator artifact."* Total game
  damage is dominated by the 9999-power Shard turn and by massive overkill turns, which
  contribute enormous damage and zero shield relevance. The right metric is **expected turns
  of survival added**. But A is also wrong, and more dangerously: **a shield only fires if
  you chose not to play the card**, so its true cost is that card's play value every turn.
  Cryptofist Monk (shield 8, cantrip) has ~zero hold cost; holding Bulwark Chanter in one
  position would have cost M10, a relic, and 6 health.
  ```
  E[shieldsInHand] = Σ_deck shield(c)·(D/N)·holdProb(c)
  netDamagePerTurn = max(0, oppPPT − min(E[shields], oppPPT·faceFraction) − tauntAbsorb)
  ```
  Price it **into the denominator, never as an additive bonus** — then when
  `prevention + taunt ≥ oppPPT`, TTK → ∞ and shields are correctly worth everything.
  Shields scale with turns remaining and inversely with opponent burst: a long-game asset,
  near-worthless once either player is near lethal.
- **My reading:** D. It explains the measured 2–3% *and* the positions where shields decided
  the line, which neither A nor B does.

### ⚔ D5. The shape of the health term
- **Experts A–C:** nearly flat above ~30, steep below ~20.
- **Expert D:** that gets the right answer for the wrong reason. The nonlinearity is not in
  absolute health, it is in `danger = oppBurst_p90 / myHealth`. It only *looks* like a 20/30
  threshold because typical burst is 15–25. In one position the opponent held a **22-power
  single card** (Terminal Crescents at M20), so the cliff was at 22 and a player on 23 health
  was standing on it — a fixed-threshold curve cannot see that. Against a 6-power opponent,
  23 health is completely safe.
- **Both agree** health is genuinely sublinear near the **50 cap** (overflow healing is
  destroyed), and that *damage against a healing deck at cap is not progress*:
  `effectiveDPT = oppPPT − oppHealPerTurn; if ≤ 0 → the damage clock is INFINITE, not slow.`

### ⚔ D4. Reading the opponent's hand
- One expert warns: use the opponent's **deck average**, not their true hand, or the
  evaluator over-trusts its terminal checks and leaks hidden information.
- **My reading:** inside the search this is a non-issue — the evaluator runs on *determinized
  clones* where the opponent's hand is a resampled belief, which is exactly what
  determinization is for. But it **must not** be called on the live state outside search.
  Worth a guard.

---

## 3. Stage behaviour

| Stage | What dominates | What to stop rewarding |
|---|---|---|
| Rounds 1–6 | deck density, gems/turn, reaching M5 and M10. Both clocks are 10+, so scores should sit near 0.5 and health differences are nearly worthless. | — |
| Rounds 7–13 | the race model takes over; board permanents and shield density start to dominate because they are per-turn; health finally converts into turns | raw gems beyond ~8/turn; more cheap cards (dilution flips sign) |
| Round 14+ | burst and effective health; fast-plays; the M30 line is decisive or dead | deck quality (you will not cycle again); recruits are near-worthless |

⚔ Two ways to detect stage — **pick one**:
- `phase = clamp(max(maxMastery/30, (50 − minHealth)/40), 0, 1)` — derived from state
- `horizon = min(TTK_me, TTK_opp)` and multiply every deck-quality term by it — **no explicit
  stage rule at all**, "buy engine early, buy damage late" falls out for free.

My reading: the second. It is one term instead of two weight vectors, and it is the same
quantity the race model already computes.

---

## 4. [measured] — 30,000 games, outranks opinion where they conflict

| Feature | Q1 → Q4 win rate | OR/SD | p |
|---|---|---|---|
| early aggression | 41.6% → **62.2%** | **1.67** | 0.000 |
| total acquisitions | 40.3% → **62.5%** | **1.65** | 0.000 |
| mastery at round 8 | 44.2% → 57.2% | 1.32 | 0.000 |
| average buy cost | 42.1% → 57.9% | 1.29 | 0.000 |
| faction concentration | 54.4% → 46.3% | 1.10 **inverted** | 0.000 |
| focus count | 43.6% → 55.0% | 1.03 | 0.002 |
| champion share | — | 1.01 | **0.440 n.s.** |

- Killing an Ingeminex → the killer wins **63.7–67.8%** of the time.
- Shields prevent **2–3.2%** of all damage.
- ~**40%** of wins are comebacks.
- Game length: rounds p10/p50/p90 = **10 / 13 / 18**.
- Overwhelm (Infinity Shard) wins: **5.5–9.3%** of games — real, not a gimmick.
- First player wins **54–59%** — the mastery stagger undercompensates.

> ⚠ These are correlations between the *bots'* policies, not causal card strength.
> `champion share` being insignificant sits interestingly beside R8/R2: champion *count*
> doesn't predict winning, but the experts say champion *exhaust output* is the best asset
> class in the game. Both can be true if the bots buy the wrong champions.

---

## 5. Proposed implementation shape

```
eval(state, p):
  if terminal:                      return 1 / 0 / 0.5
  if burst(opp) >= effHP(me):       return 0.03..0.10      // they move first
  if burst(me)  >= effHP(opp):      return 0.70..0.80      // we still give them a turn

  for each player: N, D, powerPT, masteryPT, density  (cards priced at THAT player's mastery)
  killClock, ascendClock, TTK
  edge = TTK(opp) − TTK(me) − 0.5
  base = sigmoid(k · edge [/ sqrt(1 + min TTK)])          // ⚔ D3
  return clamp(base + boundedResidual(±0.05), 0.02, 0.98)
     // residual: unclaimed relic/destiny gate, one-short-of-threshold, row fit,
     //           reachable Ingeminex, extra-turn pending
```

**Weights go in the `W` layout** (`ShardsWeights.cs`) with **non-zero defaults** so CMA-ES
can tune them — sep-CMA-ES scales each dimension by `max(|start|, 0.05)`, so a weight
defaulting to 0.0 is untunable forever. Note the state-eval coefficients have **never been
tuned by anything**; that is a cheap win once the terms exist.

**Compose multiplicatively where a sign should decide.** Measured this session: an additive
base large enough to matter in a ladder swamps the term meant to make the decision
(`Base(200) + net(±2)` was worth *nothing*; `net × scale` was **+42 Elo**).

## 6. Acceptance tests before any strength gate

An evaluator that fails these is not ready regardless of what a probe says.

**Qualitative — each one kills a specific naive evaluator:**
- The **forced-win** position must read ≈1.0, not merely "ahead" — needs gems→row power.
- The **forced-loss** position (opponent has a deterministic 5-card mastery kill in hand)
  must read ≈0.02, not ~0.25 — needs the opponent-lethal check.
- The **30-HP-vs-50** position must read as *favoured*, on a ~6-turn Shard clock against an
  opponent who cannot kill — needs the ascend clock.
- A **46/50-health** position with a weak attacker must not reward further healing.
- A position where the opponent holds a **22-power single card** at 23 health must read as
  near-lost — needs burst-based danger, not a fixed health threshold.
- Structural: pure function of state; returns 1/0/0.5 on terminals; **must not be callable
  on the live state outside search** (see ⚔ D4).

**Quantitative — expert win-probability anchors for the side that just moved.** These are
judgement, not sim output, and where two experts scored the same position I have kept both:

| Position | Anchor | What it tests |
|---|---|---|
| pos-013 | **1.00** | forced win; must not read merely "ahead" |
| pos-007 | **0.02** | forced loss from a hand the opponent holds |
| pos-018 | **0.95** | big board + multiplier burst |
| pos-016 | **0.70** | 14 HP but M24 + ~2-turn Shard clock ⇒ *winning* |
| pos-022 | **0.62** | exact-cap healing line |
| pos-012 | **0.58** | champion kill at a good rate |
| pos-014 | **0.56** | early tempo |
| pos-015 | **0.55** | punishes crediting bought cards before they arrive |
| pos-019 | **0.52** | monster one power out of reach ⇒ worth zero |
| pos-017 | **0.45** | zero gem conversion on round 3 |
| pos-023 | **0.28** | damage into a healing deck at cap ⇒ ~0 progress |
| pos-021 | **0.20** | monster reachable by them, not by you |
| pos-020 | **0.12** | 23 HP against a 22-power single card |

Ship these as a unit test over the committed position files.

---

## 7. Open questions for you

1. **⚔ D1–D4 above** — especially faction concentration, where the experts split and the
   measured data contradicts one of them.
2. **Is the two-clock race model the right frame at all**, or would you rather I build a
   conventional weighted sum that is easier to reason about and tune?
3. **How aggressive should the terminal clamps be?** They dominate the function by design,
   and a false positive is expensive.
4. **Anything the experts got wrong about your game.** They are strong general players
   reasoning from the rules and 24 positions; you have played it.
