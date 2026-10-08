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
  5 Ingeminex + 30 destinies), `ShardsDuelSet.cs` (**Duel of Doom** — 21 new defs + ~44
  errata replacement defs). `ShardsContentRegistry.EnsureRegistered()` registers all.

### Controller-visible effect predicates

`If.Visible` and `PerCount.Visible` preserve the existing rule effect and expose
an author-reviewed visibility contract for AI gain previews. The 46 direct
predicates/counters in the five sets, plus Inspire/Echo/Character/FullHealth,
read only the acting controller's visible state: health/mastery, public
champions/discard/played cards, faction counts, opponent mastery, or visible
Ingeminex. No rules text, costs, or effect amounts change. Unknown `new If` and
`new PerCount` delegates remain opaque. Never mark a callback that reads enemy
hands, private deck order, or unrevealed center/destiny cards as visible.

`PerCount.VisibleCount` exposes an exact count only for the same reviewed visible
callbacks and rejects opaque ones. `Do.RecruitRouting` annotates Numeri Drones'
existing increment of `NextHomodeusChampionsIntoPlay`: it has no immediate cost or
hidden read and expires at cleanup. This lets the AI recognize its free gem before
ending a turn without executing an unknown callback. The effect, rules text and
amounts are unchanged. Never use this annotation for health costs, draws, events,
or mutations beyond the controller's turn-local recruit routing counters.

## Current Duel balance — 2026-10-08

This section supersedes older Duel values and Testudo/combat rulings below.
The reviewed source is `Tools/BalanceReview/balance_proposals.json`, version
`2026-10-08-v5`. Existing base/expansion definitions keep their non-Duel behavior;
nine new `_duel` replacements carry changes that previously had no Duel printing.

| Card / ability | Current Duel change |
|---|---|
| Giga, Source Adept | Dominion exhaust gains 2 mastery; still cost 2, defense 4, draw on play. |
| Deadly Recruits | Pay 1 gem as activation cost, then choose fast-play OR recruit. Target cost 2, M20 cost 4. No additional purchase payment. |
| Multitask Brain | 1 power and 1 draw per distinct faction; M20 remains 4 power and 1 draw per faction. |
| Panconscious Crown | Resolve 2 mastery, 5 health, draw 1, **then** M20 Unify for 50 health. The drawn card can satisfy Unify. |
| Unconditional Conscription | 5 power; preserve the existing count of 2+ non-starter non-champions costing at most 2, including eligible relics. |
| Soul Syphon | 2+ actual faction-bearing played cards spanning 2+ factions give 5 health. One Prism alone fails. |
| Furrowing Elemental | Cost 4; effects unchanged. |
| J-Chord | Cost 4; Warp 3 / M15 Warp 6 unchanged. |
| Shard Abstractor | Cost 2; gain 1 mastery, or 2 if already M10 before the gain. Starting at 9 reaches 10. |
| Volos | One-gem mode grants 3 power; the other modes are unchanged. |
| Order Initiate | Dominion gains 1 mastery; optional market removal and 2 gems unchanged. |
| Shard Seer | Infinity Shard reveal grants 1 mastery; draw happens first. |
| Duplication Fabricator | Cost 4; copied effects and reveal provenance unchanged. |
| Breaker | Warp 6; shield 4 and recruit-to-hand retained. |
| Fungal Hermit | Add hand Shield 2; its own mastery gain still counts for M10 healing. |
| Ingeminex: Corruption | Defense 15, including Doom Gate-created copies. |
| Omnius | Dominion gains 3 mastery; draw 2 unchanged. |
| Systema A.I. | Cost 4; exhaust unchanged. |
| Testudo Vanguard | Each actual shield-card play grants +1 temporary defense to every current controlled champion, until the start of its owner's next turn; exhaust still gives 2 gems. |

Three new market definitions, **two copies each**:
- `horizon_seeker`: Order ally, cost 2; draw 1, then if strictly behind an opponent
  in mastery gain 1. Each play reevaluates the condition; ties fail.
- `riftbreaker`: Wraethe mercenary, cost 3; 5 power and, while strictly behind,
  optional banish 1 from hand/discard.
