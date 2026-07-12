# Shards of Infinity — functional rules spec (paraphrased)

Fan re-implementation notes. All rules expressed in functional wording, paraphrased from the
official rules PDFs (Stone Blade Entertainment, stoneblade.com/pages/rules):
base rulebook (SOI_Rules_v1), Relics of the Future (SOI_ROF_Rules_v1),
Shadow of Salvation (SOI_003_Rules_Sheetv5_1), Into the Horizon (SOI_004_Rules_Final).
Card-level data lives in `cards.json` next to this file.

Resources: **Gems** (buy), **Power** (attack), **Mastery** (permanent progress, unlocks card
bonuses), **Health** (lose at 0), **Shield** (damage prevention from hand).

---

## 1. Base game

### 1.1 Setup
- 2–4 players. Random first player; play proceeds clockwise.
- Each player: character card (Health dial + Mastery tracker), Health = 50 (cap 50).
- Mastery stagger: first player starts at 0 Mastery; each subsequent player starts with 1 more
  than the previous (P2=1, P3=2, P4=3). Mastery cap 30.
- Starting deck (10 cards, white border): 7x Crystal, 1x Blaster, 1x Shard Reactor,
  1x Infinity Shard. Shuffle; draw 5 (5 remain in deck).
- Shuffle the 88 black-bordered cards into the face-down **center deck**; flip 6 face up as the
  shared **center row**.

### 1.2 Turn structure (three phases)

**1. Play Phase** — any number of the following, in any order, freely interleaved:
- Play cards from hand into your **play zone** (no cost to play). Resolve an ally's effects
  top-to-bottom immediately; choices/conditionals are locked in at play time. Gems/Power
  generated persist until end of turn (spend later if you like).
- Exhaust champions you have in play (turn sideways) to use their exhaust effects — each
  champion at most once per turn.
- Recruit any number of allies/champions/mercenaries from the center row by paying their gem
  cost; recruited cards go to YOUR DISCARD PILE.
- Fast-play any number of mercenaries from the center row: pay the same gem cost, resolve its
  effect immediately, put it in your play zone (see 1.4).
- Once per turn: **Focus** — exhaust your character card and pay 1 gem to gain 1 Mastery.
- Spend Power to attack champions (any time during the turn; see 1.5).

**2. Attack Phase** — assign ALL remaining Power as damage divided any way among opponents
(uneven splits fine). Then each attacked player may reveal any number of Shield cards from
their hand to prevent damage (see 1.6). Reduce each attacked player's Health by (damage
assigned − shields revealed). Players at 0 or less Health are eliminated.

**3. End Phase**, in order:
1. Each fast-played mercenary in your play zone goes to the BOTTOM OF THE CENTER DECK
   (never your deck/discard).
2. Allies in your play zone go to your discard pile. Champions remain in play.
3. Discard your remaining hand.
4. Ready (reset) all your champions.
5. Draw 5 cards. Turn passes clockwise.
Unspent Gems and Power evaporate at end of turn. Damage marked on any champion evaporates too
(1.5).

### 1.3 Universal rules
- **Card text beats rules text.**
- **Reshuffle / no deck-out**: whenever your deck is empty and you must draw or reveal from it,
  shuffle your discard pile into a new deck; if a multi-card draw runs the deck out mid-draw,
  reshuffle and continue drawing the remainder. You can never deck out.
- **Center row refill**: the moment a card leaves the center row it is replaced from the center
  deck, before anything else happens.
- **Play zone**: cards you played this turn stay face-up in front of you until cleanup — a card
  played this turn is NOT in your hand (can't be banished "from hand", can't be revealed as a
  shield).
- **Banish**: banished cards go to a shared removed-from-game pile. "Banish from hand/discard"
  effects may only take cards actually in that zone.
- **Health gain**: capped at 50.
- **Gaining Mastery**: never spent, never lost, capped at 30. Sources: Focus (1/turn) + card
  effects.

### 1.4 Mercenaries (fast-play)
- A mercenary in the center row can be either **recruited** normally (→ your discard, part of
  your deck forever) or **fast-played**: pay its gem cost, resolve its effect immediately, it
  sits in your play zone, and at end of turn it returns to the bottom of the center deck.
- Fast-play is once only per copy per visit — you can't fast-play then also recruit it.
- A fast-played mercenary COUNTS as an ally you played this turn (feeds Unify / Dominion and
  similar "played this turn" conditions). Official FAQ confirms.
