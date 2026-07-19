---
name: shards-cards
description: The Shards of Infinity card registry — every implemented card's stats, effect composition, and the engine rulings it depends on. Use when adding, changing, removing, or looking up any SoI card, character, relic, destiny, or Ingeminex. MUST be updated on every SoI card change.
---

# Shards of Infinity Card Registry

Personal fan re-implementation of the tabletop game (Stone Blade Entertainment). All
mechanics compiled from the official public rulebooks + photo-verified card research
(reports referenced from `Tools/ShardsData/rules-notes.md`); every rules text in the code
is OUR functional paraphrase — never copy printed card prose or flavor text. Official
art, if imported, is for PERSONAL USE ONLY (M7 import window; images git-ignored).

## Where things live

- **Definitions**: `Assets/Scripts/Shards/Content/` — one builder file per set:
  `ShardsBaseSet.cs` (10 starters + 88 center), `ShardsRelicsSet.cs` (24 center + 8 relics),
  `ShardsShadowSet.cs` (12 center + Rez's 2 relics), `ShardsHorizonSet.cs` (25 center +
  5 Ingeminex + 30 destinies). `ShardsContentRegistry.EnsureRegistered()` registers all.
- **Full card table** (id / name / set / faction / type / cost / qty / def / shield / text):
  `Tools/ShardsData/cards-table.md` — REGENERATE, never hand-edit:
  `cd Tools/EngineVerify && dotnet test --filter ExportShardsCardTable`
  (also seeds `Tools/soi_art_sources.json` if missing).
- **Effect vocabulary**: `Assets/Scripts/Shards/Engine/ShardsEffects.cs` — `Gain`,
  `E.Seq`/`ShardsComposite` (sequential — own mastery gain precedes later thresholds),
  `E.At`/`AtMastery` (ADDITIVE delta: "3, M10: 6 instead" = base 3 + At(10,+3)),
  `BestByMastery` (true "instead" tiers), `If` (+`Inspire`/`Echo`/`Character`/`FullHealth`),
  `Unify` (another ally of the faction played OR reveal from hand — decision),
  `Dominion` (played/reveal one of EACH of H/U/W), `PerCount`, `OpponentLosesMastery`,
  `BanishUpTo`, `ReturnFromDiscard`, `DestroyEnemyChampions`, `WarpUpTo`, `RecruitFromRow`,
  `CopyPlayedEffect`, `AllPlayersLoseHealth/LoseMastery/Discard/DestroyBiggestChampion`,
  `Custom`/`Do`.
- **Static hooks on `ShardsCardDef`** (`ShardsTypes.cs`): `Taunt` (Zetta — mid-turn attacks
  on other targets stay blocked; the END-TURN split may reach the owner/other champions
  ONLY when the same answer assigns Zetta lethal — options carry `Required`/`Amount`/
  `OwnerIndex` UI hints and `SplitDamageFlow` drops assignments that violate the rule;
  power > 1000 skips the split entirely and kills every opponent instantly), `CanBeAttacked`
  (Li Hin / Raidian / Drakonarius), `DefenseAura` (Ferrata Guard, One Mind One Army),
  `CostModifier` (Axia), `ShieldInPlay` + `DynamicShield` (Praetorian-02, Datic Robes),
  `ReturnsFromDiscardOnChampionPlay` (Praetorian-01), `OnDamageDealt` (Blood for Blood —
  ⚠ its effect is QUEUED by ApplyDamage during the defense chain, so `AfterDefenses` must
  queue cleanup behind the effect queue whenever `_effectQueue.Count > 0`, never call
  `FinishEndTurn` synchronously; otherwise cleanup empties the play zone before the
  trigger resolves and the banish choice silently vanishes — shipped bug, now pinned by
  `BloodForBlood_TriggersOnFivePlusUnblockedDamage_BanishesPlayedCard`),
  `KeepFastPlaysCharacter` (Swyft/Rez), `RecruitsToHand` (Breaker),
  `RedirectChampionRecruitsToDeckTop` (Maglev Tunnels),
  `ReturnFromDiscardOnFactionPlay` (The Dispossessed).
- **Behavior implemented ENGINE-side by def id** (grep before renaming ids!):
  `project_yggdrasil` (CountsAs/CountPlay W↔U swap), `phasic_technology` (ShieldValue +2
  H/O), `cloud_oracles` (skipped when SoS enabled — errata replacement by
  `cloud_oracles_sos`), `ingeminex_corruption` (removed without RotF).
- **Characters**: decima / tetra / volos / kosynwu (+ rez with SoS). All identical:
  Focus = exhaust character + 1 gem → +1 mastery, once per turn. Relic pairs bind via
  `ShardsCardDef.Character`.
- **Tests**: `ShardsContentTests.cs` (counts, setup, termination, conservation),
  `ShardsRulingsTests.cs` (one test per FAQ ruling), `ShardsEngineTests.cs` (structural,
  stub set). Keep `Tools/EngineVerify` green.

## Checklist for ANY card change

1. Edit the builder in the right set file (effects composed from the vocabulary; new
   mechanics → prefer a new generic effect class or def hook over `Custom`).
