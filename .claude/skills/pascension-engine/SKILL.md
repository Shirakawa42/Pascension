---
name: pascension-engine
description: Pascension rules-engine architecture — stack, APNAP priority, iterator effects, decisions, zones, cleanup order, how to add effects/triggers/keywords/actions — plus the user-approved design decisions log (rules source of truth together with Assets/GDD.txt). Use before modifying anything under Assets/Scripts/Engine, writing Pascension card effects, or answering why a Pascension rule works the way it does.
---

# Pascension Rules Engine

Assembly `Pascension.Engine` (`Assets/Scripts/Engine/`) is pure C# — **no UnityEngine**. It must stay deterministic, headless-runnable, and single-threaded. (SoI does NOT use this engine — see the shards-engine skill.)

## Execution model (the one thing to internalize)
- The engine is a sequential machine: `GameEngine.Submit(PlayerAction)` → validate (`ActionValidator`) → mutate → **fixpoint loop** (`collect triggers → state-based actions → resolve/advance → grant priority`) until the engine needs external input again.
- At any moment exactly ONE input is pending: `PriorityInput(playerId, legalActions)` (a player holds priority) or `DecisionInput(playerId, DecisionRequest)` (mid-resolution choice). Check `GameEngine.PendingInput`.
- Every mutation emits `GameEvent`s into `EventLog` (sequence-numbered). UI/bots/network consume events; hidden info is redacted per-player ONLY via `EventLog.FilterFor(player)` / `SnapshotBuilder`.
- **Effects are iterators**: `IEnumerable<EngineStep> Resolve(EffectContext ctx)`. To pause for a choice: `yield return EngineStep.AwaitDecision(request);` then read `ctx.Answer`. **Never block, never recurse into `Submit`** from inside an effect. `ResolutionEngine` keeps a stack of live enumerators, so effects can cast sub-spells (reflexive "you may cast" like Random Bullshit Go) that resolve nested.
- The stack (`GameStack`) holds spells (playing any card) and abilities (activated/triggered/damage-assignment). Spells are counterable; abilities are not. Priority is APNAP; `PriorityController` fast-passes players whose only legal action is Pass (unless their full-control flag is on).
- Timing: instants — any time their controller has priority; everything else — own main phase, empty stack. Buying and moving are off-stack special actions (buys still emit `CardBought` for triggers); **damage assignment IS on-stack** so barriers can respond.

## Key terms
- GDD card type "nothing" = `CardType.Action` in code; the card's type line displays "Nothing".
- "AP" = action points. Damage pool + AP clear at end of the active player's turn (for all players).
- Keyword **Ethereal**: exiled (not discarded) at cleanup if still in hand.
- Cards played this turn live in the `PlayedThisTurn` zone until cleanup (GDD reshuffle rule).

## Respondability (2026-07-08 user reversal — invariant)
**Everything is respondable**: every play and tap uses the stack and opens response windows. The `IsManaAbility`/`ManaAbility` engine flags REMAIN as a rare explicit opt-out for future cards — any card using them MUST say "can't be responded to" in its rules text (pinned by `RespondableInvariant_NoCardOptsOut_WithoutSayingSoInItsText`). No current card is flagged; `CardPlayedEvent` + its showcase path stay wired but dormant.

## Determinism rules (breaking these breaks replays & multiplayer)
- Randomness only via `GameState.Rng`. Never `System.Random`, `Guid.NewGuid`, `DateTime`.
- Never iterate `Dictionary`/`HashSet` where order affects state; use `List` or sort by id.
- Replay check: same seed + same action sequence ⇒ identical `GameState.ComputeHash()`.

## How to add things