- Exhausting a champion is NOT "playing a card" (RotF glossary clarification).

### 1.5 Champions & attacking champions
- Champions are recruited like allies (→ discard). When later played from hand they STAY in the
  play zone across turns until destroyed.
- After playing a champion you may exhaust it that same turn (no summoning sickness); exhaust
  effects are everything after the "Exhaust:" marker; once per turn per champion; all your
  champions ready at your end phase.
- Attacking: spend Power to deal damage to an enemy champion at ANY time during your turn
  (unlike players, who are only hit in the attack phase). A champion is destroyed only if a
  single player deals total damage equal to its full Defense **within one turn** — champions do
  not track damage from turn to turn.
  - **Accumulation ruling**: damage on a champion persists WITHIN the turn (you may hit it in
    several increments as you generate more Power, e.g. play a card, hit for 2, play another,
    hit for 3) but resets at end of turn; partial damage is simply wasted. This matches the
    digital adaptation. The rulebook's summary line "use enough Power to equal the champion's
    Defense" is shorthand for the same thing. TODO-VERIFY: no official ruling text found that
    explicitly addresses split attacks within one turn beyond the "in a single turn" clause —
    engine should treat within-turn accumulation as legal.
- A destroyed champion goes to its CONTROLLER's discard pile (it can be redrawn/replayed later).
- Shields never protect champions (FAQ). Base-game champions' printed Shield icons are only
  usable while the card is in hand (see 1.6); one RotF relic (Praetorian-02) explicitly breaks
  this — see 2.3.
- Card effects that "destroy" a champion bypass Defense entirely (e.g. a Unify destroy-effect
  can kill an "can't be damaged" champion — FAQ: Thorn Zealot can destroy Li Hin).

### 1.6 Shields & player damage
- All player damage lands at once, at the attack phase: attacker declares the full split among
  opponents first; THEN each attacked player may reveal any number of cards bearing a Shield
  value from their HAND; each prevents damage equal to its Shield value.
- Revealed shield cards are NOT discarded — they stay in hand, are still playable next turn,
  and the same card can shield again on a different opponent's turn.
- Shields prevent damage to the owner only, never to champions.
- Only damage is preventable: effects that say "lose Health" are not damage — no shields, and
  they don't count as "unblocked damage" for cards that care (ItH FAQ).
- **Multiple defenders order: the rules give no ordering; each defender's shield decision is
  independent and only affects themselves. TODO-VERIFY (engine: resolve in clockwise turn
  order from the active player; outcome-equivalent).**
- A champion's printed Shield is inert while the champion is in play (base game): shields are
  revealed FROM HAND only.

### 1.7 Focus, Mastery & thresholds
- **Focus**: once per turn, exhaust the character card + pay 1 gem → gain 1 Mastery. It is an
  exhaust power (character card readies at end of turn like champions).
- **Mastery threshold bonuses** ("Mx:" lines on cards): granted when the card is PLAYED (or the
  champion EXHAUSTED) if your Mastery is already ≥ x at that moment. Never re-checked — Mastery
  gained later in the turn doesn't retro-activate them.
- Threshold bonuses cost nothing (Mastery is not spent).
- Self-counting: if the card itself grants Mastery above the threshold line, that Mastery counts
  for its own threshold (resolve top-to-bottom: Fungal Hermit at M9 → +1 Mastery → M10 → its
  M10 bonus fires; Cache Warden likewise — FAQ-confirmed).
- Exhaust effects also honor thresholds written below the exhaust line (Systema A.I. FAQ).

### 1.8 The Infinity Shard & game end
- Infinity Shard (1 per starting deck): play for 2 Power; M10: 3 Power instead; M20: 5 Power
  instead; M30: INFINITE Power — effectively lethal, win by killing everyone else the same turn.
- Reaching Mastery 30 does nothing by itself; you still have to draw/play the Shard (or
  otherwise win). Mastery is capped at 30.
- **Elimination**: Health ≤ 0 ⇒ out of the game immediately (their cards leave play with them).
- **Win**: last player with Health remaining. Simultaneous drop below 1 (possible with ItH
  "lose Health" effects) ⇒ tie (ItH FAQ).

### 1.9 Factions & trigger keywords
Four base factions (a 5th, **Aion**, arrives with Shadow of Salvation/Into the Horizon):