2. If quantities/sets changed → update `Counts_MatchPublishedComponentLists`.
3. Add/adjust a ruling test if the card carries a printed FAQ ruling.
4. Regenerate `Tools/ShardsData/cards-table.md` (command above).
5. `cd Tools/EngineVerify && dotnet test` — all green.
6. Update this file's rulings list if a new ruling was encoded.

## Encoded rulings (each pinned by a test in ShardsRulingsTests / ShardsEngineTests)

- Staggered start mastery 0/1/2/3; cap 30; thresholds check AT PLAY/EXHAUST time and a
  card's own mastery gain counts for its own threshold (Fungal Hermit / Cache Warden).
- Champion damage accumulates WITHIN a turn (partial hits legal), evaporates at end
  phase; destroyed only by one player reaching full (effective) defense in one turn.
- Champion printed shields are INERT in play — shields reveal from HAND, are NOT
  discarded, and never protect champions. Praetorian-02 is the one in-play exception
  (and never works from hand). Ru Bo Vai M10 pierces all shields for the turn.
- Zetta's taunt protects the OWNER (no end-turn damage assignable) and other champions.
- Li Hin can't be attacked with power but destroy-EFFECTS kill it (Thorn Zealot FAQ).
- Fast-played mercenaries: effect now, play zone, BOTTOM of center deck at cleanup,
  count as played allies of their faction (feeds Unify etc.); Swyft (Rez) may keep them.
- Unify needs ANOTHER ally of the faction (champions never satisfy it; self never
  counts; hand-reveal alternative). Dominion needs one card of EACH of H/U/W
  (played and/or revealed).
- "Lose health" is NOT damage: shields never apply; simultaneous drop below 1 = TIE
  (WinnerIndex −1); eliminations are checked after ALL simultaneous losses land.
- Relics: set both aside; recruit exactly ONE free at M10 (the other stays set aside,
  dead weight — except the Ingeminex Corruption reward fetches it to hand).
- Destinies: shared face-up row of 6; take ONE free at M5+, once per game, row never
  refills; Agony/Malice rewards (and Stolen Futures) bypass both limits; destinies
  exhaust like champions and ready at the owner's end phase.
- Ingeminex: never enter the row (own space beside it; next card refills), attack ALL
  players once at the end of the reveal turn, defeat (accumulating power, like
  champions) cancels the attack, defeated → bottom of center deck, defeater alone
  gets the reward.
- Warp N (an EFFECT, not a card property): fast-play a row ally costing ≤ N for free;
  Deadly Recruits' fast-play is NOT warp — the card is kept (discard at cleanup).
- End phase order: fast-plays → center-deck bottom, play zone → discard, discard hand,
  ready champions/destinies/character (readying happens at END phase, not turn start),
  draw 5 (+ Heart of Nothing bonus), pools/turn-flags reset. Mid-draw reshuffle: never
  deck out.
- Entropic Talons converts health gains to POWER (photo-verified; the "mastery" claim
  in early notes was wrong) and fizzled at-cap heals still count.
- Copying an effect (Ojas/Taur/Duplication Fabricator) is NOT playing the card: no
  faction counts, no play triggers; Cinder Scars' pair bonus needs a REAL second copy.
- Slipstream Shard M20: extra turn, once per game per player.
- Full power assignment at the attack phase is MANDATORY (Min=Power on the split
  decision; DefaultOptionIds pre-fill a legal full assignment for timeouts/bots).
- Imperative texts without "may" are mandatory (Korvus/Shadebound/Zen Chi Set returns,
  Portal Monk/Crystal Gate recruits, Forged in Flame banish); "may" wordings use the
  optional effect variants (Malice's champion return, Shadow Apostle banishes…).
- Rez's relics ship in the SoS box: relic set-aside follows the SET that ships the
  relic, not the RotF flag (Rez has relics with SoS alone).
- The active player can eliminate THEMSELVES mid-turn (Bound for Life, Gatekeeper) —
  RoutePriority passes the turn instead of deadlocking.
- General Decurion M20 doubles Homodeus ally effects on EVERY play path (hand,
  fast-play, warp — fast-plays count as played allies).
- Shard Defiant may decline both options (the revealed card stays on top).
- Numeri Drones / Anomaly Cleric redirects are COUNTERS (two exhausts = two redirects).

## Known simplifications (revisit in M8 polish)

- Multi-defender shield order: clockwise from attacker (rulebook silent; outcome-equivalent).
- Several bottom-of-center-deck returns stack in play order (rulebook silent).
- Gem-cost-gated destiny exhausts (Shard Defiant, Whatever it Takes) no-op when
  unaffordable instead of being unactivatable (the exhaust is still spent) — UI should
  grey them out.
- Giga's Dominion-gated exhaust: activating with the condition unmet wastes the exhaust
  (effect fizzles) rather than being illegal.
- Ingeminex Malice's "highest-cost champion" tie-break: deterministic (lowest instance
  id) instead of owner's choice.
- Nemesis solo variant, co-op campaign, Shadow Summoning Draft, RotF table variants
  (Bloodbath/Auction/2v2/drafts): out of scope by user decision.
