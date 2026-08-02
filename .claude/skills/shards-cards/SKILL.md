---
name: shards-cards
description: The Shards of Infinity card registry — every implemented card's stats, effect composition, and the engine rulings it depends on. Use when adding, changing, removing, or looking up any SoI card, character, relic, destiny, or Ingeminex. MUST be updated on every SoI card change.
---

# Shards of Infinity Card Registry

Personal fan re-implementation of the tabletop game (Stone Blade Entertainment). All
mechanics compiled from the official public rulebooks + photo-verified card research
(reports referenced from `Tools/ShardsData/rules-notes.md`); every rules text in the code
is OUR functional paraphrase — never copy printed card prose or flavor text. Official
art, if imported, is for PERSONAL USE ONLY (M7 import window; images git-ignored —
see the art-pipeline skill). Engine architecture (pump, end-turn chain, the three
pump gotchas, glow hints): see the shards-engine skill.

## Where things live

- **Definitions**: `Assets/Scripts/Shards/Content/` — one builder file per set:
  `ShardsBaseSet.cs` (10 starters + 88 center), `ShardsRelicsSet.cs` (24 center + 8 relics),
  `ShardsShadowSet.cs` (12 center + Rez's 2 relics), `ShardsHorizonSet.cs` (25 center +
  5 Ingeminex + 30 destinies), `ShardsDuelSet.cs` (**Duel of Doom** — 21 new defs + ~43
  errata replacement defs). `ShardsContentRegistry.EnsureRegistered()` registers all.

## Reveal rule (engine-wide, 2026-07-26)

Any effect that REVEALS from a player's deck pulls through `ShardsEngine.PeekTopOfDeck`,
which shuffles the discard back in when the deck runs dry mid-reveal; if both are empty it
reveals what exists. Reveals are never optional ("you may reveal" is gone) and the pick
window ALWAYS opens listing EVERY revealed card — the ones that don't qualify carry
`DecisionOption.Disabled` (shown, greyed, and rejected by `ValidateAnswer`), so the player
sees the whole reveal and passes deliberately. `ShardsHorizonSet.RevealTopForChampion` is
the shared implementation (Legion Carrier 3 / errata 5).

## Card-text house style (2026-07-25 sweep — EN defs AND SoiFrenchCards)

1. **Zero parentheses** in rules text — asides become short sentences (`Once per game.`),
   suffix clauses (`…, rounded up`), or em-dash clauses; mastery asides become their own
   tier line (`M20: 6 instead.`).
2. **One effect per line** (`\n`); mastery tiers and keyword clauses (`Unify:`, `Echo:`,
   `Dominion:`, `Allegiance X 4:`) always start a line. `Iconize` auto-newlines `M##:`
   tokens, so a literal `\n` before them is optional but the rest is explicit.