| Faction | Color | Theme | Keyword |
|---|---|---|---|
| Homodeus | grey/yellow | champions-in-play synergy | (RotF adds **Inspire**) |
| The Order | blue | mastery, shields, draw | **Dominion** |
| Undergrowth | green | healing, ally-chains | **Unify** |
| Wraethe | purple | discard-pile synergy, banish | (RotF adds **Echo**) |

- **Unify** (Undergrowth): bonus applies if you have played another Undergrowth card this turn
  OR you reveal an Undergrowth card from your hand at resolution. Fast-played mercenaries count
  as played. The card itself does not satisfy its own Unify.
- **Dominion** (Order): bonus applies if you have played (or reveal from hand) at least one
  card of EACH of the other three base factions (Homodeus, Undergrowth, Wraethe) this turn.
- Exact printed reminder text for both: see cards.json / keyword notes at the end — flagged
  TODO-VERIFY until cross-checked against card scans.
- Faction membership matters only for these conditions (and Nemesis-variant logic); there is no
  other faction rule.

---

## 2. Relics of the Future (expansion #1)

### 2.1 Contents / setup
- 24 black-bordered center cards shuffled into the center deck (adds the **Echo** and
  **Inspire** keywords plus more cards for all four factions).
- 8 grey-bordered **Relic** cards — NOT shuffled in. At setup each player sets aside the TWO
  relics belonging to their chosen character.

### 2.2 Relic recruitment (core new rule)
- Once a player has reached **Mastery 10**, they may recruit ONE of their two set-aside relics,
  for free (no gems, Mastery not spent), during their turn; it goes to their DISCARD PILE
  exactly like a center-row recruit.
- Strictly one relic per player per game — the other is dead weight forever.

### 2.3 The 8 relics (owners; full effects in cards.json)
| Character | Faction | Relics |
|---|---|---|
| Decima | Homodeus | Praetorian-01, Praetorian-02 |
| Tetra | Order | Datic Robes, Terminal Crescents |
| Volos | Undergrowth | Entropic Talons, Panconscious Crown |
| Ko Syn Wu | Wraethe | The Heart of Nothing, The World Piercer |