- `rift_scout`: Aion **mercenary**, cost 2; 3 power and conditional Warp 2 while
  strictly behind. Its own acquisition/fast-play removes it from the row first.

`dna`: neutral destiny, quantity 1, **ExhaustGemCost = 4**. Activating arms a copy of
this turn's next recruitment. Fresh extra instances enter discard, bypass routing
and play effects, and do not recursively recruit. Real free recruits and relic
recruits qualify; fast-play, Warp and returning owned cards do not. Multiple pending
DNA effects copy the same next recruit; unused effects expire during cleanup.
`ShardsHorizonSet.CorruptionReward` calls `NotifyRecruit` after granting the relic.
The rejected Relief Courier and Aegis Surveyor are absent; no replacements added.

**Testudo timing:** `ChampionDefensePerShieldPlay = 1`; live positive shield value
qualifies, including dynamic/granted shields. Actual hand play, fast-play and Warp
trigger; revealing or copying effects does not. Each Testudo stacks independently.
The grant persists if Testudo leaves; a receiving champion loses its grant on leaving
play. Later champions receive no earlier grant. This is defense, unaffected by shield
bypass and irrelevant to direct destruction.

**Duel combat and opening shop:** initial six slots allow printed cost at most 5
plus Comet; later refills are unrestricted. Spend full remaining effective defense
to attack champions between actions, resolving destruction immediately. Zetta guards
remain attackable; each protects its owner and non-guard champions. End-turn power
attacks players only. Ordinary hand shield reveals protect players only. Non-Duel
keeps its previous end-turn champion split. Mastery-30 Infinity Shard bypasses Zetta
and automatically damages every opponent at end turn. Comet also bypasses Zetta:
it directly destroys one opponent, with a target choice only when several remain.

## Reveal rule (engine-wide, 2026-07-26)

Any effect that REVEALS from a player's deck pulls through `ShardsEngine.PeekTopOfDeck`,
which shuffles the discard back in when the deck runs dry mid-reveal; if both are empty it
reveals what exists. Reveals are never optional ("you may reveal" is gone) and the pick
window ALWAYS opens listing EVERY revealed card — the ones that don't qualify carry
`DecisionOption.Disabled` (shown, greyed, and rejected by `ValidateAnswer`), so the player
sees the whole reveal and passes deliberately. `ShardsHorizonSet.RevealTopForChampion` is
the shared implementation (Legion Carrier 3 / errata 5).

### Reveal provenance for current-state policies

`ShardsCardsRevealedEvent.PersonalTopPlayers` and `PersonalTopInstanceIds` are
parallel to the publicly shown `DefIds` for Fabricator and Gatekeeper.
`RemovedFromPersonalTop` distinguishes Legion Carrier's temporary reveal window.
Center-top takes carry parallel `CenterInstanceIds`; personal and center provenance
is emitted only for cards the effect has already publicly shown. Hand reveals,
including Shard Seer and Riposte Doctrine, set `FromHand`.
Hand reveals and selected hand shields carry parallel `HandInstanceIds` for only
the publicly shown cards. Preserve these IDs in Unify, both Dominion paths,
Shard Seer, Riposte and shield resolution so a different same-definition copy's
later movement does not erase knowledge of the card still held.
Maintain these annotations when changing a reveal flow so AI memory can retain
known deck positions and cards temporarily held outside the state's zone lists.
Rules effects, draw order and RNG calls are unchanged by these metadata fields.
`ShardsCenterCardBottomedEvent` covers paid and free market removal before refill;
`ShardsCenterDeckShuffledEvent` invalidates remembered order before later reveals.
Longshot's publicly revealed bottom returns carry `ShardsMercenaryReturnedEvent.InstanceId`;
preserve each physical identity so several returned cards retain their known order.
Legion Carrier's resolved nonselected cards emit parallel public IDs/definitions in
`ShardsRevealedCardsDiscardedEvent` before any subsequent effect can reshuffle them.
This retires iterator-held reveal records chronologically, including partial
repeated reveals; the selected champion's existing hand-return event stays separate.
Copied/replayed public effects use `ShardsContext.ResolvePublicPlayEffects` so
selected targets, recursion guards and remaining repetitions survive child menus.
Use `markCopied: true` only where the existing copy recursion guard applies;
Warpquartz and Decurion replays keep `false`. Preserve these public annotations
when changing the corresponding flow; see the focused ZeroDepth host selftests.

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
faction — deck+hand+discard+play+champions, counts itself and temporary fast-plays in
the play zone, confirmed 2026-09-25; collection `FullDeck` still excludes loans), `Scry`,
`OpponentDrawsThenDiscards`, `ShardsDuel.DistinctFactionsPlayed`. Engine hooks on
`ShardsCardDef`: `CountsAsEveryFaction` (Prism), `CannotBeRerolled` (Comet),
`KeepFastPlaysAtMastery` (Swyft M10), `ImmuneToIngeminex` (Doom Gate),
`DoublesExhaustsAtMastery` (Unknown God M20). Player flags: `HealingDoubledThisTurn`,
`OverflowHealthToPowerThisTurn`, `ShieldsDoubledUntilNextTurn` (Praetorian-02, cleared in
StartTurn), `HeroAbilityUsedThisTurn`, `FirstBuyUsedThisTurn`, `NextChampionsIntoPlay`.
**Dominion reworked when Duel on** (3+ other cards of 3+ different factions; else base
H/U/W) — flag-gated inside `Dominion`.

