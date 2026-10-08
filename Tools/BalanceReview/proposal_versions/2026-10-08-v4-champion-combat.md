# Champion combat proposal — 2026-10-08

Status: **patch design for review; no gameplay implementation**. The requested change is part of the Duel balance patch. Existing non-Duel rules should not be silently changed while implementing it.

## Verified official baseline

Stoneblade's current [rulebook archive](https://stoneblade.com/pages/rules) links its [Saga Collection rulebook](https://cdn.shopify.com/s/files/1/0691/8805/9412/files/SOI_Saga-Rules-Web.pdf?v=1735833106). Pages 7–8 and 13 permit champion attacks during the play phase, interleaved with other actions. Players receive remaining power together at the end of the turn. Pages 11 and 13 require sufficient power to defeat a champion; damage does not persist across turns. They do **not explicitly resolve arbitrary partial attacks within one turn**. Page 13 allows hand shields to defend players, not champions. Revealed cards stay in hand; the FAQ permits reuse against different opponents. These rules do not define repeated mid-turn shield reactions. [Publisher rulebook, pp. 7–13 and 32](https://cdn.shopify.com/s/files/1/0691/8805/9412/files/SOI_Saga-Rules-Web.pdf?v=1735833106#page=7)

Zetta's pictured ability protects its owner and other champions from attacks. The FAQ allows Thorn Zealot's destroy effect to remove Li Hin despite attack immunity, and limits Praetorian-02's shield to its in-play zone. [Publisher rulebook, pp. 6, 32–33](https://cdn.shopify.com/s/files/1/0691/8805/9412/files/SOI_Saga-Rules-Web.pdf?v=1735833106#page=32)

Everything below that defines shield reuse, duplicate guards, or aura-damage interactions is a **proposed local clarification**, not an official FAQ ruling. Testudo Vanguard is a custom Duel card.

## Existing local implementation and why a new flow is needed

The current engine intentionally rejects `ShardsAttackChampionAction`, following the former July 20 end-turn-only decision. It includes champions in the final damage split; Testudo then defers those hits until its owner's shield reaction. Zetta can currently unlock other targets through a lethal assignment in that same split. This cannot simply be relabeled as ordered mid-turn combat. [ShardsEngine.cs](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs:448)

Current Testudo is cost 4, defense 4, exhaust for 2 gems. Its static ability copies the defender's shield reduction to each champion individually. Its protection is not one shared pool across the whole army. [ShardsDuelSet.cs](../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs:83)

The engine already stores `DamageThisTurn`, computes defense from live champion/destiny auras, and distinguishes attack permission from direct destruction. It does not currently run a general lethal-damage sweep when an aura source disappears. The local rules notes explicitly mark unrestricted within-turn partial attacks as unverified. [Damage application](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs:2050), [defense and targeting](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs:990), [local rules discussion](../ShardsData/rules-notes.md:79)

## Recommended combat rule

Proposed patch wording:

> During your play phase, spend power to attack an enemy champion. Finish that attack before taking another action. You may attack champions between playing cards, buying cards, and using abilities. Each attack must assign at least the champion's remaining defense before shields; you may spend extra power to overcome shields. Damage left on a surviving champion lasts until the end of the current turn. Your remaining power attacks players together at the end of your turn.

The minimum declaration is a conservative interpretation of the rulebook's sufficient-power wording, not a claim that an official FAQ expressly forbids every partial attack. It also keeps the UI and AI action space manageable. Testudo can leave partial **damage** even when the initial declared attack was sufficient to kill before prevention. Subsequent attacks can finish that damage. An alternative allowing arbitrary sublethal declarations is possible, but would be an explicit house rule and introduces cheap shield-probing actions.

Attacks spend power immediately. A shield reveal does not let the attacker cancel, change target, recover power, or go back to an earlier action. Overkill stays spent; it does not spill into the player. Resolve destruction and all resulting changes before returning control. Other effects cannot interrupt the attack except its defined shield reaction.

