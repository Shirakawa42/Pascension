# V8 damage abstraction audit

The training adapter now removes positive assignments below a champion's publicly announced remaining defense. It uses a kill-or-skip decision for ordinary champions and retains every at-least-lethal amount against Testudo, where over-assignment can pay through shields. The engine rules are unchanged.

## Why this is justified

The engine accumulates champion damage, destroys at effective defense, and otherwise emits only a damage event ([ShardsEngine.cs:2033](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L2033)). All remaining champion marks disappear during the same end-turn cleanup ([ShardsEngine.cs:1515](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1515)). A partial end-turn hit therefore has no durable payoff in the present rules. This claim does not generalize to hypothetical mid-turn sequences followed by more damage.

The existing human interface already uses exact-lethal toggles for ordinary champions and freeform assignment for Testudo ([SoiDecisionModal.cs:624](../../../Assets/Scripts/Game/Soi/SoiDecisionModal.cs#L624)). V8 removes the AI-only burden of learning these arithmetic constraints.

The public decision contract explicitly publishes `Amount` as remaining effective HP and `Required` as the taunt gate ([DecisionRequest.cs:28](../../../Assets/Scripts/Core/DecisionRequest.cs#L28)). The adapter consumes those values. It does not evaluate opponent defense callbacks, read an opponent hand, or predict hidden shields.

## Implementation and retained choices

[HostV8/Adapter.cs](../experiments/HostV8/Adapter.cs):

- Required taunt targets are processed first, then other champions, then the enemy player.
- Ordinary champion choices are zero or exactly the published remaining defense.
- With a publicly deployed Testudo, zero and every amount from published remaining defense through available power remain reachable.
- Other targets behind an unassigned taunt are automatically assigned zero.
- The final unblocked player automatically receives remaining power. Unassignable residual is dropped only when the engine request permits it (`Min=0`).
- Sparse legal values are partitioned by rank. Binary branches cannot get stuck across the gap between zero and lethal.
- Generic synthetic split fixtures and non-1v1 adapters retain their original general allocation machinery. The requested training mode is 1v1.

The first taunt gates its owner's other targets, matching the engine's `Champions.Find(c => c.Def.Taunt)` semantics ([ShardsEngine.cs:1175](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1175)). The existing engine resolves Testudo shield reductions and taunt survival after the assignment ([ShardsEngine.cs:1220](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1220)). An announced lethal assignment can therefore still fail after secret shields are revealed; that uncertainty is intentional.

## Verification

`DamageSelfTest.Run()` passed **28,064 checks** in an isolated .NET build, and passed again after integration of the supplementary information tracker hooks:

- Exhausted every interval subrange with thresholds 1–12, bounds 0–14, branch counts 2/3/7/64, both exact-lethal and overkill domains.
- Exhausted 42 complete allocation trees, comparing every reachable answer with an independently enumerated expected set: zero/insufficient/exact/excess power, two or three champions, taunt, Testudo, residual face damage.
- Verified no positive sublethal assignment, no duplicate allocation path, no empty menu, no staged loop, and valid engine decision bounds.
- Exercised actual engine damage resolution for Testudo shield piercing, shielded taunt survival blocking all damage behind it, and sufficient taunt overkill opening the other targets.

The same deterministic two-champion safety fixture (power 3, defenses 3 and 2) failed against a copy of the immutable V6 adapter with `No positive sublethal champion allocation`. This supplies a concrete red reproduction. No old host executable or pinned source was modified. A test-only unused method signature was added to the temporary V6 copy solely to compile the shared test file; it was never called.

## Other automatic mechanics: safe boundary

Automatically completing a decision with exactly one legal completion is safe as a computation shortcut; optional choices with a decline button are not single-completion decisions. In V8 this principle is applied to damage residuals and unavoidable zero assignments.

Premature exhaustion is not covered by that proof. The engine can double an exhaust effect, and its legal action check does not promise positive immediate value ([ShardsEngine.cs:723](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L723)). Exhaustion also competes with reset, copying, later allegiance, mastery, health and resource interactions. Removing every currently inactive ability globally needs a card-by-card equivalence proof. This audit does not introduce that restriction.

Revealing all shields is also not purely arithmetic: shield cards stay in hand and a reveal is public ([ShardsEngine.cs:1345](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1345)). The player may trade damage prevention for information concealment. The damage fix leaves that decision intact.

Damage minimization does not dictate which champion should die. V8 keeps that strategic choice for the policy, including spending no power on champions and attacking the player when taunt permits it.

## Additional proven automation: copied Reactor Drone

V8 also automatically chooses three gems for the exact Reactor mode menu when the currently resolving source is an authorized visible card other than `reactor_drone_duel`. [The effect implementation](../../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs#L520) gains three gems and marks a card for banishment only if its resolving source is the physical Reactor Drone in the controller's play zone. A copy with another source therefore gets the extra gem without a banish cost. [GainGems](../../../Assets/Scripts/Shards/Engine/ShardsEngine.cs#L1860) only increments resources and emits the normal event; it has no negative gain trigger.

The guard matches context, title, decision kind, minimum/maximum, both option IDs and labels, and enabled status. Null or unauthorized hidden sources never authorize automation. Physical Reactor keeps both options, including a Reactor already marked for cleanup; no additional dominance claim is needed for that case.

Submissions use the ordinary accepted-action path, retain knowledge hooks and engine events, and count as engine submissions. They do not consume policy decisions. The automatic chain iterates instead of recursively rebuilding itself. Existing statistics callbacks read the complete engine log span, so emitted mode/gem events remain available.

`AutoChoiceSelfTest.Run()` passed **45 checks**: physical/copied/null/hidden sources, already-marked physical Reactor, six exact-menu guard mutations, and chains of 1, 3 and 128 copied effects. It verifies three gems per copied effect, no copier banishment, exact submission counts, no extra wrapper decisions, and all actual mode/gem events.

The copied-Reactor test was also run against the unchanged V6 adapter copy and failed at `Every copied mode takes free extra gem`, confirming that the regression distinguishes the new automatic behavior. Final verification used a forced `dotnet build -t:Rebuild`: Windows-mounted source timestamps lagged the Linux temporary output timestamps enough for incremental builds to reuse a stale test binary. The forced build produced the stated 45-check result.