**Row reroll** (repriced 2026-07-25): `ShardsRerollRowAction` — pay
`ShardsEngine.RerollCost(player)` = **max(0, 1 + RerollsThisTurn - NextRerollDiscount)** (climbs per use, resets each
turn; counter on `ShardsPlayer`, snapshotted) → `BottomRowCardAndRefill`
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

**Volos rework (updated 2026-09-18)**: First Aid still requires M5 and is once per turn,
separate from Focus. Activation is free and opens `soi.volos`: heal 3 for free,
pay 1 gem for 2 power, pay 2 gems to draw 1, or pay 3 gems for 1 mastery.
All four `soivolos:<mode>` cards are shown; unaffordable modes are disabled.
`VolosAbilityChoice` owns mode costs/text/effects; `SoiCardFaces` renders the cards.

**2026-09-28 balance** (supersedes earlier values): 1v1 seat 1 opens with
5 cards, 1 mastery and 1 crystal for its first turn only; normal cleanup removes
unused crystals. Decima's M5 first-buy discount is 2. Terminal Crescents (Duel)
gains 1 mastery, then power equal to half mastery rounded up below M20, or mastery
minus 5 at M20. Deadly Recruits (Duel) chooses free fast-play OR free recruit,
not both (ally cost up to 2, or 4 at M20). Swyft (Duel) defense 5. Ferrata Guard
(Duel) exhaust gains 1 base crystal plus 1 per Homodeus champion controlled.
Torian Commandos gives 3 crystals + 2 power. Le'shai Knight gives 4 power, 6 total
with Unify. Advanced Medicine heals 6 under its unchanged condition. Power Struggle
gives 6 power. Doom Gate floods 35 Ingeminex, still once per player per game and
still defense 7. Warpquartz (Duel) draws 1 BEFORE choosing banishes; copied effects
still resolve twice. Datic Robes (Duel) discard shield starts at M15.
Entropic Talons, Praetorian-01 and Nil Assassin remain unchanged.

**2026-09-27 balance** (supersedes the September 18 values below): in 1v1 seat 1
opens with 6 cards and 1 mastery; later hands still draw 5. Tetra's M5 activation
costs 3 gems; Ko Syn Wu's costs 1 health. Rez's M5 activation is Scry 3, with a
separate M5 passive reducing EVERY reroll by 1 (prices 0, 1, 2, ... without activation).
Duel Warpquartz resolves each of its banished cards' play effects twice, sequentially;
its 3-gem/3-power bonus per card banished this turn is unchanged and awarded once.
Doom Gate defense 7. Praetorian-02 in-play shields 4 / 8 at M20. Duel Cinder Scars
quantity 4 instead of 5. Non-Duel card definitions are unchanged.