End turn should lead only to player damage. Do not leave a second unordered champion split there. An End Turn reminder may offer a return to the play phase while legal champion attacks remain, before committing player damage. If Zetta protects every remaining opponent, blocked power expires rather than forcing an impossible allocation.

## Zetta and attack restrictions

Keep ordinary Zetta's card text and stats unchanged; document its sequencing in the combat rule tile rather than adding an unchanged card to the balance list.

- A living Zetta blocks attacks on its owner and their other champions. It must actually leave play before the next protected target becomes legal. A declaration that might kill it does not unlock anything early.
- With Testudo present, Zetta receives its normal shield reaction. If it survives, protection continues. If it dies, the attacker may next attack Testudo or another legal champion, then later assign remaining power to the player.
- Direct destroy effects remain independent of power attacks: no shield reaction, no defense check, and no Zetta attack gate. Destroying Testudo first with such an effect is valid counterplay.
- Reevaluate Li Hin, Raidian and Drakonarius restrictions on every attack. Gaining mastery can unlock Raidian; removing General Decurion can unlock Drakonarius. Removing Zetta does not bypass a different champion's own immunity. Current predicates are the source for these local conditions. [Target rules](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs:1006), [destroy effect](../../Assets/Scripts/Shards/Engine/ShardsEffects.cs:735)

**Duplicate-guard recommendation, explicitly a house ruling:** if a future card or mode permits several Zettas/guards, let guards be attacked while they protect the player and non-guard champions. Any remaining guard continues that protection. Do not let two copies make each other permanently unattackable. There is currently only one Zetta in the ordinary center deck, and I found no official duplicate-Zetta ruling. If adopted, generalize the rule to a documented Guard keyword and update Zetta's text then; do not claim its printed text already specifies this exception. [Current quantity](../../Assets/Scripts/Shards/Content/ShardsBaseSet.cs:208)

## Testudo Vanguard: preserve its identity without repeat-hit exploits

Recommended card wording:

> While you control Testudo Vanguard, your shields can also protect your champions. Each shield's value can prevent that much damage to each champion during an opponent's turn. Damage prevented for one champion does not use another champion's protection. Repeated attacks do not refresh protection. Multiple Testudo Vanguards do not multiply this ability.
>
> Exhaust: gain 2 gems.

Use these detailed timing rules:

1. The attacker commits a legal target and power amount. If Testudo is currently in play and shields are not ignored, the defender may reveal any eligible, previously unrevealed hand shields before damage lands. These stay in hand. The defender can decline or reveal additional cards on a later attack, but cannot add prevention retroactively.
2. Revealing a physical shield card once establishes its reveal-time value for the current opponent's turn. It provides a **separate allowance for each protected champion and for the player**. Track how much each target has already used. Re-revealing the same instance, repeated attacks, or multiple Testudos cannot replenish that allowance. The player allowance preserves protection for the final player attack even after champion allowances are spent.
3. Apply already revealed prevention automatically; ask again only if there are additional eligible hand shields to reveal. Do not automatically reveal hidden cards. Once revealed, the allowance lasts for this attacking turn, even if that card later leaves the hand; this duration is an explicit part of the custom rule and makes resolution stable.
4. Testudo protects itself. If the last Testudo is destroyed, its shield permission ends immediately for subsequent champion attacks. Already prevented damage stays prevented. A later Testudo cannot reset used allowances on surviving targets.
5. Passive shield sources, including Praetorian-02 in play and qualifying Datic Robes in discard, use the same per-source/per-target accounting. Their unused protection is available only while the source is in its required zone and its condition holds. Losing a source never converts earlier prevention into damage. Reentering its zone does not erase that source's recorded use this turn. Check dynamic values live; lowering a value reduces future availability only. Consume passive allowances before revealed hand allowances in deterministic source order, so source accounting does not introduce another choice dialog. [Current shield sources](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs:1287), [shield modifiers](../../Assets/Scripts/Shards/Engine/ShardsEngine.cs:890)
6. Ru Bo Vai's active shield-ignore flag bypasses hand and passive allowances for attacks made after activation, including Testudo's copied protection. It does not undo prevention from earlier attacks. Ordinary defense and attack restrictions still apply. [Ru Bo Vai Duel](../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs:368)
7. Clear shield-use records at the end of this attacking turn, separately for each defender. Use physical instance identities, not definition IDs. A second copy of the same shield is a different source. A later opponent's turn gets fresh allowances.