2b. **Paragraph rhythm is a DISPLAY rule, not a def rule** (2026-07-26, `Iconize`): a
   mastery tier renders TIGHT under the effect it modifies (any `\n` before it is
   swallowed — the old double newline made "M10" look like its own effect), while every
   other `\n` becomes a half-height gap. A line starting with an em-dash bullet ("Choose
   one:" modes) is a sub-item and stays tight. Long texts auto-shrink rather than
   overflow: `CardView` caps the rules box at `MaxRulesBoxTop` and turns on TMP
   auto-sizing past it, so a card reads identically at every zoom.
3. `Iconize` also renders inline `shield`/`bouclier` words as the shield icon and carries
   a safety regex collapsing `(\s*\n` so a paren slip can never orphan again.
4. **ORDER MATTERS in `Iconize`**: the LINE-ANCHORED `Attack:`/`Reward:` icon swaps must run
   BEFORE the paragraph pass, which injects a `<size>` tag at the head of every following
   line — markup between `^` and the keyword silently drops the icon. That is exactly how the
   Reward chest went missing on every Ingeminex (2026-07-27).
Every SoI card change keeps BOTH sides in this style (EN def + `SoiFrenchCards`).

## Duel of Doom (`ShardsDlc.Duel = 8`, set id "duel") — FULLY SHIPPED 2026-07-24 (211 EngineVerify tests green, Unity clean)

Requires all three other DLCs (`ShardsEngine.NormalizeDlc` forces them on). Errata swap:
a def with `ShardsCardDef.ReplacesId` skips that base def when Duel is on (generalized
`cloud_oracles_sos` pattern, `ShardsEngine.ReplacedIds`) — applies to the center deck AND
the destiny deal AND relic set-aside.

**New rules-side vocabulary** — `ShardsDuelEffects.cs`: `AllegianceEffect` (own ≥N of a
faction — deck+hand+discard+play+champions, counts itself), `Scry`,
`OpponentDrawsThenDiscards`, `ShardsDuel.DistinctFactionsPlayed`. Engine hooks on
`ShardsCardDef`: `CountsAsEveryFaction` (Prism), `CannotBeRerolled` (Comet),
`KeepFastPlaysAtMastery` (Swyft M10), `ImmuneToIngeminex` (Doom Gate),
`DoublesExhaustsAtMastery` (Unknown God M20). Player flags: `HealingDoubledThisTurn`,
`OverflowHealthToPowerThisTurn`, `ShieldsDoubledUntilNextTurn` (Praetorian-02, cleared in
StartTurn), `HeroAbilityUsedThisTurn`, `FirstBuyUsedThisTurn`, `NextChampionsIntoPlay`.
**Dominion reworked when Duel on** (3+ other cards of 3+ different factions; else base
H/U/W) — flag-gated inside `Dominion`.

**Row reroll** (repriced 2026-07-25): `ShardsRerollRowAction` — pay
`ShardsEngine.RerollCost(player)` = **1 + RerollsThisTurn** (climbs per use, resets each
turn; counter on `ShardsPlayer`, cloned/hashed/snapshotted) → `BottomRowCardAndRefill`
(also used free by Order Initiate errata). **Hero abilities**: `ShardsHeroAbilityAction` +
`HeroAbilityFor(characterId)` (Tetra/Volos/KoSynWu/Rez active; Decima = passive first-buy
discount in `EffectiveCost`); separate from Focus, once/turn.
**Hero draft**: `HeroDraftFlow` runs at Setup (Duel) — board built first, reverse seat
order, no duplicates, `_draftDefaults` = lobby picks; players create with null CharacterId,
`SetAsideRelicsFor` runs after each pick. UI: draft via the decision modal; in-game the
ability is a REAL CARD beside the portrait (`soiability:<heroId>` face, taps when used,
passive = permanent untapped; portrait click = Focus) + per-slot reroll buttons with the
live climbing price. **Ability ART** (2026-07-25) is its own piece per hero —
`Assets/Art/Shards/Cards/soiability_<hero>.png`, prompts in
`Tools/ShardsData/art-prompts-extra.json` (the manual companion for non-def art, alongside
`soichar_*`); adding one requires `Pascension/Rebuild Card Art Index` since these ids are
not card defs and no test exports them. The card wears a pulsing gold outer halo exactly
while the ability is usable.

**New defs (20)**: relics praetorian_03/multitask_brain/unknown_god/star_seeker/doom_gate
(one per hero); cards testudo_vanguard, century_forge, riposte_doctrine, index_of_futures,
bulwark_chanter, aegis_archivist, thornshell_warden, nectar_alchemist, lifebloom_ritual,
doomstalker, bleak_communion, grim_tutor, comet, prism, longshot.
**whisper_extractor REMOVED 2026-08-02** (user decision: too strong even after the
2026-07-25 redraw nerf). Gone with it: the `OpponentDrawsThenDiscards` effect class, the
`soi.handpick` context (bot handlers, search candidates, SoiSim coverage expectation) and
the two FR decision-title patterns. The `OppHandStrips` atom + weight STAY (W layout is
append-only); the art png/CardArtIndex entry remain on disk until the next
`Rebuild Card Art Index` (editor-only).
**grim_tutor** (2026-07-25, Wraethe Mercenary, cost 3, qty 2): Custom flow — decision over
the player's DECK sorted by DefId/InstanceId (never deck order — the World Piercer
anti-leak rule), chosen card to hand, `Rng.Shuffle(deck)`, `LoseHealth(3)` (a loss, not
damage; applies even with an empty deck). Context `"soi.tutor"`. **Errata (~43
`<id>_duel` defs)**: 4 Allegiance conversions (ferrata/mainframe/hounds/the_lost) + ~16
stat tweaks + 5 hook + 12 bespoke + 7 destiny — all in `RegisterErrata` sub-methods.

**Testudo Vanguard rework (2026-07-25, user decision)**: while its owner defends,
champion split options may be OVER-assigned (0..Power like faces) — deferred hits then
subtract the shield prevention per champion, so overkill "pays through". Taunt (Zetta) ×
Testudo: the taunt's deferred hit resolves FIRST; if it SURVIVES post-shields, the wall
held — every other deferred champion hit AND the face damage resolve as ZERO
(`ResolveDefenderDamage`). The UI (`SoiDecisionModal`) renders that defender's champions
with the hero-style 0/−/+/MAX strip (detected via the `ShieldsProtectChampions` def flag —
no wire change; `option.Amount` stays the live-HP display + taunt-unlock threshold).
Pinned by `Duel_Testudo_*` tests (over-assign kill, exact-lethal saved, taunt-held zero).