Official-FAQ ruling anchors:
- Praetorian-01 returns to hand from your DISCARD PILE when you play a champion (playing only —
  exhausting a champion doesn't trigger it; it can't bounce from the play zone, and not on the
  turn it was played since it's still in the play zone then).
- Praetorian-02 is a champion whose Shield works while IN PLAY (the printed shield does NOT
  work from hand). This is the one exception to "shields from hand only".
- Entropic Talons (ongoing): whenever you would gain Health on your turn, you also gain that
  much Mastery — even if you are at the 50-Health cap and the heal fizzles.

### 2.4 New keywords
- **Echo** (Wraethe) and **Inspire** (Homodeus): conditional bonus lines like Unify/Dominion.
  Inspire keys off champions you have in play; Echo keys off the Wraethe discard-pile theme.
  Exact conditions recorded with the cards in cards.json (TODO-VERIFY marker there until
  confirmed from scans).
- Keyword lines are independent: text on a separate line from an Echo/Inspire clause resolves
  regardless of the condition (FAQ: Limiter Drones / Pall Shades still draw).

### 2.5 Competitive variants shipped in the RotF rules (optional modes)
- **3-Player Bloodbath**: any Power you assign to one opponent hits BOTH opponents for the full
  amount; each defends separately. Last standing wins.
- **Health Auction**: bid Health to pick character + go first (clockwise raising auction;
  winner loses bid, picks first, plays first; repeat for remaining seats; last unbid player
  starts at full 50).
- **2v2 Team Attack**: teams of 2 sit opposite; you may only damage the player across from you
  (champions of either enemy are fair game); excess damage rolls over to the other enemy once
  the first is eliminated; if your teammate dies, both enemies are "across from you". Random
  team goes first; everyone starts at 0 Mastery.
- **Relic Draft**: deal 2 random face-up relics per player; snake-draft (clockwise pick 1, then
  counter-clockwise pick a 2nd starting from the last picker); last drafter takes first turn.
- **Mystery Relics**: deal the relics out randomly, keep them secret until Mastery 10.
- **Single-player Nemesis**: automated opponent flips the top center card each turn, plays all
  row cards matching its faction, etc. (Solo-only; out of competitive scope — see the RotF PDF
  if ever needed.)

---

## 3. Shadow of Salvation (expansion #2) — COMPETITIVE content only

(The box is mostly a 2–5 player co-op campaign — bosses, fate deck, shadow champions, saved
cards. All of that is intentionally out of scope here.)

### 3.1 Competitive ("Classic Mode") additions
- Shuffle its **12 black-bordered center cards** into the center deck. These are:
  3x Cloud Oracles (errata replacement — see 3.3) + the Aion-faction cards
  Swyft x2, Breaker x1, Brute x2, Dash x2, Lucky x2. Some carry the **Warp** keyword (see 4.4 —
  Warp premiered here in SoS's glossary).
- **Rez** becomes a 5th playable character (own Health/Mastery dial), faction **Aion**, with two
  unique grey-bordered relics: **Slipstream Shard** and **Warpquartz** (normal relic rules:
  set both aside, recruit one free at Mastery 10).

### 3.2 5-player Alliance mode (competitive variant)
- 5 players in a circle; your two neighbors are allies, the other two are enemies. You win when
  BOTH your enemies are eliminated (even if you or your allies died before that); two players
  can complete this simultaneously and share the win. Mastery stagger 0/1/2/3/4 as usual.

### 3.3 Cloud Oracles errata
- SoS ships replacement copies of RotF's Cloud Oracles; if you own RotF, REPLACE the old copies,
  otherwise just add the new ones. (Functional difference recorded in cards.json.)

### 3.4 Shadow Summoning Draft (competitive variant using co-op cards)
- Deal 5 random Shadow Champions to each player; each keeps 3 face-down. On your turn you may
  pay 3 gems to summon one; resolve its Ambush; you control it, may exhaust it for its Power,
  and it satisfies Inspire. Destroyed ⇒ removed from game. (Requires co-op card data — out of
  scope for cards.json; noted for completeness.)

---

## 4. Into the Horizon (expansion #3)

### 4.1 Contents / setup
- 30 black-bordered center cards shuffled into the center deck (13 distinct faction cards for
  Aion/Homodeus/Order/Undergrowth/Wraethe + 5 **Ingeminex** monsters).
- 30 purple-backed **Destiny** cards: shuffle separately, deal **6 face up** as a Destiny Row
  near the center row.
- Leave table space for revealed Ingeminex. If not playing with RotF relics, remove the
  Ingeminex **Corruption** from the center deck (its reward needs relics); or ignore it when
  revealed and flip the next card.

### 4.2 Destinies
- Acquisition: once per game, on your own turn, at any point, IF your Mastery is **5 or more**,
  you may take ONE Destiny card from the Destiny Row — free, no resource cost, **chosen** from
  the shared face-up row (NOT per-character, NOT automatic — you pick when and which).
- The taken Destiny is NOT replaced — the row only shrinks, so later players have fewer options.
- It sits in front of you; its ability applies for the rest of the game.
- Exceptions: the Ingeminex rewards of **Agony** and **Malice** grant a Destiny WITHOUT needing
  Mastery 5 and WITHOUT consuming your once-per-game right (you can still take your normal one
  at M5+ later). These are the only ways to hold multiple Destinies.

### 4.3 Ingeminex (monsters in the standard competitive game)
- Live shuffled in the center deck. When one is REVEALED from the center deck (row refill, or
  any effect revealing/recruiting off the top): it never enters the row, a hand, a deck or a
  discard pile — it goes face up to its own space beside the row, and the next center-deck card
  replaces it for whatever purpose the reveal had (row refill / recruit effect / etc.).
- No limit on simultaneously active Ingeminex.
- **Attack**: each Ingeminex's attack effect triggers ONCE, at the end of the turn on which it
  was revealed, and hits ALL players (not just the active one). It does NOT keep attacking on
  later turns — after that one attack it just sits there until defeated.
- **Defeating**: during your turn, spend Power equal to the number printed on the Ingeminex
  (same mechanism as attacking a champion). It goes to the BOTTOM of the center deck (no row
  replacement, since it never occupied a slot) and YOU alone gain its printed reward,
  immediately.
- Defeat it on the very turn it was revealed ⇒ its attack never triggers.
- TODO-VERIFY: whether partial Power can accumulate on an Ingeminex within one turn — the rules
  say "similar to using Power to defeat an opponent's Champion", so mirror the champion ruling
  (within-turn accumulation OK, resets at end of turn).

### 4.4 New keywords / terms
- **Warp**: you may fast-play this card from the center row WITHOUT paying gems — including
  cards that are not mercenaries. A warped card follows fast-play rules (effect immediately,
  play zone, bottom of center deck at end of turn). (Introduced in SoS, used across SoS/ItH.)
- **Reset**: ready an exhausted champion during the turn — it may exhaust again this turn.
- **Faction: Aion** joins the roster (5 factions total).
- Even/odd cost matters to some ItH cards: cards WITHOUT a gem cost (Crystal, Blaster, relics,
  etc.) count as NEITHER even nor odd (FAQ).
- "Lose Health" effects (e.g. Oblivion Gatekeeper, some Destinies): not damage — unpreventable
  by shields, don't trigger "unblocked damage" effects, can cause simultaneous-death ties.
- Copying an effect (Ojas-style) is NOT playing the card: a copied effect grants the copied
  card's base clauses but not "when you play this" set-bonuses (FAQ: a copied Cinder Scars
  effect still gives the M3-conditional gems but the original's play-count bonus doesn't
  double-fire). Record per-card in cards.json.

---

## 5. Engine-facing invariants (summary)

1. Only the active player ever holds Gems/Power; both zero out at cleanup.
2. Champion damage is per-(attacker-turn) transient state; clears at end of turn.
3. Shield reveal is a defender decision made AFTER full damage assignment is public.
4. Mastery is monotonic 0→30; thresholds snapshot at play/exhaust time.
5. Health clamps to [.., 50]; elimination at ≤0 checked after shield math (and after
   lose-Health effects); simultaneous ⇒ tie.
6. Center row always refilled to 6 immediately; Ingeminex bypass the row.
7. Personal decks never empty while discard has cards (auto-reshuffle mid-effect).
8. Zones: hand / deck / discard / play zone (persistent for champions) / banish pile (global,
   removed-from-game) / center deck / center row / relic set-aside / destiny row / Ingeminex
   space.
9. Fast-played & warped cards return to bottom of CENTER deck at cleanup, in the play order?
   TODO-VERIFY bottom-stack ordering when several return at once (engine: active player picks
   the order).

---

## 6. Open TODO-VERIFY items

1. **Shield-reveal ordering with multiple defenders** — rulebook silent; independent decisions,
   engine uses clockwise-from-attacker. (Cosmetic.)
2. **Champion damage accumulation across several attacks in one turn** — "in a single turn"
   wording + digital adaptation support it; no explicit printed example of split attacks.
3. **Exact printed reminder text of Unify / Dominion / Echo / Inspire** — functional conditions
   recorded per card in cards.json; cross-check against card scans pending.
4. **Original RotF box quantity distribution** — Saga-collection inventory yields 22 non-Cloud-
   Oracles center cards + 3 Cloud Oracles = 25 ≠ 24 printed count; one quantity likely differs
   in the original box (suspect Cloud Oracles x2, or a x2 that was x1). Gameplay-irrelevant if
   you just pick a distribution and note it.
5. **Bottom-of-center-deck ordering** when multiple fast-played/warped cards return at cleanup.
6. **Ingeminex within-turn Power accumulation** (mirrors champion ruling; assumed yes).
7. Per-card LOW-confidence entries in cards.json (see its validation block).

---

## 7. Corrections from the card-photo verification pass (2026-07-12)

The engine follows the printed cards where this document's paraphrases disagreed:

1. **Entropic Talons converts health gains to POWER, not Mastery** (§2.3 above is a
   transcription error; the printed icon and the RotF FAQ both say power).
2. **Warp is an effect line ("Warp N"), not a keyword carried by the row card** (§4.4's
   phrasing was ambiguous): the card you PLAY grants "fast-play one center-row ally
   costing ≤ N for free"; the warped card then follows fast-play rules.
3. **Unify requires another Undergrowth ALLY** (played or revealed) — champions never
   satisfy it (§1.9 said "card"; the printed reminder text on five photographed cards
   says "Ally").
4. **Full power assignment is mandatory** at the attack phase (§1.2 was right; noted
   here because early engine drafts allowed wasting power).
5. **Rez's relics ship in the Shadow of Salvation box** and work with SoS alone —
   relic availability follows the SET that ships the relic, not the RotF toggle.
6. Imperative effect texts ("Return a…", "Recruit a…", "banish a…" without "may") are
   MANDATORY; the sets print "you may" explicitly when a choice is optional.
7. Printed name spelling: "Ru Bo Vai, The Transcendant" (-ant).