**A new effect class** (`Assets/Scripts/Content/Effects/` for one-offs, `Engine/Effects/Common/` only if generic):
```csharp
public sealed class MyEffect : IEffect
{
    public IEnumerable<EngineStep> Resolve(EffectContext ctx)
    {
        var options = ctx.State.ActivePlayer.Hand.Cards; // build choices
        yield return EngineStep.AwaitDecision(DecisionRequest.ChooseCards(ctx.Controller, options, min: 0, max: 1, "Exile a card"));
        foreach (var id in ctx.Answer.CardIds) ctx.MoveCard(id, ZoneType.Exile);
    }
}
```
Then use it from a card builder entry. Effects must be stateless (all state in ctx/GameState) — instances are shared between card copies.

**A triggered ability**: builder `.Triggered(EventFilter.MonsterKilledByYou, new GainXp(1))` — filters live in `Engine/Effects/TriggeredAbility.cs`. Triggers collected after each event batch, pushed onto the stack in APNAP order.

**An activated ability**: builder `.Activated(Cost.Tap, new GainDamage(1))` or `Cost.Ap(2)`; hero actives add `Cost.OncePerTurn`.

**A continuous effect**: `ctx.AddModifier(Modifier.MonsterHp(target, +3, Duration.EndOfTurn))` — stored timestamped in `ContinuousEffects`; effective values are computed by folding modifiers, never by writing base stats.

**A keyword**: add to `KeywordSystem` (e.g. Ethereal = cleanup replacement: exile instead of discard) and expose via builder `.Keyword(Keyword.Ethereal)`.

**A new PlayerAction or GameEvent**: add the DTO + register it in `EngineJson`'s whitelist type registry (wire format uses `"t"` discriminators) + handle in `ActionValidator`/`LegalActionGenerator`.

## Zones
Deck, Hand, Discard, Exile, **PlayedThisTurn** (cards played this turn; moved to Discard at cleanup), EquipmentSlots (weapon/armor/trinket — replacing exiles the old one), RelicRow, MarketRow/piles, Stack. All movement via `ctx.MoveCard`/`Zone` APIs (emits events).

## Turn/cleanup order (End phase)
end-of-turn triggers → ethereal exile (unplayed hand cards) → discard hand → PlayedThisTurn → discard → clear ALL players' AP + damage pools + monster marked damage → expire EOT modifiers → draw 5 (reshuffle discard if short) → hero end-of-turn passives (Nyx keeps cards before discard).

## Testing
Edit-mode NUnit in `Assets/Tests/EngineTests/`. Use `TestGameFactory` (seeded, scripted piles/decks) — never rely on unseeded shuffles. Every new card/effect gets at least one test. Run via Unity Test Runner (MCP) or `Tools/EngineVerify` dotnet. See pascension-balance skill for full-game sims.

## Design decisions log (source of truth, user-approved 2026-07-08+)

Rules source of truth = `Assets/GDD.txt` + this log. Networking/UI decisions here are elaborated in the networking and ui-presentation skills.

No player HP (PvP via effects only) · monsters live in market rows, their damage resets each turn · XP from kills/effects only, curve 2,2,3,3,4,4,5,5,6 · boss burst race on step 50 · inns 10/20/30/40 = choose 1 of {+2 XP, draw 2, exile ≤2 from discard} + move-back checkpoint · 4 heroes (Ignis/Wren/Cornelius/Nyx; passive L1, active L3, upgrade L6, ult L9) · NGO host mode; internet play via **Unity Relay join codes as game IDs** (2026-07-10; no port forwarding, no matchmaking) · **mid-game disconnect PAUSES the game** (overlay + rejoin by game ID, or host kicks → bot takes the seat permanently; host loss = game over) · Arena-style auto-pass priority + full-control toggle (NO response timer — players take unlimited time, changed 2026-07-08; `GameRules.ResponseTimerSeconds=0`) · **everything is respondable** (mana-ability rule reversed 2026-07-08; future opt-out cards must say "can't be responded to" in their text) · **speculative fast-play with rollback** (effects/reveal only on validation; opponents prompted per card) · single-screen 2D tabletop · painterly-anime full-art cards (Anima via ComfyUI) · staggered start (P2 +1 AP; P3/P4 +1 AP +1 card) · target 30-45 min · basic CC0 audio · title "Pascension".