Example: Testudo has defense 4 and its owner reveals Shield 3 against a 4-power attack. The shield prevents 3 and Testudo takes 1 damage. Its remaining defense is now 3 and its allowance from that shield is exhausted. A later 3-power attack destroys it; the defender may still reveal a different unused shield. Destroying it disables shield protection for other champions immediately, while the player can still use their separate Shield 3 allowance at turn end.

This preserves today's per-champion protection amount while making attack order meaningful. A single pool shared by the army would be a substantial extra nerf. Fully restoring the shield amount for every individual attack would instead let repeated attacks generate unlimited prevention. Neither is recommended for this patch.

## Defense auras and destruction order

Recommended local clarification: damage is marked against the champion's **current** effective defense. After removing a champion or changing an aura condition, recompute remaining defenses and destroy every champion whose existing damage is now lethal, repeating until stable. Process a simultaneous lethal group as a group before evaluating the next group; do not make it depend on collection iteration order. Destroy effects that target all champions likewise select and remove that batch coherently.

For example, a champion with base defense 4 and Ferrata's +2 has 5 marked damage. Removing Ferrata lowers its defense to 4, so it dies immediately. Lost shield protection does not have this effect: prevention has already reduced the damage that was marked. Allegiance counts owned cards across zones, so moving Ferrata to discard removes **its aura source** but does not itself subtract an owned Homodeus card from the deck-composition count. [Ferrata aura](../../Assets/Scripts/Shards/Content/ShardsDuelSet.cs:201)

## UI, AI and acceptance checks for later implementation

Use a champion action with a default **Attack for N** based on public remaining defense, an editable amount for shield overpayment, and a visible power balance. Show known remaining shield allowance separately from defense. Unrevealed opponent hand shields must never influence the displayed guaranteed-kill hint or AI observation. Log committed power, shield source reveals, prevention, damage and destruction in order. Recompute legal targets and halos after every resolved attack.

Required focused regressions before any new training:

- Attack, play a card, attack again; insufficient declarations rejected; excess power spent exactly once; no cancelling after a reveal; no champion damage carried into the next turn.
- Zetta survives shields; Zetta dies then another champion becomes legal; direct destruction bypasses protection; optional duplicate-guard rule; no legal face target at end turn.
- With the same sources active and the same shields revealed before damage, Testudo's total allowance is unchanged whether legal attack power arrives across successive attacks or together; no allowance recharge from repeated reveal, multiple Testudos or source-zone cycling.
- Distinct copies of the same shield both work; unrevealed shields can be revealed later; previously marked damage is never healed by a new reveal; champion and face allowances are independent; each opponent's turn resets use.
- Destroy Testudo first, destroy Praetorian-02 first, remove a discard shield, reduce mastery, remove a defense aura, and activate Ru Bo Vai after an earlier blocked attack.
- All shield decisions resume the single-pending-input pump safely; queued destruction triggers finish before the attacker can act again; reconnect/save/clone/search states retain damage and shield-use accounting.
- Both installed AIs and the training host expose every legal new target/amount, understand ordered champion removal, and observe only public shield information. Existing end-turn split encoders cannot be reused unchanged.
- Deterministic replay, card conservation, end-turn cleanup, infinite-power victory, hero/relic/DLC combinations and multiplayer remain covered. Replace tests that intentionally asserted the former end-turn-only rule instead of preserving contradictory expectations.

No engine tests were run for this research note because no engine code changed. Rule references and source inspection support the proposal; the new interactions remain to be implemented and validated after review.