**Ingeminex rewards**: destroying one by CARD EFFECT is defeating it — `DestroyActiveMonster`
takes the destroyer's seat index and queues `RewardEffect` just like the attack path does
(Doom Gate paid nothing at all before 2026-07-27). Pass -1 only for a kill that belongs to
nobody. Doom Gate floods **30** Ingeminex (was 20).

**Testing**: `Duel_*` tests in `ShardsContentTests` (draft, errata swaps, reroll, hero
ability, Decima discount, full-game random-bot termination). SoiSim gained a `--dlc duel`
flag (`SimConfig.AllDlc` now settable); the value model gates green on the duel pool with
existing weights + `ShardsCustomAnnotations` entries for every new Custom/Do — no retune
needed.

**Adversarial review pass (2026-07-24, 125-agent workflow, 37 confirmed findings — ALL fixed, 220 tests green):**
- `CannotBeFastPlayed` def flag (Comet) enforced in WarpUpTo/WarpFromRow/FastPlayLoose/BuyCard — closes the free-instant-kill via unlimited Warp.
- `DoomGateFloodUsed` per-player once-per-game guard; `CardsBanishedThisTurn` per-turn counter (Warpquartz pays on the TURN total); `ShardsCard.BanishAtCleanup` (Reactor Drone mode 2 banishes at END of turn, only when the source IS the drone — copies banish nothing). Sentinels: player 39, card 8.
- **Duel Dominion**: 3+ OTHER cards AND 3+ distinct factions, with the hand-REVEAL decision (reveals never feed PlayedThisTurn); Prism = all factions but ONE card; CountsAs/Yggdrasil honored everywhere (`ShardsDuel.DistinctFactionsPlayed` + `PlayedFactionCards` gates on the 3-faction destinies).
- **Testudo now IS "shields protect champions"**: champion hits deferred via `_pendingChampionHits` into the owner's defense step (ShieldFlow applies prevention per champion; transient engine field like _pendingDefenses). **Datic Robes M20 discard-shield implemented** (`DiscardPassiveShield` hook read in NextDefense's passive). Spore Cleric uses real `Unify` (no self-trigger); Riposte = played-or-REVEAL flow; Index of Futures = `ReorderCenterTop` (true any-order, first pick = top); Prism Qty 2; World Piercer options sorted (no deck-order leak).
- `ValidateAnswer` rejects duplicate option ids (except soi.split, whose duplicates are the mechanism).
- Bots: herodraft honors DefaultOptionIds (heuristic + value model); ScoreAction ranks hero ability just above / reroll just below END TURN; ShardsDecisionCandidates covers herodraft/mode/handpick/removeshop/defiant/scry; ISMCTS KeyOf distinguishes reroll slots + hero ability. SoiSim records DRAFTED characters, not scheduler defaults.
- UI: draft/reroll/ability events narrated (history + toasts); opponent portraits guarded during the draft; errata ArtId inherits ReplacesId (art regression fix) + CardArtIndex rebuilt. (The former `SoiCardFaces.DuelEnabled` character-face ability block was replaced 2026-07-25 by the `soiability:` card face.)
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
- **Static hooks on `ShardsCardDef`** (`ShardsTypes.cs`): `Taunt` (Zetta — the END-TURN
  split may reach the owner/other champions ONLY when the same answer assigns Zetta
  lethal — options carry `Required`/`Amount`/`OwnerIndex` UI hints and `SplitDamageFlow`
  drops assignments that violate the rule;
  power > 1000 skips the split entirely and kills every opponent instantly), `CanBeAttacked`
  (Li Hin / Raidian / Drakonarius), `DefenseAura` (Ferrata Guard, One Mind One Army),
  `CostModifier` (Axia), `ShieldInPlay` + `DynamicShield` (Praetorian-02, Datic Robes),
  `ExhaustGemCost` ("Pay N gems, Exhaust:" — the gems are part of the activation COST:
  the tap is ILLEGAL while unaffordable, LegalActions filters it, the engine pays on
  activation; effects never check gems. Shard Defiant 2, Whatever it Takes 6; UI greys
  unaffordable destinies), `ReturnsFromDiscardOnChampionPlay` (Praetorian-01),
  `OnDamageDealt` (Blood for Blood —
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
- **Duel hero abilities** — single source of truth is `ShardsEngine.HeroAbilityInfo`
  (costs, names, rules text); the effect body is `ShardsEngine.HeroAbilityEffect`. Every UI
  and the value model read those, so a cost change is made in ONE place — plus the two FR
  strings in `LocFrench.cs` and a Changelog entry.

  | Hero | Ability | Cost | Effect |
  |---|---|---|---|
  | decima | Recruiting | — | **passive**: first buy each turn costs 1 less (lives in `EffectiveCost`) |
  | tetra | Perception | **2 gems** | draw 1 |
  | volos | First Aid | 1 gem | gain 3 health |
  | kosynwu | Sacrifice | **2 gems + 3 health** | banish 1 from hand/discard |
  | rez | Futureproof | **free** | Scry 2 the center deck |

  ⚠ **Perception 3 gems → 2 (2026-08-02, user decision)** — at 3 the draw competed with
  a whole buy and was rarely worth it.
  ⚠ **Rebalanced 2026-07-27** (was: Sacrifice 3 gems, Futureproof 1 gem). Both abilities
  were measurably unusable — `soisim coverage` recorded **0 activations across 1,622 and
  1,583 drafted games**. Futureproof is designed to PAIR with the row reroll (bury a card
  that would help an Undergrowth-heavy opponent, or set up a reroll target), which is
  unaffordable if the Scry itself costs the reroll's gem. Sacrifice is a strong effect whose
  3-health cost genuinely is not worth it in a damage race, so the gem side gave way instead.
  Any further cost change must be re-checked with `soisim coverage` — an ability nobody can
  afford is invisible to win-rate testing, because both seats share the blind spot.
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
7. **Add/update the French entry** in `Assets/Scripts/Game/Soi/SoiFrenchCards.cs`
   (official IELLO terminology — see the localization skill). A new card without a
   FR name/text is a bug.
8. If the card needs art: the table regen (step 4) also exports its ArtPrompt to
   `Tools/ShardsData/art-prompts.json`; generate original art via the art-pipeline skill.

## Encoded rulings (each pinned by a test in ShardsRulingsTests / ShardsEngineTests)

- Staggered start mastery 0/1/2/3; cap 30; thresholds check AT PLAY/EXHAUST time and a
  card's own mastery gain counts for its own threshold (Fungal Hermit / Cache Warden).
- Champions can be damaged/destroyed ONLY in the attacker's end-of-turn damage
  assignment or by destroy-EFFECTS (user decision 2026-07-20 — mid-turn power attacks
  are illegal and never advertised; Ingeminex are the only mid-turn power targets).
  Damage marks evaporate at end phase; destruction needs full (effective) defense
  assigned in one split. `CanBeAttacked` vetoes (Li Hin/Raidian/Drakonarius) apply to
  the split's target list.
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
  Deadly Recruits' fast-play is NOT warp — base destiny: the card is always kept
  (discard at cleanup); **duel errata "you may keep it" is a real keep-or-not decision**
  (2026-08-02 fix — `soi.keepfast`, default/bots = keep; declined, the card follows
  fast-play rules to the bottom of the center deck).
- End phase order: fast-plays → center-deck bottom, play zone → discard, discard hand,
  ready champions/destinies/character (readying happens at END phase, not turn start),
  draw 5 (+ Heart of Nothing bonus), pools/turn-flags reset. Mid-draw reshuffle: never
  deck out.
- Entropic Talons converts health gains to POWER (photo-verified; the "mastery" claim
  in early notes was wrong) and fizzled at-cap heals still count.
- Copying an effect (Ojas/Taur/Duplication Fabricator) is NOT playing the card: no
  faction counts, no play triggers; Cinder Scars' pair bonus needs a REAL second copy.
- A card can be COPIED only once per resolution chain (`ShardsContext` copy-chain,
  locked 2026-07-21): a played Fabricator copying the revealed second Fabricator
  re-reveals the same unchanged deck tops → without the guard this recursed forever
  (stack overflow, found by a 3000-game bench sweep). Both copy sites filter on
  `ctx.InCopyChain` and call `ctx.MarkCopied`. Pinned by
  `DuplicationFabricator_CopyingTheRevealedSecondFabricator_CannotRecurse`.
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
- Shard Defiant's keep-or-banish is MANDATORY (user decision 2026-07-19, replacing the
  earlier may-decline reading — the 2 gems are a real activation cost now, so a paid
  activation always resolves). Both options carry the revealed card's DefId/InstanceId
  so the UI renders the CARD (rule: decisions must never reference a card by name
  only). Pinned by ShardDefiant_GemPaymentIsAnActivationCost….
- Numeri Drones / Anomaly Cleric redirects are COUNTERS (two exhausts = two redirects).

## Known simplifications (revisit in M8 polish)

- Multi-defender shield order: clockwise from attacker (rulebook silent; outcome-equivalent).
- Several bottom-of-center-deck returns stack in play order (rulebook silent).
- Giga's Dominion-gated exhaust: activating with the condition unmet wastes the exhaust
  (effect fizzles) rather than being illegal.
- Ingeminex Malice's "highest-cost champion" tie-break: deterministic (lowest instance
  id) instead of owner's choice.
- Nemesis solo variant, co-op campaign, Shadow Summoning Draft, RotF table variants
  (Bloodbath/Auction/2v2/drafts): out of scope by user decision.