**2026-09-18 balance** (source: `Tools/CardDesigner/soi-design-session-2026-09-18.json`):
Duel-only. `panconscious_crown_duel` replaces the original and heals 5 (original stays 2);
Comet costs 13; Testudo defense 4; Praetorian-02 activation 2 gems; Praetorian-03 top tier M20;
Unknown God defense 6; Multitask Brain loses Dominion; Doom Gate defense 5/flood 25;
Heart of Nothing power 7/14; World Piercer optionally returns up to two mercenaries
(M20 still returns all). Rez's Scry 2 also discounts the next successful reroll this turn
by 1, then consumes the discount; unused discount expires at turn cleanup.
CardDesigner export now has 189 definitions + 10 character/ability faces.

**Original new defs (20, plus the four October additions above)**: relics praetorian_03/multitask_brain/unknown_god/star_seeker/doom_gate
(one per hero); cards testudo_vanguard, century_forge, riposte_doctrine, index_of_futures,
bulwark_chanter, aegis_archivist, thornshell_warden, nectar_alchemist, lifebloom_ritual,
doomstalker, bleak_communion, grim_tutor, comet, prism, longshot.
**whisper_extractor REMOVED 2026-08-02** (user decision: too strong even after the
2026-07-25 redraw nerf). Gone with it: the `OpponentDrawsThenDiscards` effect class, the
`soi.handpick` context and the two FR decision-title patterns. The art png/CardArtIndex
entry remain on disk until the next
`Rebuild Card Art Index` (editor-only).
**grim_tutor** (2026-07-25, Wraethe Mercenary, cost 3, qty 2): Custom flow — decision over
the player's DECK sorted by DefId/InstanceId (never deck order — the World Piercer
anti-leak rule), chosen card to hand, `Rng.Shuffle(deck)`, `LoseHealth(3)` (a loss, not
damage; applies even with an empty deck). Context `"soi.tutor"`. **Errata (~44
`<id>_duel` defs)**: 4 Allegiance conversions (ferrata/mainframe/hounds/the_lost) + ~16
stat tweaks + 5 hook + 12 bespoke + 7 destiny — all in `RegisterErrata` sub-methods.

**Historical Testudo rule:** the July shield-copying rework was replaced by the
October 8 temporary-defense trigger documented above.

**Ingeminex rewards**: destroying one by CARD EFFECT is defeating it — `DestroyActiveMonster`
takes the destroyer's seat index and queues `RewardEffect` just like the attack path does
(Doom Gate paid nothing at all before 2026-07-27). Pass -1 only for a kill that belongs to
nobody. Doom Gate floods **35** Ingeminex (2026-09-28; previously 25).

**Testing**: `Duel_*` tests in `ShardsContentTests` cover draft, errata swaps, reroll, hero
abilities and Decima discount.

**Adversarial review pass (2026-07-24, 125-agent workflow, 37 confirmed findings — ALL fixed, 220 tests green):**
- `CannotBeFastPlayed` def flag (Comet) enforces normal-purchase-only acquisition in
  WarpUpTo/WarpFromRow/FastPlayLoose/BuyCard and free recruitment
  (`RecruitFromRow` options, `RecruitFromRowFree`, `RecruitLoose`). Shard Defiant shows
  Comet with Keep disabled and Banish as the legal timeout default. Normal purchase
  discounts/redirects and moving an already-owned Comet remain legal (2026-09-25 ruling).
