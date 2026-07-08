---
name: rules-engine
description: Pascension rules-engine architecture — how the stack/priority/effects/decisions work and how to add new effects, triggers, keywords, or actions. Use before modifying anything under Assets/Scripts/Engine or writing card effects.
---

# Rules Engine Guide

Assembly `Pascension.Engine` (`Assets/Scripts/Engine/`) is pure C# — **no UnityEngine**. It must stay deterministic, headless-runnable, and single-threaded.

## Execution model (the one thing to internalize)
- The engine is a sequential machine: `GameEngine.Submit(PlayerAction)` → validate (`ActionValidator`) → mutate → **fixpoint loop** (`collect triggers → state-based actions → resolve/advance → grant priority`) until the engine needs external input again.
- At any moment exactly ONE input is pending: `PriorityInput(playerId, legalActions)` (a player holds priority) or `DecisionInput(playerId, DecisionRequest)` (mid-resolution choice). Check `GameEngine.PendingInput`.
- Every mutation emits `GameEvent`s into `EventLog` (sequence-numbered). UI/bots/network consume events; hidden info is redacted per-player ONLY via `EventLog.FilterFor(player)` / `SnapshotBuilder`.
- **Effects are iterators**: `IEnumerable<EngineStep> Resolve(EffectContext ctx)`. To pause for a choice: `yield return EngineStep.AwaitDecision(request);` then read `ctx.Answer`. `ResolutionEngine` keeps a stack of live enumerators, so effects can cast sub-spells (reflexive "you may cast" like Random Bullshit Go) that resolve nested.
- The stack (`GameStack`) holds spells (playing any card) and abilities (activated/triggered/damage-assignment). Spells are counterable; abilities are not. Priority is APNAP; `PriorityController` fast-passes players whose only legal action is Pass (unless their full-control flag is on).
- Timing: instants — any time their controller has priority; everything else — own main phase, empty stack. Buying and moving are off-stack special actions (buys still emit `CardBought` for triggers); **damage assignment IS on-stack** so barriers can respond.

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
Edit-mode NUnit in `Assets/Tests/EngineTests/`. Use `TestGameFactory` (seeded, scripted piles/decks) — never rely on unseeded shuffles. Every new card/effect gets at least one test. Run via Unity Test Runner (MCP) or batchmode CLI. See playtesting skill for full-game sims.