- `DoomGateFloodUsed` per-player once-per-game guard; `CardsBanishedThisTurn` per-turn counter (Warpquartz pays on the TURN total); `ShardsCard.BanishAtCleanup` (Reactor Drone mode 2 banishes at END of turn, only when the source IS the drone — copies banish nothing). Sentinels: player 41, card 8.
- **Duel Dominion**: 3+ OTHER cards AND 3+ distinct factions, with the hand-REVEAL decision (reveals never feed PlayedThisTurn); Prism = all factions but ONE card; CountsAs/Yggdrasil honored everywhere (`ShardsDuel.DistinctFactionsPlayed` + `PlayedFactionCards` gates on the 3-faction destinies).
- **Historical July Testudo shield copying is superseded by the October rule above.** **Datic Robes M15 discard-shield implemented** (`DiscardPassiveShield` hook read in NextDefense's passive). Spore Cleric uses real `Unify` (no self-trigger); Riposte = played-or-REVEAL flow; Index of Futures = `ReorderCenterTop` (true any-order, first pick = top); Prism Qty 2; World Piercer options sorted (no deck-order leak).
- `ValidateAnswer` rejects duplicate option ids (except soi.split, whose duplicates are the mechanism).
- UI: draft/reroll/ability events narrated (history + toasts); opponent portraits guarded during the draft; errata ArtId inherits ReplacesId (art regression fix) + CardArtIndex rebuilt. (The former `SoiCardFaces.DuelEnabled` character-face ability block was replaced 2026-07-25 by the `soiability:` card face.)
- **Full card table** (id / name / set / faction / type / cost / qty / def / shield / text):
  `Tools/ShardsData/cards-table.md` — REGENERATE, never hand-edit:
  `cd Tools/EngineVerify && dotnet test --filter ExportShardsCardTable`
  (also seeds `Tools/soi_art_sources.json` if missing).
- **Effect vocabulary**: `Assets/Scripts/Shards/Engine/ShardsEffects.cs` — `Gain`,
  `E.Seq`/`ShardsComposite` (sequential — own mastery gain precedes later thresholds),
  `E.At`/`AtMastery` (ADDITIVE delta: "3, M10: 6 instead" = base 3 + At(10,+3)),
  `BestByMastery` (true "instead" tiers), `If` (+`Inspire`/`Echo`/`Character`/`FullHealth`),
  `Unify` (another CARD of the faction played OR automatic first matching hand reveal;
  champions count since 2026-08-23; reveal is automatic since 2026-09-16),
  `Dominion` (played/reveal one of EACH of H/U/W), `PerCount`, `OpponentLosesMastery`,
  `BanishUpTo`, `ReturnFromDiscard`, `DestroyEnemyChampions`, `WarpUpTo`, `RecruitFromRow`,
  `CopyPlayedEffect`, `AllPlayersLoseHealth/LoseMastery/Discard/DestroyBiggestChampion`,
  `Custom`/`Do`.
- **Static hooks on `ShardsCardDef`** (`ShardsTypes.cs`): `Taunt` (Zetta — the END-TURN
  split in non-Duel may reach the owner/other champions ONLY when the same answer assigns Zetta
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
  | tetra | Perception | **2 gems** | draw **2** |
  | volos | First Aid | **free** | gain **4** health |
  | kosynwu | Sacrifice | **3 health** | banish 1 from hand/discard |
  | rez | Futureproof | **free** | Scry 2 the center deck |

  ⚠ **Rebalanced 2026-08-23 (user decision)**: Perception draws 2 instead of 1 (the gem
  cost stays 2); First Aid 1 gem → 0 and 3 → 4 health; Sacrifice 2 gems → 0, the 3 health
  is now the whole price. All three lost to "just buy a card" at their old prices.
  ⚠ **Perception 3 gems → 2 (2026-08-02, user decision)** — at 3 the draw competed with
  a whole buy and was rarely worth it.
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
- **Non-Duel:** champions can be damaged/destroyed ONLY in the attacker's end-of-turn damage
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
- **Unify needs ANOTHER CARD of the faction — CHAMPIONS COUNT (user decision
  2026-08-23)**, played this turn or revealed from hand; self never counts. A champion
  already IN PLAY satisfies nothing (Unify wants a play or a hand reveal). This is a
  deliberate deviation from the printed "Ally" reminder text, recorded in
  `rules-notes.md`'s deviation list; `Unify` reads `FactionPlays`, not `FactionAllyPlays`.
  Dominion needs one card of EACH of H/U/W (played and/or revealed).
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
  (2026-08-02 fix — `soi.keepfast`, default = keep; declined, the card follows
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
  (stack overflow). Both copy sites filter on
  `ctx.InCopyChain` and call `ctx.MarkCopied`. Pinned by
  `DuplicationFabricator_CopyingTheRevealedSecondFabricator_CannotRecurse`.
- Slipstream Shard M20: extra turn, once per game per player.
- Full power assignment at the attack phase is MANDATORY (Min=Power on the split
  decision; DefaultOptionIds pre-fill a legal full assignment for timeouts).
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
